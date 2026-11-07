using System;
using System.Collections.Generic;

namespace RealtimeTranslator.AgcBench;

/// <summary>usage 誤り (exit 2)。</summary>
internal sealed class CliUsageException(string message) : Exception(message);

/// <summary>`--key value` 形式のオプション辞書。</summary>
internal sealed class CliOptions
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public string Require(string key) =>
        Get(key) ?? throw new CliUsageException($"missing required option --{key}");

    public int GetInt(string key, int defaultValue)
    {
        var raw = Get(key);
        if (raw is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, out var value) || value <= 0)
        {
            throw new CliUsageException($"--{key} must be a positive integer");
        }

        return value;
    }

    public IReadOnlyList<string> GetList(string key)
    {
        var raw = Get(key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts;
    }

    public static CliOptions Parse(ReadOnlySpan<string> args)
    {
        var options = new CliOptions();
        for (var index = 0; index < args.Length; index += 1)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new CliUsageException($"unexpected argument '{arg}'");
            }

            var key = arg[2..];
            if (key.Length == 0 || index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new CliUsageException($"option '--{key}' needs a value");
            }

            options._values[key] = args[++index];
        }

        return options;
    }
}
