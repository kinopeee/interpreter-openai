using System;
using System.IO;
using System.Text;
using RealtimeTranslator.Core.Settings;
using RealtimeTranslator.Platform.Logging;

namespace RealtimeTranslator.Platform.Settings;

/// <summary>%LOCALAPPDATA%\RealtimeTranslator\settings.json を原子的に読み書きする。</summary>
public sealed class AppSettingsStore
{
    public const string FileName = "settings.json";
    public const string DirectoryName = "RealtimeTranslator";

    private readonly string _path;
    private readonly object _sync = new();

    public AppSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DirectoryName,
            FileName);
    }

    public string FilePath => _path;

    public AppSettingsData Load()
    {
        lock (_sync)
        {
            try
            {
                return File.Exists(_path)
                    ? AppSettingsCodec.Decode(File.ReadAllText(_path, Encoding.UTF8))
                    : AppSettingsData.Default;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                AppLogger.Warning(LogCategory.General, "settings load failed, using defaults");
                return AppSettingsData.Default;
            }
        }
    }

    public void Save(AppSettingsData settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_sync)
        {
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // 書き込み途中で落ちても既存ファイルを壊さないよう temp → replace/move で差し替える。
                var temporaryPath = _path + ".tmp";
                File.WriteAllText(temporaryPath, AppSettingsCodec.Encode(settings), Encoding.UTF8);
                if (File.Exists(_path))
                {
                    File.Replace(temporaryPath, _path, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(temporaryPath, _path);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                AppLogger.Warning(LogCategory.General, "settings save failed");
            }
        }
    }
}
