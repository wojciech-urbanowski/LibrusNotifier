namespace LibrusNotifier;

/// <summary>Ścieżki plików w katalogu aplikacji.</summary>
public sealed class AppPaths
{
    public const string ConfigFileName = "librus_config.txt";

    public AppPaths(string appDirectory)
    {
        AppDirectory = Path.GetFullPath(appDirectory);
        ConfigFile = Path.Combine(AppDirectory, ConfigFileName);
        LockFile = Path.Combine(AppDirectory, "librus_notifier.lock");
        DataDirectory = Path.Combine(AppDirectory, "data");
        DatabaseFile = Path.Combine(DataDirectory, "librus.db");
        BackupDirectory = Path.Combine(DataDirectory, "backups");
        LogDirectory = Path.Combine(DataDirectory, "logs");
        HolidayCacheFile = Path.Combine(DataDirectory, "ferie_cache.json");
    }

    /// <summary>Domyślny katalog aplikacji: katalog pliku wykonywalnego, niezależnie od katalogu roboczego.</summary>
    public static string DefaultAppDirectory => AppContext.BaseDirectory;

    public string AppDirectory { get; }
    public string ConfigFile { get; }
    public string LockFile { get; }
    public string DataDirectory { get; }
    public string DatabaseFile { get; }
    public string BackupDirectory { get; }
    public string LogDirectory { get; }
    public string HolidayCacheFile { get; }
}
