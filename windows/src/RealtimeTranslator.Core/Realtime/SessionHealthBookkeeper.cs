using System;
using System.Collections.Generic;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>
/// 受信停止監視の帳簿。<see cref="SessionHealthMonitor"/> と接続試行・世代の診断窓を束ねる。
/// <c>InterpretationSession</c> の <c>_sync</c> ロック下だけで触るコンポーネント。
/// 独自ロックは持たない（ロック順序問題を避ける）。
/// </summary>
internal sealed class SessionHealthBookkeeper
{
    /// <summary>受信監視で数える対象 lane（source + 全 target）。</summary>
    private static readonly RealtimeTranslationLane[] Lanes =
    [
        RealtimeTranslationLane.Source,
        RealtimeTranslationLane.Translation(RealtimeTranslationOutputLanguage.English),
        RealtimeTranslationLane.Translation(RealtimeTranslationOutputLanguage.Japanese),
        RealtimeTranslationLane.Translation(RealtimeTranslationOutputLanguage.Spanish),
    ];

    private readonly SessionHealthMonitor _monitor;
    private readonly Dictionary<RealtimeTranslationLane, int> _receiveCounts = new();
    private readonly Func<TimeSpan> _nowProvider;
    private readonly Func<long> _wallNowUnixSecondsProvider;

    /// <summary>世代未開始（pre-Listening）の終了診断用に、接続試行の開始時刻と意図 epoch を保持する。</summary>
    private TimeSpan? _attemptStart;
    private int _attemptEpoch;
    private int _attemptGeneration;
    private bool _generationEnded = true;
    private int _connectionCountInGeneration;

    public SessionHealthBookkeeper(
        SessionHealthThresholds? thresholds,
        Func<TimeSpan> nowProvider,
        Func<long> wallNowUnixSecondsProvider
    )
    {
        ArgumentNullException.ThrowIfNull(nowProvider);
        ArgumentNullException.ThrowIfNull(wallNowUnixSecondsProvider);
        _monitor = new SessionHealthMonitor(thresholds);
        _nowProvider = nowProvider;
        _wallNowUnixSecondsProvider = wallNowUnixSecondsProvider;
    }

    /// <summary>テスト・診断用の最新 snapshot（検知には使わない）。</summary>
    public SessionHealthSnapshot? LatestSnapshot { get; private set; }

    /// <summary>テスト・診断用の最新 termination diagnostic（各試行で最大 1 件）。</summary>
    public SessionTerminationDiagnostic? LatestTermination { get; private set; }

    public SessionHealthThresholds Thresholds => _monitor.Thresholds;

    /// <summary>新しい録音世代の開始時に接続試行カウンタを戻す。</summary>
    public void BeginRecordingGeneration() => _connectionCountInGeneration = 0;

    /// <summary>
    /// 接続試行の開始を記録する。epoch は呼び出し側が <c>_sync</c> 保持前に読んだ
    /// Dual 側の予約値（RequireApiKey 失敗（missing key）も試行の終了診断へ乗せるため先に記録する）。
    /// </summary>
    public void BeginAttempt(int lifecycleGeneration, int reservedEpoch)
    {
        _connectionCountInGeneration += 1;
        _attemptStart = _nowProvider();
        _attemptEpoch = reservedEpoch;
        _attemptGeneration = lifecycleGeneration;
    }

    /// <summary>handshake 中に接続側の予約 epoch が進んだあと読み直した値で更新する。</summary>
    public void UpdateAttemptEpoch(int reservedEpoch) => _attemptEpoch = reservedEpoch;

    /// <summary>Listening 確定時に monitor 世代を開始し、受信数・期限をシードする。</summary>
    public void BeginGeneration(int generation, int epoch, EventDeliveryState deliveryState)
    {
        ArgumentNullException.ThrowIfNull(deliveryState);

        var monitorNow = _nowProvider();
        _monitor.BeginGeneration(generation, epoch, _connectionCountInGeneration > 1, monitorNow);
        _generationEnded = false;
        // handshake の受信を初回 tick で「新規受信」と誤認しないよう現数でシードする。
        _receiveCounts.Clear();
        foreach (var lane in Lanes)
        {
            _receiveCounts[lane] = deliveryState.ReceiveCount(lane);
        }

        // 世代が始まった attempt の診断窓は monitor 側へ移す。
        _attemptStart = null;
        // 期限の remaining は受信時に一度だけ壁時計で算出し、以後は単調時計で追う。
        var wallNow = _wallNowUnixSecondsProvider();
        foreach (var lane in Lanes)
        {
            var expiry = deliveryState.SessionExpiry(lane);
            _monitor.RecordSessionExpiry(
                lane,
                expiry is { } value && RealtimeSessionExpiry.RemainingSeconds(value, wallNow) is { } seconds
                    ? TimeSpan.FromSeconds(seconds)
                    : null,
                monitorNow
            );
        }
    }

    /// <summary>
    /// 各 lane の decode 受信数の差分で RecordReceive し、Evaluate を回す。
    /// 検知に対して再接続や lane 変更は行わない（診断のみ）。
    /// </summary>
    /// 戻り値は <c>_sync</c> 解放後に HealthDetected へ流す検知列。
    public IReadOnlyList<SessionHealthDetection> Tick(RealtimeEventFeed? feed)
    {
        if (feed is null)
        {
            return Array.Empty<SessionHealthDetection>();
        }

        var now = _nowProvider();
        foreach (var lane in Lanes)
        {
            var count = feed.DeliveryState.ReceiveCount(lane);
            if (count > (_receiveCounts.TryGetValue(lane, out var previous) ? previous : 0))
            {
                _monitor.RecordReceive(lane, now);
            }

            _receiveCounts[lane] = count;
        }

        var (snapshot, detections) = _monitor.Evaluate(now);
        LatestSnapshot = snapshot;
        return detections;
    }

    /// <summary>
    /// セッションループ終了・停止時の診断。kind は自前 enum のみ（生 message は渡さない）。
    /// 各世代で最初の終了経路だけを記録する。
    /// </summary>
    /// <param name="kind">終了分類。</param>
    /// <param name="reservedEpoch">
    /// handshake 中の stop では Dual 側の予約 epoch が <c>_attemptEpoch</c> より
    /// 先に進むため、呼び出し側が <c>_sync</c> 保持前に読んだ予約 epoch。
    /// </param>
    public void RecordTermination(SessionTerminationKind kind, int reservedEpoch)
    {
        var now = _nowProvider();
        if (!_generationEnded)
        {
            LatestTermination = _monitor.RecordTermination(kind, now);
            _generationEnded = true;
            _monitor.EndGeneration(now);
            LatestSnapshot = _monitor.Evaluate(now).Snapshot;
        }
        else if (_attemptStart is { } attemptStart)
        {
            // 世代未開始（pre-Listening / handshake 失敗）の終了は attempt の
            // 開始時刻・意図 epoch で記録する。monitor の stall 状態には触れない。
            var duration = now - attemptStart;
            LatestTermination = new SessionTerminationDiagnostic(
                kind,
                duration < TimeSpan.Zero ? TimeSpan.Zero : duration,
                _attemptGeneration,
                reservedEpoch
            );
        }
        else
        {
            return;
        }

        // 1 試行につき 1 件だけ。
        _attemptStart = null;
    }

    /// <summary>recoverable・正常終了を問わず接続 teardown で健康世代を閉じる。</summary>
    public void EndGeneration()
    {
        if (_generationEnded)
        {
            return;
        }

        _generationEnded = true;
        _monitor.EndGeneration(_nowProvider());
        LatestSnapshot = _monitor.Evaluate(_nowProvider()).Snapshot;
    }

    // 以下は monitor 記録の直通 forward。now はここで単調時計から読む。

    public void RecordCapture(bool hasAudioActivity) => _monitor.RecordCapture(_nowProvider(), hasAudioActivity);

    public void RecordSendStart() => _monitor.RecordSendStart(_nowProvider());

    public void RecordSendSuccess() => _monitor.RecordSendSuccess(_nowProvider());

    public void RecordSourceProgress() => _monitor.RecordSourceProgress(_nowProvider());

    public void RecordTranslationProgress(RealtimeTranslationLane lane) =>
        _monitor.RecordTranslationProgress(lane, _nowProvider());

    public void SetSelectedLane(RealtimeTranslationLane? lane) => _monitor.SetSelectedLane(lane, _nowProvider());
}
