using System.Diagnostics;
using System.Globalization;
using LibrusNotifier.Logging;
using Microsoft.Data.Sqlite;

namespace LibrusNotifier.Data;

/// <summary>Wynik vacuum bazy; trafia na koniec podsumowania dnia dla administratorów.</summary>
public sealed record VacuumReport(DateOnly Day, bool Succeeded, long SizeBefore, long SizeAfter, TimeSpan Duration, string? Error = null)
{
    /// <summary>Ile bajtów zwolniono (ujemne, gdy plik urósł).</summary>
    public long Freed => SizeBefore - SizeAfter;

    public string DurationText => string.Create(CultureInfo.InvariantCulture, $"{Duration.TotalSeconds:0.##} s");
}

/// <summary>
/// Vacuum bazy (zwolnienie miejsca po usuniętych danych) na zakończenie działania, w dni roku podzielne przez
/// <see cref="DayOfYearDivisor"/> (10., 20., 30. … dzień roku).
/// </summary>
public static class DatabaseVacuum
{
    public const int DayOfYearDivisor = 10;

    public static bool IsDue(DateOnly day) => day.DayOfYear % DayOfYearDivisor == 0;

    /// <summary>
    /// Wykonuje vacuum, jeśli dla danego dnia jest zaplanowane. Baza nie może być w tym czasie otwarta przez inne połączenie.
    /// Błąd trafia do logu i do raportu; aplikacja działa dalej.
    /// </summary>
    /// <returns><c>null</c>, gdy vacuum nie jest zaplanowany na ten dzień.</returns>
    public static VacuumReport? RunIfDue(DateOnly day, string databaseFile, IAppLog log)
    {
        if (!IsDue(day)) return null;

        var started = Stopwatch.GetTimestamp();
        long before = 0;
        try
        {
            if (!File.Exists(databaseFile))
            {
                log.Warn("Vacuum bazy pominięty: brak pliku bazy.");
                return new VacuumReport(day, false, 0, 0, Stopwatch.GetElapsedTime(started), "brak pliku bazy");
            }
            before = new FileInfo(databaseFile).Length;
            Vacuum(databaseFile);
            var report = new VacuumReport(day, true, before, new FileInfo(databaseFile).Length, Stopwatch.GetElapsedTime(started));
            log.Info($"Vacuum bazy: {ByteSize.Format(report.SizeBefore)} → {ByteSize.Format(report.SizeAfter)} w {report.DurationText}.");
            return report;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            log.Error($"Vacuum bazy nie powiódł się: {e.Message}");
            return new VacuumReport(day, false, before, before, Stopwatch.GetElapsedTime(started), e.Message);
        }
    }

    private static void Vacuum(string databaseFile)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = databaseFile, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM";
        command.ExecuteNonQuery();
    }
}
