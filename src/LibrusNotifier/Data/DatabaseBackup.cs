using System.Globalization;
using LibrusNotifier.Logging;
using LibrusNotifier.Timing;
using Microsoft.Data.Sqlite;

namespace LibrusNotifier.Data;

/// <summary>
/// Kopie zapasowe bazy w <c>data/backups</c>. Kopia powstaje przy starcie aplikacji i ma w nazwie czas (strefa Europe/Warsaw)
/// z dokładnością do minuty: <c>librus-RRRR-MM-DD_GGmm.db</c>. Jeśli plik o takiej nazwie już istnieje, kopii się nie powtarza.
/// Kopie starsze niż miesiąc (wg czasu w nazwie) są kasowane.
/// </summary>
public sealed class DatabaseBackup(Clock clock, IAppLog log)
{
    public const string FilePrefix = "librus-";
    public const string FileExtension = ".db";
    public const int RetentionMonths = 1;

    private const string TimestampFormat = "yyyy-MM-dd_HHmm";
    private const string TempSuffix = ".tmp";

    public static string FileNameFor(DateTime local) =>
        FilePrefix + local.ToString(TimestampFormat, CultureInfo.InvariantCulture) + FileExtension;

    /// <summary>
    /// Wykonuje kopię (jeśli baza istnieje i kopii o tej nazwie jeszcze nie ma), po czym kasuje stare kopie.
    /// Błędy trafiają do logu jako błąd; aplikacja działa dalej.
    /// </summary>
    /// <returns>
    /// <c>false</c>, gdy kopii nie udało się wykonać. Wtedy stare kopie zostają nietknięte, żeby nie zostać bez żadnej.
    /// </returns>
    public bool Run(string databaseFile, string backupDirectory)
    {
        // Nazwy mają dokładność do minuty, więc i granica przechowywania liczona jest co do minuty.
        var local = clock.Now;
        var now = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0);
        if (!TryCreate(databaseFile, backupDirectory, now)) return false;
        var deleted = DeleteOld(backupDirectory, now);
        if (deleted > 0) log.Info($"Usunięto stare kopie zapasowe bazy: {deleted}.");
        return true;
    }

    private bool TryCreate(string databaseFile, string backupDirectory, DateTime now)
    {
        if (!File.Exists(databaseFile))
        {
            log.Info("Brak pliku bazy - kopia zapasowa pominięta.");
            return true;
        }

        var name = FileNameFor(now);
        var target = Path.Combine(backupDirectory, name);
        // Kopia powstaje pod tymczasową nazwą i dopiero gotowa dostaje docelową, więc przerwana nie blokuje kopii z tej minuty.
        var temp = target + TempSuffix;
        try
        {
            if (File.Exists(target))
            {
                log.Info($"Kopia zapasowa {name} już istnieje - pomijam.");
                return true;
            }
            Directory.CreateDirectory(backupDirectory);
            File.Delete(temp);
            CopyDatabase(databaseFile, temp);
            File.Move(temp, target);
            log.Info($"Kopia zapasowa bazy: {name} ({ByteSize.Format(new FileInfo(target).Length)}).");
            return true;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            log.Error($"Nie można wykonać kopii zapasowej bazy ({name}): {e.Message}");
            return false;
        }
    }

    /// <summary>Kopiuje bazę interfejsem kopii zapasowych SQLite, spójnie także wtedy, gdy plik jest akurat używany.</summary>
    private static void CopyDatabase(string source, string destination)
    {
        using var from = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = source, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        using var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        from.Open();
        to.Open();
        from.BackupDatabase(to);
    }

    private int DeleteOld(string backupDirectory, DateTime now)
    {
        if (!Directory.Exists(backupDirectory)) return 0;
        var cutoff = now.AddMonths(-RetentionMonths);
        var deleted = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(backupDirectory, FilePrefix + "*"))
            {
                if (!TryGetTime(Path.GetFileName(file), out var time) || time >= cutoff) continue;
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log.Warn($"Nie można usunąć starej kopii zapasowej {Path.GetFileName(file)}: {e.Message}");
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Nie można przejrzeć katalogu kopii zapasowych {backupDirectory}: {e.Message}");
        }
        return deleted;
    }

    /// <summary>Czas z nazwy kopii (także niedokończonej, z końcówką .tmp); inne pliki w katalogu nie są kopiami i nie są ruszane.</summary>
    private static bool TryGetTime(string fileName, out DateTime time)
    {
        time = default;
        if (fileName.Length < FilePrefix.Length + TimestampFormat.Length) return false;
        var stamp = fileName.Substring(FilePrefix.Length, TimestampFormat.Length);
        if (!DateTime.TryParseExact(stamp, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out time)) return false;
        var expected = FileNameFor(time);
        return fileName == expected || fileName == expected + TempSuffix;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Pozostałość zostanie usunięta razem ze starymi kopiami albo nadpisana przy następnej próbie.
        }
    }
}
