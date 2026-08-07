using System;
using System.Globalization;
using System.IO;
using System.Text;
using RealtimeTranslator.Core.Subtitles;

namespace RealtimeTranslator.Platform.Subtitles;

public enum SubtitleTranscriptAppendResult
{
    Appended,
    SkippedDuplicate,
    SkippedEmpty,
    Capped,
    Failed,
}

/// <summary>確定字幕ペアをローカルファイルへ追記する。件数に比例するメモリは持たない。</summary>
public sealed class SubtitleTranscriptStore
{
    public const int MaxFileBytes = SubtitleTranscriptLimits.MaxFileBytes;
    public const string SizeLimitBanner = SubtitleTranscriptLimits.SizeLimitBanner;
    public const string WriteFailureBanner = SubtitleTranscriptLimits.WriteFailureBanner;

    private readonly string _filePath;
    private readonly Func<DateTimeOffset> _now;
    private readonly int _maxFileBytes;
    private readonly object _sync = new();

    private string? _lastSource;
    private string? _lastTranslation;
    private bool _announcedSizeLimit;
    private long? _cachedByteCount;

    public SubtitleTranscriptStore(
        string? filePath = null,
        Func<DateTimeOffset>? now = null,
        int maxFileBytes = SubtitleTranscriptLimits.MaxFileBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileBytes);
        _filePath = filePath ?? DefaultFilePath();
        _now = now ?? (() => DateTimeOffset.Now);
        _maxFileBytes = maxFileBytes;
    }

    public string FilePath => _filePath;

    public bool HasEntries
    {
        get
        {
            lock (_sync)
            {
                return FileByteCountLocked() > 0;
            }
        }
    }

    public static string DefaultFilePath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RealtimeTranslator",
            "transcripts");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "session.txt");
    }

    public static string DefaultExportFileName(DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.Now;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"subtitles-{timestamp:yyyyMMdd-HHmmss}.txt");
    }

    public SubtitleTranscriptAppendResult MarkSessionStart()
    {
        lock (_sync)
        {
            // 新セッションでは直前セッション末尾との連続重複判定を切る。
            _lastSource = null;
            _lastTranslation = null;
            var timestamp = SubtitleTranscriptFormatter.FormatTimestamp(_now());
            var chunk = SubtitleTranscriptFormatter.FormatSessionStart(timestamp);
            return AppendChunkLocked(chunk, updateLastPair: false, source: null, translation: null);
        }
    }

    public SubtitleTranscriptAppendResult AppendEntry(string sourceText, string translatedText)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(translatedText);

        lock (_sync)
        {
            var source = sourceText.Trim();
            var translation = translatedText.Trim();
            if (source.Length == 0 || translation.Length == 0)
            {
                return SubtitleTranscriptAppendResult.SkippedEmpty;
            }

            if (sourceText == _lastSource && translatedText == _lastTranslation)
            {
                return SubtitleTranscriptAppendResult.SkippedDuplicate;
            }

            var timestamp = SubtitleTranscriptFormatter.FormatTimestamp(_now());
            // 記録は表示クリップ前の原文・訳文そのものを残す。
            var chunk = SubtitleTranscriptFormatter.FormatEntry(timestamp, sourceText, translatedText);
            return AppendChunkLocked(chunk, updateLastPair: true, sourceText, translatedText);
        }
    }

    public void ExportCopy(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        lock (_sync)
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(_filePath))
            {
                File.WriteAllText(destinationPath, string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return;
            }

            File.Copy(_filePath, destinationPath, overwrite: true);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_filePath, string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _lastSource = null;
            _lastTranslation = null;
            _announcedSizeLimit = false;
            _cachedByteCount = 0;
        }
    }

    private SubtitleTranscriptAppendResult AppendChunkLocked(
        string chunk,
        bool updateLastPair,
        string? source,
        string? translation)
    {
        var chunkBytes = Encoding.UTF8.GetByteCount(chunk);
        var currentBytes = FileByteCountLocked();
        if (currentBytes >= _maxFileBytes || currentBytes + chunkBytes > _maxFileBytes)
        {
            // macOS 実装と同じく初回/以降を区別してフラグを立てる（呼び出し側の一度だけ案内と併用）。
            if (_announcedSizeLimit)
            {
                return SubtitleTranscriptAppendResult.Capped;
            }

            _announcedSizeLimit = true;
            return SubtitleTranscriptAppendResult.Capped;
        }

        try
        {
            EnsureFileExistsLocked();
            using var stream = new FileStream(
                _filePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read);
            var payload = Encoding.UTF8.GetBytes(chunk);
            stream.Write(payload, 0, payload.Length);
            stream.Flush(flushToDisk: true);
            _cachedByteCount = currentBytes + chunkBytes;
            if (updateLastPair)
            {
                _lastSource = source;
                _lastTranslation = translation;
            }

            return SubtitleTranscriptAppendResult.Appended;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 失敗したペアも記憶し、同一再試行で失敗通知を連発しない。
            if (updateLastPair)
            {
                _lastSource = source;
                _lastTranslation = translation;
            }

            return SubtitleTranscriptAppendResult.Failed;
        }
    }

    private void EnsureFileExistsLocked()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(_filePath))
        {
            File.WriteAllText(_filePath, string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _cachedByteCount = 0;
        }
    }

    private long FileByteCountLocked()
    {
        if (_cachedByteCount is { } cached)
        {
            return cached;
        }

        if (!File.Exists(_filePath))
        {
            _cachedByteCount = 0;
            return 0;
        }

        var size = new FileInfo(_filePath).Length;
        _cachedByteCount = size;
        return size;
    }
}
