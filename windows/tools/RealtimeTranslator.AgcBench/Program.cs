using System;
using System.Threading.Tasks;

namespace RealtimeTranslator.AgcBench;

/// <summary>AGC 比較評価ツールのエントリポイント。引数誤りは 2、実行失敗は 1。</summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await Run(args).ConfigureAwait(false);
        }
        catch (CliUsageException exception)
        {
            Console.Error.WriteLine($"usage error: {exception.Message}");
            PrintUsage();
            return 2;
        }
#pragma warning disable CA1031 // 実行失敗はメッセージを出して exit 1。
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    internal static async Task<int> Run(string[] args)
    {
        if (args.Length == 0)
        {
            throw new CliUsageException("subcommand が必要です。");
        }

        var options = CliOptions.Parse(args.AsSpan(1));
        return args[0] switch
        {
            "compose" => ComposeCommand.Run(options),
            "process" => ProcessCommand.Run(options),
            "transcribe" => await TranscribeCommand.RunAsync(options).ConfigureAwait(false),
            "report" => ReportCommand.Run(options),
            _ => throw new CliUsageException($"unknown subcommand '{args[0]}'"),
        };
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            """
            usage:
              agc-bench compose    --scenarios <scenarios.json> --out <corpusDir>
              agc-bench process    --corpus <corpusDir> --out <processedDir> [--variants off,v1,v2]
              agc-bench transcribe --processed <processedDir> --out <resultsDir> [--runs 3] [--variants off,v1,v2] [--clips id1,id2]
              agc-bench report     --processed <processedDir> --results <resultsDir> --out <report.md>
            """
        );
    }
}
