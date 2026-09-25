using System;
using System.IO;
using System.Text.Json;
using RealtimeTranslator.AgcBench;
using Xunit;

namespace RealtimeTranslator.AgcBench.Tests;

public class WavFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"agcbench-{Guid.NewGuid():N}");

    public WavFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    // Given: PCM16 WAV として書き出したサイン波
    // When: WavFile.Read で読み戻す
    // Then: サンプル数と値が往復する
    [Fact]
    public void Pcm16RoundTrips()
    {
        var samples = new float[4800];
        for (var index = 0; index < samples.Length; index += 1)
        {
            samples[index] = MathF.Sin(index * 0.1f) * 0.5f;
        }

        var path = Path.Combine(_dir, "pcm16.wav");
        WavFile.Write(path, samples);
        var read = WavFile.Read(path);

        Assert.Equal(samples.Length, read.Length);
        for (var index = 0; index < samples.Length; index += 1)
        {
            Assert.True(
                MathF.Abs(samples[index] - read[index]) <= 0.0001f,
                $"sample {index}: {samples[index]} vs {read[index]}"
            );
        }
    }

    // Given: float32 フォーマットの WAV
    // When: WavFile.Read で読む
    // Then: float サンプル列がそのまま返る
    [Fact]
    public void Float32WavReads()
    {
        var path = Path.Combine(_dir, "f32.wav");
        WriteWav(
            path,
            format: 3,
            bitsPerSample: 32,
            sampleRate: 24000,
            channels: 1,
            payload =>
            {
                var samples = new byte[] { 0x00, 0x00, 0x80, 0x3E }; // 0.25f
                payload.Write(samples);
            }
        );
        var read = WavFile.Read(path);

        Assert.Single(read);
        Assert.Equal(0.25f, read[0], 6);
    }

    // Given: 48 kHz / ステレオの WAV
    // When: WavFile.Read で読む
    // Then: ffmpeg 変換ヒント付きの例外になる
    [Fact]
    public void RejectsWrongRateAndChannels()
    {
        var path48k = Path.Combine(_dir, "48k.wav");
        WriteWav(
            path48k,
            format: 1,
            bitsPerSample: 16,
            sampleRate: 48000,
            channels: 1,
            payload => payload.Write(new byte[96])
        );
        var stereo = Path.Combine(_dir, "stereo.wav");
        WriteWav(
            stereo,
            format: 1,
            bitsPerSample: 16,
            sampleRate: 24000,
            channels: 2,
            payload => payload.Write(new byte[192])
        );

        var rateError = Assert.Throws<InvalidDataException>(() => WavFile.Read(path48k));
        Assert.Contains("ffmpeg", rateError.Message);
        var channelError = Assert.Throws<InvalidDataException>(() => WavFile.Read(stereo));
        Assert.Contains("ffmpeg", channelError.Message);
    }

    private static void WriteWav(
        string path,
        ushort format,
        ushort bitsPerSample,
        int sampleRate,
        ushort channels,
        Action<BinaryWriter> payload
    )
    {
        using var stream = new MemoryStream();
        using var body = new BinaryWriter(stream);
        payload(body);
        var data = stream.ToArray();
        var blockAlign = (ushort)(channels * bitsPerSample / 8);

        using var file = new BinaryWriter(File.Create(path));
        file.Write("RIFF".ToCharArray());
        file.Write(36 + data.Length);
        file.Write("WAVE".ToCharArray());
        file.Write("fmt ".ToCharArray());
        file.Write(16);
        file.Write(format);
        file.Write(channels);
        file.Write(sampleRate);
        file.Write(sampleRate * blockAlign);
        file.Write(blockAlign);
        file.Write(bitsPerSample);
        file.Write("data".ToCharArray());
        file.Write(data.Length);
        file.Write(data);
    }
}

public class ComposeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"agcbench-{Guid.NewGuid():N}");

    public ComposeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string WriteBaseWav(string name = "base.wav", float amplitude = 0.5f, int samples = 24000)
    {
        var baseDir = Path.Combine(_dir, "base");
        Directory.CreateDirectory(baseDir);
        var data = new float[samples];
        for (var index = 0; index < data.Length; index += 1)
        {
            data[index] = index < 12000 ? amplitude : 0f;
        }

        WavFile.Write(Path.Combine(baseDir, name), data);
        return Path.Combine("base", name).Replace('\\', '/');
    }

    private (Corpus Corpus, string OutDir) Compose(params Scenario[] scenarios)
    {
        var scenariosPath = Path.Combine(_dir, $"scenarios-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            scenariosPath,
            JsonSerializer.Serialize(new Scenarios { Items = [.. scenarios] }, Json.Options)
        );
        var outDir = Path.Combine(_dir, $"corpus-{Guid.NewGuid():N}");
        var options = CliOptions.Parse(["--scenarios", scenariosPath, "--out", outDir]);
        Assert.Equal(0, ComposeCommand.Run(options));
        return (Corpus.Load(outDir), outDir);
    }

    // Given: 同じ seed の scenarios.json
    // When: compose を 2 回実行する
    // Then: 出力 WAV のバイト列が一致する
    [Fact]
    public void SameSeedProducesIdenticalBytes()
    {
        var basePath = WriteBaseWav();
        var scenario = new Scenario
        {
            Id = "det",
            Base = basePath,
            Noise = new NoiseSpec { Rms = 0.004 },
            Seed = 7,
        };
        var (_, outA) = Compose(scenario);
        var (_, outB) = Compose(scenario);

        Assert.Equal(
            File.ReadAllBytes(Path.Combine(outA, "det.wav")),
            File.ReadAllBytes(Path.Combine(outB, "det.wav"))
        );
    }

    // Given: gainDb -20 と先頭 500 ms 無音のシナリオ
    // When: compose する
    // Then: base ピークが 0.1 倍になり、先頭 500 ms が無音で speechOnsetMs=500
    [Fact]
    public void GainDbScalesBaseAndSilencePrepended()
    {
        var basePath = WriteBaseWav(amplitude: 0.5f);
        var (corpus, outDir) = Compose(
            new Scenario
            {
                Id = "quiet",
                Base = basePath,
                GainDb = -20,
                LeadingSilenceMs = 500,
            }
        );

        var clip = Assert.Single(corpus.Clips);
        Assert.Equal(500, clip.SpeechOnsetMs);
        Assert.False(clip.ExpectSilence);

        var samples = WavFile.Read(Path.Combine(outDir, "quiet.wav"));
        var peak = 0f;
        for (var index = 0; index < 12000; index += 1)
        {
            Assert.Equal(0f, samples[index]);
            peak = MathF.Max(peak, MathF.Abs(samples[index + 12000]));
        }

        Assert.Equal(0.05f, peak, 3);
    }

    // Given: dipEveryMs 付きホワイトノイズだけのシナリオ
    // When: compose する
    // Then: 全体 RMS は指定の ±10% 以内、dip 窓の RMS はそれより低い
    [Fact]
    public void NoiseMatchesRequestedRmsAndDipsLower()
    {
        var (corpus, outDir) = Compose(
            new Scenario
            {
                Id = "noise",
                LeadingSilenceMs = 0,
                TrailingSilenceMs = 3000,
                Noise = new NoiseSpec
                {
                    Rms = 0.004,
                    DipEveryMs = 500,
                    DipMs = 100,
                    DipRms = 0.001,
                },
                Seed = 3,
            }
        );
        Assert.True(Assert.Single(corpus.Clips).ExpectSilence);

        var samples = WavFile.Read(Path.Combine(outDir, "noise.wav"));
        var rms = Rms(samples, 0, samples.Length);
        Assert.InRange(rms, 0.004 * 0.9, 0.004 * 1.1);

        // 2 周期目の dip 窓 ([500ms,600ms)) の RMS が通常区間より低い。
        var dipRms = Rms(samples, 500 * 24, 100 * 24);
        Assert.True(dipRms < rms * 0.5, $"dipRms {dipRms} should be << {rms}");
    }

    // Given: クリックと base 省略のシナリオ
    // When: compose する
    // Then: atMs 位置にクリックが乗り、expectSilence=true になる
    [Fact]
    public void ClickAppearsAndMissingBaseMarksSilence()
    {
        var (corpus, outDir) = Compose(
            new Scenario
            {
                Id = "click",
                LeadingSilenceMs = 200,
                TrailingSilenceMs = 1800,
                Noise = new NoiseSpec { Rms = 0.001 },
                Clicks =
                [
                    new ClickSpec
                    {
                        AtMs = 1200,
                        Peak = 0.95,
                        DurationMs = 2,
                    },
                ],
                Seed = 1,
            }
        );

        var clip = Assert.Single(corpus.Clips);
        Assert.True(clip.ExpectSilence);
        Assert.Equal(200, clip.SpeechOnsetMs);

        var samples = WavFile.Read(Path.Combine(outDir, "click.wav"));
        var clickSample = samples[1200 * 24];
        Assert.True(clickSample > 0.9f, $"click sample {clickSample}");
    }

    private static double Rms(float[] samples, int start, int length)
    {
        double sum = 0;
        for (var index = start; index < start + length && index < samples.Length; index += 1)
        {
            sum += (double)samples[index] * samples[index];
        }

        return Math.Sqrt(sum / length);
    }
}
