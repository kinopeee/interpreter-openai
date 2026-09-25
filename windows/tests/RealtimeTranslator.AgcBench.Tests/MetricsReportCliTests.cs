using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RealtimeTranslator.AgcBench;
using Xunit;

namespace RealtimeTranslator.AgcBench.Tests;

public class TextMetricsTests
{
    // Given: 1 文字だけ異なる日本語文
    // When: CER を計算する
    // Then: 1 / 5 = 0.2
    [Fact]
    public void CerCountsRuneEdits() => Assert.Equal(0.2, TextMetrics.Cer("こんにちは", "こんにちわ"), 6);

    // Given: 参照 3 語に対し 1 語欠落した英語文
    // When: WER を計算する
    // Then: 1 / 3
    [Fact]
    public void WerCountsTokenEdits() => Assert.Equal(1.0 / 3.0, TextMetrics.Wer("hello big world", "hello world"), 6);

    // Given: 句読点・大小文字違い・全角/半角を含む文字列
    // When: 正規化する
    // Then: FormKC で互換分解され、句読点と記号と空白が落ち、小文字化される
    [Fact]
    public void NormalizeDropsPunctuationAndCase()
    {
        Assert.Equal("helloworld", TextMetrics.Normalize("Hello, World!", keepSpaces: false));
        Assert.Equal("abc123", TextMetrics.Normalize("ＡＢＣ　１２３", keepSpaces: false));
        Assert.Equal("ガ", TextMetrics.Normalize("ｶﾞ", keepSpaces: false));
    }

    // Given: ハイフン区切りの仮説
    // When: WER を計算する
    // Then: 句読点は空白扱いになり "hello world" と同値 (0 エラー)
    [Fact]
    public void WerTreatsPunctuationAsTokenBoundary() =>
        Assert.Equal(0.0, TextMetrics.Wer("hello world", "hello-world"), 6);

    // Given: 無音クリップへの誤字幕
    // When: falseSubtitleChars を計算する
    // Then: 空なら 0、非空なら正規化後の文字数
    [Fact]
    public void FalseSubtitleCharsCountsNormalizedLength()
    {
        Assert.Equal(0, TextMetrics.FalseSubtitleChars(""));
        Assert.Equal(0, TextMetrics.FalseSubtitleChars("… 。、"));
        Assert.Equal(5, TextMetrics.FalseSubtitleChars("Hello"));
        Assert.Equal(5, TextMetrics.FalseSubtitleChars("こんにちは"));
    }
}

public class ReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"agcbench-{Guid.NewGuid():N}");

    public ReportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string WriteProcessedDir(bool expectSilence = false)
    {
        var processed = Path.Combine(_dir, $"processed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(processed);
        new Corpus
        {
            Clips =
            [
                new CorpusClip
                {
                    Id = "clip",
                    File = "clip.wav",
                    Language = "en",
                    Pair = "en-es",
                    Reference = "hello world",
                    SpeechOnsetMs = 500,
                    ExpectSilence = expectSilence,
                },
            ],
        }.Save(processed);
        File.WriteAllText(
            Path.Combine(processed, "process-summary.json"),
            JsonSerializer.Serialize(
                new ProcessSummary
                {
                    Results =
                    [
                        new ClipVariantSummary
                        {
                            Clip = "clip",
                            Variant = "off",
                            Frames = 10,
                            MaxGain = 1f,
                            MinGain = 1f,
                            ClippedSamples = 0,
                            SpeechOutRms = 0.05,
                        },
                        new ClipVariantSummary
                        {
                            Clip = "clip",
                            Variant = "v2",
                            Frames = 10,
                            MaxGain = 5f,
                            MinGain = 1f,
                            ClippedSamples = 3,
                            SpeechOutRms = 0.2,
                        },
                    ],
                },
                Json.Options
            )
        );
        return processed;
    }

    // Given: summary と 2 件の transcribe 結果 (1 件は firstDeltaMs なし)
    // When: report を生成する
    // Then: バリアント行が描かれ、欠けた値は – になる
    [Fact]
    public void ReportRendersRowsAndMissingMarkers()
    {
        var processed = WriteProcessedDir();
        var results = Path.Combine(_dir, "results");
        Directory.CreateDirectory(results);
        File.WriteAllText(
            Path.Combine(results, "clip.v2.run1.json"),
            JsonSerializer.Serialize(
                new TranscribeResult
                {
                    Clip = "clip",
                    Variant = "v2",
                    Run = 1,
                    Transcript = "hello world",
                    FirstDeltaMs = 700,
                    FirstDeltaFromOnsetMs = 200,
                    SentMs = 1000,
                },
                Json.Options
            )
        );
        File.WriteAllText(
            Path.Combine(results, "clip.v2.run2.json"),
            JsonSerializer.Serialize(
                new TranscribeResult
                {
                    Clip = "clip",
                    Variant = "v2",
                    Run = 2,
                    Transcript = "hello word",
                    FirstDeltaMs = null,
                    SentMs = 1000,
                },
                Json.Options
            )
        );

        var reportPath = Path.Combine(_dir, "report.md");
        var options = CliOptions.Parse(["--processed", processed, "--results", results, "--out", reportPath]);
        Assert.Equal(0, ReportCommand.Run(options));

        var report = File.ReadAllText(reportPath);
        Assert.Contains("| off | 1 | 0 | 0.05 |", report);
        Assert.Contains("| v2 | 5 | 3 | 0.2 |", report);
        // off に結果が無いので – が並ぶ
        Assert.Contains("| off | 1 | 0 | 0.05 | – | – | – | – |", report);
        // v2: 2 runs, 片方だけ firstDelta あり → mean/min/max は 200
        Assert.Contains("200 (200–200)", report);
        Assert.Contains("| 0/2 |", report);
    }

    // Given: 無音クリップに ok run とエラー run が 1 件ずつ
    // When: report を生成する
    // Then: falseSubtitleChars は ok run だけの値、runs with error は 1/2
    [Fact]
    public void ReportExcludesErroredRunsFromStats()
    {
        var processed = WriteProcessedDir(expectSilence: true);
        var results = Path.Combine(_dir, $"results-{Guid.NewGuid():N}");
        Directory.CreateDirectory(results);
        File.WriteAllText(
            Path.Combine(results, "clip.off.run1.json"),
            JsonSerializer.Serialize(
                new TranscribeResult
                {
                    Clip = "clip",
                    Variant = "off",
                    Run = 1,
                    Transcript = "oops",
                    SentMs = 1000,
                },
                Json.Options
            )
        );
        File.WriteAllText(
            Path.Combine(results, "clip.off.run2.json"),
            JsonSerializer.Serialize(
                new TranscribeResult
                {
                    Clip = "clip",
                    Variant = "off",
                    Run = 2,
                    Transcript = "much longer false subtitle",
                    SentMs = 1000,
                    Error = "CloseTimeout",
                },
                Json.Options
            )
        );

        var reportPath = Path.Combine(_dir, "report-err.md");
        var options = CliOptions.Parse(["--processed", processed, "--results", results, "--out", reportPath]);
        Assert.Equal(0, ReportCommand.Run(options));

        var report = File.ReadAllText(reportPath);
        // エラー run の 24 文字は平均に入らず、ok run の 4 文字だけになる。
        Assert.Contains("| off | 1 | 0 | 0.05 | – | – | 4 | 1/2 |", report);
    }

    // Given: transcribe 結果ディレクトリが存在しない
    // When: report を生成する
    // Then: process-summary だけで描ける
    [Fact]
    public void ReportWorksWithoutResults()
    {
        var processed = WriteProcessedDir();
        var reportPath = Path.Combine(_dir, "report2.md");
        var options = CliOptions.Parse(["--processed", processed, "--out", reportPath]);
        Assert.Equal(0, ReportCommand.Run(options));
        var report = File.ReadAllText(reportPath);
        Assert.Contains("| v2 | 5 | 3 | 0.2 | – | – | – | – |", report);
    }
}

public class CliTests
{
    // Given: 未知のサブコマンド
    // When: Main を呼ぶ
    // Then: exit code 2
    [Fact]
    public async Task UnknownSubcommandIsUsageError() => Assert.Equal(2, await Program.Main(["bogus"]));

    // Given: --out なしの process
    // When: Main を呼ぶ
    // Then: exit code 2
    [Fact]
    public async Task MissingRequiredOptionIsUsageError() =>
        Assert.Equal(2, await Program.Main(["process", "--corpus", "x"]));
}

public class SelectPairsTests
{
    private static Corpus CorpusWith(params string[] ids)
    {
        var corpus = new Corpus();
        foreach (var id in ids)
        {
            corpus.Clips.Add(new CorpusClip { Id = id, File = id + ".wav" });
        }

        return corpus;
    }

    private static ProcessSummary SummaryWith(params (string Clip, string Variant)[] pairs)
    {
        var summary = new ProcessSummary();
        foreach (var (clip, variant) in pairs)
        {
            summary.Results.Add(new ClipVariantSummary { Clip = clip, Variant = variant });
        }

        return summary;
    }

    // Given: summary に off と v2 だけ存在する
    // When: 既定バリアントで選択する
    // Then: 実績のあるペアだけが返り、v1 は skip される
    [Fact]
    public void DefaultVariantsSkipMissingPairs()
    {
        var pairs = TranscribeCommand.SelectPairs(
            CorpusWith("clip"),
            SummaryWith(("clip", "off"), ("clip", "v2")),
            [],
            GainVariants.AllNames,
            explicitVariants: false
        );

        Assert.Equal([("clip", "off"), ("clip", "v2")], pairs.Select(p => (p.Clip.Id, p.Variant)).ToList());
    }

    // Given: 明示 --variants に summary 外のバリアントを含める
    // When: 選択する
    // Then: 欠けたペアを列挙した usage error になる
    [Fact]
    public void ExplicitVariantMissingPairIsUsageError()
    {
        var error = Assert.Throws<CliUsageException>(() =>
            TranscribeCommand.SelectPairs(
                CorpusWith("clip"),
                SummaryWith(("clip", "off")),
                [],
                ["off", "v1"],
                explicitVariants: true
            )
        );
        Assert.Contains("clip.v1", error.Message);
    }

    // Given: --clips に corpus 外の id がある
    // When: 選択する
    // Then: usage error になる
    [Fact]
    public void UnknownClipIdIsUsageError()
    {
        var error = Assert.Throws<CliUsageException>(() =>
            TranscribeCommand.SelectPairs(
                CorpusWith("clip"),
                SummaryWith(("clip", "off")),
                ["ghost"],
                ["off"],
                explicitVariants: true
            )
        );
        Assert.Contains("ghost", error.Message);
    }

    // Given: --clips で絞り込む
    // When: 選択する
    // Then: 指定クリップのペアだけが返る
    [Fact]
    public void ClipFilterNarrowsSelection()
    {
        var pairs = TranscribeCommand.SelectPairs(
            CorpusWith("a", "b"),
            SummaryWith(("a", "off"), ("b", "off")),
            ["b"],
            ["off"],
            explicitVariants: false
        );

        var pair = Assert.Single(pairs);
        Assert.Equal("b", pair.Clip.Id);
        Assert.Equal("off", pair.Variant);
    }
}
