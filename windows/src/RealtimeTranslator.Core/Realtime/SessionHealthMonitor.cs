using System;
using System.Collections.Generic;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>受信停止監視の検知種別。契約は shared/fixtures/v1/health.json。</summary>
public enum SessionHealthDetectionKind
{
    CaptureStalled,
    SendStalled,
    ReceiveStalled,
    SourceStalled,
    TranslationStalled,
    ExpiryNear,
    Expired,
}

public enum SessionHealthPhase
{
    Idle,
    CatchingUp,
    Silence,
    AwaitingLanguageDetection,
    Active,
}

/// <summary>セッションループ終了理由の自前分類。サーバー文言や生のエラー文字列は保持しない。</summary>
public enum SessionTerminationKind
{
    MissingApiKey,
    NotConnected,
    InvalidMessage,
    AuthenticationFailed,
    FatalServerError,
    RecoverableTransportFailure,
    RecoverableServerError,
    ReceiveOverflow,
    SessionUpdateTimeout,
    CloseTimeout,
    Cancelled,
    /// <summary>再接続 budget の枯渇（RealtimeTranslationErrorKind にはない終了分類）。</summary>
    ReconnectBudgetExhausted,
    /// <summary>ユーザーによる停止。</summary>
    UserStopped,
    /// <summary>RealtimeTranslationErrorKind 以外の失敗（デバイス・未知のエラー）。</summary>
    Other,
}

/// <summary>RealtimeTranslationErrorKind → SessionTerminationKind の明示対応。</summary>
public static class SessionTerminationKindMapping
{
    public static SessionTerminationKind FromErrorKind(RealtimeTranslationErrorKind kind) => kind switch
    {
        RealtimeTranslationErrorKind.MissingApiKey => SessionTerminationKind.MissingApiKey,
        RealtimeTranslationErrorKind.NotConnected => SessionTerminationKind.NotConnected,
        RealtimeTranslationErrorKind.InvalidMessage => SessionTerminationKind.InvalidMessage,
        RealtimeTranslationErrorKind.AuthenticationFailed => SessionTerminationKind.AuthenticationFailed,
        RealtimeTranslationErrorKind.FatalServerError => SessionTerminationKind.FatalServerError,
        RealtimeTranslationErrorKind.RecoverableTransportFailure =>
            SessionTerminationKind.RecoverableTransportFailure,
        RealtimeTranslationErrorKind.RecoverableServerError => SessionTerminationKind.RecoverableServerError,
        RealtimeTranslationErrorKind.ReceiveOverflow => SessionTerminationKind.ReceiveOverflow,
        RealtimeTranslationErrorKind.SessionUpdateTimeout => SessionTerminationKind.SessionUpdateTimeout,
        RealtimeTranslationErrorKind.CloseTimeout => SessionTerminationKind.CloseTimeout,
        RealtimeTranslationErrorKind.Cancelled => SessionTerminationKind.Cancelled,
        _ => SessionTerminationKind.Other,
    };

    /// <summary>RealtimeTranslationException 以外は Other。生のメッセージは使わない。</summary>
    public static SessionTerminationKind FromException(Exception error) =>
        error is RealtimeTranslationException translationError
            ? FromErrorKind(translationError.Kind)
            : SessionTerminationKind.Other;
}

public sealed record SessionHealthDetection(
    SessionHealthDetectionKind Kind,
    int Generation,
    int Epoch,
    RealtimeTranslationLane? Lane,
    TimeSpan Elapsed,
    TimeSpan? Remaining)
{
    /// <summary>数値と enum 名だけの content-free な表現（ログ用）。</summary>
    public override string ToString() =>
        $"kind={Kind} generation={Generation} epoch={Epoch} lane={(Lane?.ToLogString() ?? "-")} "
            + $"elapsedMs={(long)Elapsed.TotalMilliseconds} "
            + $"remainingMs={(Remaining is { } remaining ? ((long)remaining.TotalMilliseconds).ToString() : "-")}";
}

public sealed record SessionTerminationDiagnostic(
    SessionTerminationKind Kind,
    TimeSpan ConnectionDuration,
    int Generation,
    int Epoch)
{
    public override string ToString() =>
        $"kind={Kind} durationMs={(long)ConnectionDuration.TotalMilliseconds} "
            + $"generation={Generation} epoch={Epoch}";
}

public sealed record SessionHealthSnapshot
{
    public int Generation { get; init; }
    public int Epoch { get; init; }
    public SessionHealthPhase Phase { get; init; }
    public TimeSpan ConnectionElapsed { get; init; }
    public TimeSpan? SinceCapture { get; init; }
    public TimeSpan? SinceSendSuccess { get; init; }
    public TimeSpan? CaptureToSendGap { get; init; }
    public TimeSpan? SinceReceive { get; init; }
    public TimeSpan? SinceSourceProgress { get; init; }
    public TimeSpan? SinceSelectedTranslationProgress { get; init; }
    public RealtimeTranslationLane? SelectedLane { get; init; }
    public IReadOnlyDictionary<RealtimeTranslationLane, TimeSpan?> LaneExpiryRemaining { get; init; }
        = new Dictionary<RealtimeTranslationLane, TimeSpan?>();
    public int SourceProgressCount { get; init; }
    public int TranslationProgressCount { get; init; }

    public override string ToString()
    {
        var lane = SelectedLane?.ToLogString() ?? "-";
        var sinceReceive = SinceReceive is { } value ? ((long)value.TotalMilliseconds).ToString() : "-";
        var sinceSource = SinceSourceProgress is { } value2 ? ((long)value2.TotalMilliseconds).ToString() : "-";
        return $"phase={Phase} generation={Generation} epoch={Epoch} "
            + $"elapsedMs={(long)ConnectionElapsed.TotalMilliseconds} "
            + $"lane={lane} sinceReceiveMs={sinceReceive} sinceSourceProgressMs={sinceSource} "
            + $"sourceProgressCount={SourceProgressCount} translationProgressCount={TranslationProgressCount}";
    }
}

/// <summary>shared/fixtures/v1/health.json `thresholds` が既定値の正本。</summary>
public sealed record SessionHealthThresholds
{
    public TimeSpan CaptureStall { get; init; } = TimeSpan.FromMilliseconds(3_000);
    public TimeSpan SendStall { get; init; } = TimeSpan.FromMilliseconds(10_000);
    public TimeSpan ReceiveStall { get; init; } = TimeSpan.FromMilliseconds(20_000);
    public TimeSpan SourceStall { get; init; } = TimeSpan.FromMilliseconds(20_000);
    public TimeSpan TranslationStall { get; init; } = TimeSpan.FromMilliseconds(15_000);
    public TimeSpan Silence { get; init; } = TimeSpan.FromMilliseconds(5_000);
    public TimeSpan ConnectGrace { get; init; } = TimeSpan.FromMilliseconds(10_000);
    public TimeSpan ExpiryNear { get; init; } = TimeSpan.FromMilliseconds(120_000);
    public double AudioActivityPeakFloor { get; init; } = 0.005;
}

/// <summary>lane の content-free な表示名。</summary>
internal static class RealtimeTranslationLaneHealthExtensions
{
    public static string ToLogString(this RealtimeTranslationLane lane) =>
        lane.IsSource ? "source" : lane.Target?.ToWireValue() ?? "-";
}

/// <summary>
/// 時計を持たない純粋ロジックの受信停止監視。now は呼び出し側が与える単調時計（TimeSpan）。
/// 検知に対して再接続・lane 変更は行わない（診断のみ）。
/// </summary>
public sealed class SessionHealthMonitor
{
    private struct EmittedKey : IEquatable<EmittedKey>
    {
        public SessionHealthDetectionKind Kind;
        public RealtimeTranslationLane? Lane;

        public bool Equals(EmittedKey other) => Kind == other.Kind && Lane == other.Lane;

        public override bool Equals(object? obj) => obj is EmittedKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Kind, Lane);
    }

    private readonly SessionHealthThresholds _thresholds;

    private bool _active;
    private int _generation;
    private int _epoch;
    private bool _isRecovery;
    private TimeSpan _connectedAt;

    private TimeSpan? _lastCapture;
    private TimeSpan? _lastSendSuccess;
    private TimeSpan? _lastReceive;
    private TimeSpan? _lastSourceProgress;
    private readonly Dictionary<RealtimeTranslationLane, TimeSpan> _lastTranslationProgress = new();
    private TimeSpan? _lastAudioActivityAt;
    private TimeSpan? _firstActivityAfterLastReceive;
    private TimeSpan? _firstActivityAfterLastSourceProgress;
    private RealtimeTranslationLane? _selectedLane;
    private TimeSpan? _selectedAt;
    private readonly Dictionary<RealtimeTranslationLane, TimeSpan> _expiryDeadlines = new();
    private int _sourceProgressCount;
    private int _translationProgressCount;
    private readonly HashSet<EmittedKey> _emitted = new();

    public SessionHealthMonitor(SessionHealthThresholds? thresholds = null)
    {
        _thresholds = thresholds ?? new SessionHealthThresholds();
    }

    public SessionHealthThresholds Thresholds => _thresholds;

    private TimeSpan GraceEnd => _connectedAt + _thresholds.ConnectGrace;

    /// <summary>
    /// 全 timestamp・selectedLane・expiry・emitted をクリアする
    /// （再接続で言語判定がリセットされる既存契約に合わせる）。
    /// </summary>
    public void BeginGeneration(int generation, int epoch, bool isRecovery, TimeSpan now)
    {
        _active = true;
        _generation = generation;
        _epoch = epoch;
        _isRecovery = isRecovery;
        _connectedAt = now;
        _lastCapture = null;
        _lastSendSuccess = null;
        _lastReceive = null;
        _lastSourceProgress = null;
        _lastTranslationProgress.Clear();
        _lastAudioActivityAt = null;
        _firstActivityAfterLastReceive = null;
        _firstActivityAfterLastSourceProgress = null;
        _selectedLane = null;
        _selectedAt = null;
        _expiryDeadlines.Clear();
        _sourceProgressCount = 0;
        _translationProgressCount = 0;
        _emitted.Clear();
    }

    public void RecordCapture(TimeSpan now, bool hasAudioActivity)
    {
        _lastCapture = now;
        if (!hasAudioActivity)
        {
            return;
        }

        _lastAudioActivityAt = now;
        // grace 中の活動は graceEnd に置く。
        var activityAt = now > GraceEnd ? now : GraceEnd;
        _firstActivityAfterLastReceive ??= activityAt;
        _firstActivityAfterLastSourceProgress ??= activityAt;
    }

    public void RecordSendSuccess(TimeSpan now) => _lastSendSuccess = now;

    public void RecordReceive(RealtimeTranslationLane lane, TimeSpan now)
    {
        _ = lane;
        _lastReceive = _lastReceive is { } last && last > now ? last : now;
        _firstActivityAfterLastReceive = null;
    }

    public void RecordSourceProgress(TimeSpan now)
    {
        _lastSourceProgress = now;
        _sourceProgressCount += 1;
        _firstActivityAfterLastSourceProgress = null;
    }

    public void RecordTranslationProgress(RealtimeTranslationLane lane, TimeSpan now)
    {
        _lastTranslationProgress[lane] = now;
        _translationProgressCount += 1;
    }

    public void SetSelectedLane(RealtimeTranslationLane? lane, TimeSpan now)
    {
        _selectedLane = lane;
        _selectedAt = now;
    }

    /// <summary>remaining null = 不明（期限検知をしない）。</summary>
    public void RecordSessionExpiry(RealtimeTranslationLane lane, TimeSpan? remaining, TimeSpan now)
    {
        if (remaining is { } value)
        {
            _expiryDeadlines[lane] = now + value;
        }
        else
        {
            _expiryDeadlines.Remove(lane);
        }
    }

    public SessionTerminationDiagnostic RecordTermination(SessionTerminationKind kind, TimeSpan now) =>
        new(kind, now - _connectedAt, _generation, _epoch);

    public void EndGeneration(TimeSpan now)
    {
        _ = now;
        _active = false;
        _emitted.Clear();
    }

    /// <summary>その時点の snapshot と、この evaluate で新たに発火した検知（kind の定義順）を返す。</summary>
    public (SessionHealthSnapshot Snapshot, IReadOnlyList<SessionHealthDetection> NewDetections)
        Evaluate(TimeSpan now)
    {
        var phase = CurrentPhase(now);
        var laneExpiryRemaining = new Dictionary<RealtimeTranslationLane, TimeSpan?>();
        foreach (var (lane, deadline) in _expiryDeadlines)
        {
            laneExpiryRemaining[lane] = deadline - now;
        }

        var snapshot = new SessionHealthSnapshot
        {
            Generation = _generation,
            Epoch = _epoch,
            Phase = phase,
            ConnectionElapsed = _active ? now - _connectedAt : TimeSpan.Zero,
            SinceCapture = _lastCapture is { } capture ? now - capture : null,
            SinceSendSuccess = _lastSendSuccess is { } sent ? now - sent : null,
            CaptureToSendGap = _lastCapture is { } c && _lastSendSuccess is { } s ? s - c : null,
            SinceReceive = _lastReceive is { } received ? now - received : null,
            SinceSourceProgress = _lastSourceProgress is { } progress ? now - progress : null,
            SinceSelectedTranslationProgress =
                _selectedLane is { } selected
                && _lastTranslationProgress.TryGetValue(selected, out var lastSelected)
                    ? now - lastSelected
                    : null,
            SelectedLane = _selectedLane,
            LaneExpiryRemaining = laneExpiryRemaining,
            SourceProgressCount = _sourceProgressCount,
            TranslationProgressCount = _translationProgressCount,
        };

        if (!_active)
        {
            return (snapshot, []);
        }

        var detections = new List<SessionHealthDetection>();
        foreach (SessionHealthDetectionKind kind in Enum.GetValues<SessionHealthDetectionKind>())
        {
            switch (kind)
            {
                case SessionHealthDetectionKind.CaptureStalled:
                    if (now - (_lastCapture ?? _connectedAt) >= _thresholds.CaptureStall)
                    {
                        Emit(detections, kind, null, now);
                    }

                    break;

                case SessionHealthDetectionKind.SendStalled:
                    if (now >= GraceEnd
                        && now - (_lastCapture ?? _connectedAt) < _thresholds.CaptureStall
                        && now - (_lastSendSuccess ?? _connectedAt) >= _thresholds.SendStall)
                    {
                        Emit(detections, kind, null, now);
                    }

                    break;

                case SessionHealthDetectionKind.ReceiveStalled:
                    if (now >= GraceEnd
                        && _firstActivityAfterLastReceive is { } firstAfterReceive
                        && now - firstAfterReceive >= _thresholds.ReceiveStall)
                    {
                        Emit(detections, kind, null, now);
                    }

                    break;

                case SessionHealthDetectionKind.SourceStalled:
                {
                    var receiveStalled = _firstActivityAfterLastReceive is { } firstReceive
                        && now - firstReceive >= _thresholds.ReceiveStall;
                    if (now >= GraceEnd
                        && !receiveStalled
                        && _firstActivityAfterLastSourceProgress is { } firstSource
                        && now - firstSource >= _thresholds.SourceStall)
                    {
                        Emit(detections, kind, null, now);
                    }

                    break;
                }

                case SessionHealthDetectionKind.TranslationStalled:
                {
                    if (now < GraceEnd
                        || _selectedLane is not { } selectedLane
                        || _selectedAt is not { } selectedAt
                        || _lastSourceProgress is not { } lastSource
                        || lastSource < selectedAt
                        || now - lastSource < _thresholds.TranslationStall)
                    {
                        break;
                    }

                    // selectedAt より古い lane 進捗は無視（null 扱い）。
                    var lastTranslation =
                        _lastTranslationProgress.TryGetValue(selectedLane, out var lastTx)
                        && lastTx >= selectedAt
                            ? lastTx
                            : (TimeSpan?)null;
                    if (lastTranslation is null
                        || lastSource - lastTranslation.Value >= _thresholds.TranslationStall)
                    {
                        Emit(detections, kind, selectedLane, now);
                    }

                    break;
                }

                case SessionHealthDetectionKind.ExpiryNear:
                case SessionHealthDetectionKind.Expired:
                    foreach (var (lane, deadline) in SortedExpiryDeadlines())
                    {
                        var remaining = deadline - now;
                        if (kind == SessionHealthDetectionKind.ExpiryNear
                            && remaining > TimeSpan.Zero
                            && remaining <= _thresholds.ExpiryNear)
                        {
                            Emit(detections, kind, lane, now, remaining);
                        }
                        else if (kind == SessionHealthDetectionKind.Expired
                            && remaining <= TimeSpan.Zero)
                        {
                            Emit(detections, kind, lane, now, remaining);
                        }
                    }

                    break;
            }
        }

        return (snapshot, detections);
    }

    private IEnumerable<KeyValuePair<RealtimeTranslationLane, TimeSpan>> SortedExpiryDeadlines()
    {
        var lanes = new List<RealtimeTranslationLane>(_expiryDeadlines.Keys);
        lanes.Sort(static (a, b) => LaneOrder(a).CompareTo(LaneOrder(b)));
        foreach (var lane in lanes)
        {
            yield return KeyValuePair.Create(lane, _expiryDeadlines[lane]);
        }
    }

    /// <summary>expiry 検知の lane 反復順を固定する（検知列は kind 順→lane 順）。</summary>
    private static int LaneOrder(RealtimeTranslationLane lane)
    {
        if (lane.IsSource)
        {
            return 0;
        }

        return lane.Target switch
        {
            RealtimeTranslationOutputLanguage.English => 1,
            RealtimeTranslationOutputLanguage.Japanese => 2,
            RealtimeTranslationOutputLanguage.Spanish => 3,
            _ => 4,
        };
    }

    private void Emit(
        List<SessionHealthDetection> detections,
        SessionHealthDetectionKind kind,
        RealtimeTranslationLane? lane,
        TimeSpan now,
        TimeSpan? remaining = null)
    {
        if (!_emitted.Add(new EmittedKey { Kind = kind, Lane = lane }))
        {
            return;
        }

        detections.Add(new SessionHealthDetection(
            kind, _generation, _epoch, lane, now - _connectedAt, remaining));
    }

    private SessionHealthPhase CurrentPhase(TimeSpan now)
    {
        if (!_active)
        {
            return SessionHealthPhase.Idle;
        }

        if (now < GraceEnd && _isRecovery && _lastSourceProgress is null)
        {
            return SessionHealthPhase.CatchingUp;
        }

        if (_lastAudioActivityAt is null || now - _lastAudioActivityAt.Value >= _thresholds.Silence)
        {
            return SessionHealthPhase.Silence;
        }

        if (_selectedLane is null)
        {
            return SessionHealthPhase.AwaitingLanguageDetection;
        }

        return SessionHealthPhase.Active;
    }
}
