using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;

namespace RealtimeTranslator.AgcBench;

/// <summary>
/// 処理済み WAV を実際の transcription endpoint へ送信し、初回 delta までの時間と
/// transcript を結果 JSON に保存する。API key は OPENAI_API_KEY 環境変数からのみ取り、
/// 端末へ transcript 本文やキーを出さない。
/// </summary>
internal static class TranscribeCommand
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(20);
    private const int FrameBytes = 4800; // 100 ms PCM16 @24 kHz mono

    public static async Task<int> RunAsync(CliOptions options)
    {
        var processedDir = options.Require("processed");
        var outDir = options.Require("out");
        var runs = options.GetInt("runs", 3);
        var variantNames = options.GetList("variants");
        if (variantNames.Count == 0)
        {
            variantNames = GainVariants.AllNames;
        }

        var clipFilter = options.GetList("clips");
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new CliUsageException("OPENAI_API_KEY 環境変数が未設定です (transcribe は実 API を使います)。");
        }

        var corpus = Corpus.Load(processedDir);
        var summary =
            JsonSerializer.Deserialize<ProcessSummary>(
                File.ReadAllText(Path.Combine(processedDir, "process-summary.json")),
                Json.Options
            ) ?? new ProcessSummary();
        var explicitVariants = options.Get("variants") is not null;
        var pairs = SelectPairs(corpus, summary, clipFilter, variantNames, explicitVariants);
        Directory.CreateDirectory(outDir);
        var safetyIdentifier = SafetyIdentifier();
        foreach (var (clip, name) in pairs)
        {
            var wavPath = Path.Combine(processedDir, $"{clip.Id}.{name}.wav");
            var pcm = ReadPcm16(wavPath);
            for (var run = 1; run <= runs; run += 1)
            {
                var result = await TranscribeOnceAsync(clip, name, run, pcm, apiKey, safetyIdentifier)
                    .ConfigureAwait(false);
                var resultPath = Path.Combine(outDir, $"{clip.Id}.{name}.run{run}.json");
                await File.WriteAllTextAsync(
                        resultPath,
                        JsonSerializer.Serialize(result, Json.Options) + "\n"
                    )
                    .ConfigureAwait(false);
                Console.WriteLine(
                    $"transcribed {clip.Id}.{name}.run{run}: firstDeltaMs={(result.FirstDeltaMs?.ToString(CultureInfo.InvariantCulture) ?? "-")}"
                );
            }
        }

        return 0;
    }

    /// <summary>
    /// 送信対象の (clip, variant) を process-summary.json の実績から決める。
    /// 明示 --variants で欠けたペアは usage error、既定では skip して stdout に出す。
    /// --clips に corpus 外の id があれば usage error。
    /// </summary>
    internal static List<(CorpusClip Clip, string Variant)> SelectPairs(
        Corpus corpus,
        ProcessSummary summary,
        IReadOnlyList<string> clipFilter,
        IReadOnlyList<string> variantNames,
        bool explicitVariants
    )
    {
        var clips = new List<CorpusClip>();
        var corpusIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var clip in corpus.Clips)
        {
            corpusIds.Add(clip.Id);
        }

        if (clipFilter.Count == 0)
        {
            clips.AddRange(corpus.Clips);
        }
        else
        {
            var unknown = new List<string>();
            foreach (var id in clipFilter)
            {
                var clip = corpus.Clips.Find(c => c.Id == id);
                if (clip is null)
                {
                    unknown.Add(id);
                }
                else
                {
                    clips.Add(clip);
                }
            }

            if (unknown.Count > 0)
            {
                throw new CliUsageException(
                    $"unknown --clips id(s): {string.Join(",", unknown)} (corpus: {string.Join(",", corpusIds)})"
                );
            }
        }

        var available = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in summary.Results)
        {
            available.Add($"{entry.Clip}|{entry.Variant}");
        }

        var pairs = new List<(CorpusClip, string)>();
        var missing = new List<string>();
        foreach (var clip in clips)
        {
            foreach (var name in variantNames)
            {
                if (available.Contains($"{clip.Id}|{name}"))
                {
                    pairs.Add((clip, name));
                }
                else if (explicitVariants)
                {
                    missing.Add($"{clip.Id}.{name}");
                }
                else
                {
                    Console.WriteLine($"skipped {clip.Id}.{name}: not in process-summary.json");
                }
            }
        }

        if (missing.Count > 0)
        {
            throw new CliUsageException(
                $"process-summary.json に無いペアがあります: {string.Join(",", missing)}"
            );
        }

        return pairs;
    }

    private static string SafetyIdentifier()
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("RealtimeTranslator.AgcBench"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] ReadPcm16(string wavPath)
    {
        // 処理済み WAV の data チャンクをそのまま切り出す (44 byte header を書いた前提)。
        var bytes = File.ReadAllBytes(wavPath);
        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var size = (int)BitConverter.ToUInt32(bytes, offset + 4);
            if (bytes[offset] == 'd' && bytes[offset + 1] == 'a' && bytes[offset + 2] == 't' && bytes[offset + 3] == 'a')
            {
                var data = new byte[size];
                Array.Copy(bytes, offset + 8, data, 0, size);
                return data;
            }

            offset += 8 + size + (size % 2);
        }

        throw new InvalidDataException($"{wavPath}: data チャンクが見つかりません。");
    }

    private static async Task<TranscribeResult> TranscribeOnceAsync(
        CorpusClip clip,
        string variant,
        int run,
        byte[] pcm,
        string apiKey,
        string safetyIdentifier
    )
    {
        var result = new TranscribeResult
        {
            Clip = clip.Id,
            Variant = variant,
            Run = run,
        };
        var connection = new RealtimeSourceTranscriptionConnection(
            new ClientWebSocketTransport(),
            safetyIdentifier
        );
        Task? drain = null;
        var transcript = new StringBuilder();
        long? firstDeltaMs = null;
        string? streamError = null;
        var firstSendAt = 0L;
        var sentMs = 0L;
        var connectStart = Stopwatch.GetTimestamp();
        try
        {
            await connection
                .StartAsync(
                    apiKey,
                    RealtimeSessionTuning.Default.ForPair(clip.LanguagePair),
                    clip.LanguagePair
                )
                .ConfigureAwait(false);
            result.ConnectMs = ElapsedMs(connectStart);

            drain = Task.Run(
                async () =>
                {
                    await foreach (
                        var streamEvent in connection.Events.ReadAllAsync().ConfigureAwait(false)
                    )
                    {
                        switch (streamEvent.Event)
                        {
                            case RealtimeTranslationServerEvent.InputTranscriptDelta delta:
                                firstDeltaMs ??= ElapsedMs(firstSendAt);
                                transcript.Append(delta.Delta);
                                break;
                            case RealtimeTranslationServerEvent.ServerError error:
                                streamError ??= error.Message;
                                break;
                            case RealtimeTranslationServerEvent.InputTranscriptFailed failed:
                                streamError ??= failed.Code ?? failed.ErrorType ?? "transcription failed";
                                break;
                        }
                    }
                }
            );

            var frameCount = pcm.Length / FrameBytes;
            // 0 フレームなら送信も計測もしない (firstDeltaMs/sentMs は null/0 のまま)。
            firstSendAt = frameCount == 0 ? 0L : Stopwatch.GetTimestamp();
            for (var frame = 0; frame < frameCount; frame += 1)
            {
                await connection
                    .AppendAudioFrameAsync(pcm.AsMemory(frame * FrameBytes, FrameBytes))
                    .ConfigureAwait(false);
                var target = (frame + 1) * 100;
                var elapsed = ElapsedMs(firstSendAt);
                if (elapsed < target)
                {
                    await Task.Delay((int)(target - elapsed)).ConfigureAwait(false);
                }
            }

            sentMs = frameCount == 0 ? 0 : ElapsedMs(firstSendAt);
            using var closeTimeout = new CancellationTokenSource(CloseTimeout);
            try
            {
                await connection.CloseGracefullyAsync(closeTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                streamError ??= "close timeout";
            }
            catch (Exception exception)
            {
                streamError ??= exception.GetType().Name;
            }
        }
        catch (Exception exception)
        {
            streamError ??= $"{exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            // Dispose は Events チャンネルを TryComplete するので、先に閉じてから drain を待つ。
            connection.Dispose();
            if (drain is not null)
            {
                var finished = await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(5)))
                    .ConfigureAwait(false);
                if (!ReferenceEquals(finished, drain))
                {
                    streamError ??= "drain timeout";
                }
            }
        }

        result.Transcript = transcript.ToString();
        result.FirstDeltaMs = firstDeltaMs;
        result.FirstDeltaFromOnsetMs =
            firstDeltaMs is { } delta ? delta - clip.SpeechOnsetMs : null;
        result.SentMs = sentMs;
        result.Error = streamError;
        return result;
    }

    private static long ElapsedMs(long startTimestamp) =>
        (long)((Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency);
}

internal sealed record TranscribeResult
{
    public required string Clip { get; init; }
    public required string Variant { get; init; }
    public int Run { get; init; }
    public string Transcript { get; set; } = "";
    public long? FirstDeltaMs { get; set; }
    public long? FirstDeltaFromOnsetMs { get; set; }
    public long SentMs { get; set; }
    public string? Error { get; set; }

    /// <summary>StartAsync (connect + handshake) にかかった時間。診断用。</summary>
    public long? ConnectMs { get; set; }
}
