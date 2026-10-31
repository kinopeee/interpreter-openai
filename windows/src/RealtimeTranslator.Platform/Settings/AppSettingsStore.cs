using System;
using System.Globalization;
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

    private const string CorruptBackupInfix = ".corrupt-";
    private const string CorruptBackupTimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    private readonly string _path;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();

    public AppSettingsStore(string? path = null, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _path =
            path
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                DirectoryName,
                FileName
            );
    }

    public string FilePath => _path;

    public AppSettingsData Load()
    {
        lock (_sync)
        {
            string json;
            try
            {
                if (!File.Exists(_path))
                {
                    return AppSettingsData.Default;
                }

                json = File.ReadAllText(_path, Encoding.UTF8);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                AppLogger.Warning(LogCategory.General, "settings load failed, using defaults");
                return AppSettingsData.Default;
            }

            if (AppSettingsCodec.TryDecode(json, out var settings))
            {
                return settings;
            }

            MoveCorruptFileAside();
            return AppSettingsData.Default;
        }
    }

    /// <summary>
    /// 壊れた settings.json を次の保存で既定値に上書きされる前に同じディレクトリへ退避する。
    /// 既存の退避ファイルは上書きせず、空いている連番を付ける。
    /// </summary>
    private void MoveCorruptFileAside()
    {
        var timestamp = _timeProvider
            .GetUtcNow()
            .UtcDateTime.ToString(CorruptBackupTimestampFormat, CultureInfo.InvariantCulture);
        var basePath = _path + CorruptBackupInfix + timestamp;
        try
        {
            var backupPath = basePath;
            for (var suffix = 1; File.Exists(backupPath); suffix++)
            {
                backupPath = basePath + "-" + suffix.ToString(CultureInfo.InvariantCulture);
            }

            File.Move(_path, backupPath, overwrite: false);
            AppLogger.Warning(LogCategory.General, "settings file corrupt; moved aside, using defaults");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warning(LogCategory.General, "settings corrupt backup failed, using defaults");
        }
    }

    /// <summary>保存できたら true、書き込みに失敗したら false を返す。</summary>
    public bool Save(AppSettingsData settings)
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

                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                AppLogger.Warning(LogCategory.General, "settings save failed");
                return false;
            }
        }
    }
}
