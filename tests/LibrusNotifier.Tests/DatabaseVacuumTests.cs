using LibrusNotifier.Data;
using LibrusNotifier.Logging;
using LibrusNotifier.Reporting;
using Microsoft.EntityFrameworkCore;

namespace LibrusNotifier.Tests;

public sealed class DatabaseVacuumTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly ListLog _log = new();

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("2026-01-10", true)]
    [InlineData("2026-11-16", true)] // 320. dzień roku
    [InlineData("2026-12-26", true)] // 360. dzień roku
    [InlineData("2026-01-01", false)]
    [InlineData("2026-11-12", false)] // 316. dzień roku
    [InlineData("2026-12-31", false)]
    [InlineData("2027-03-10", false)] // 69. dzień roku
    [InlineData("2028-03-10", true)] // rok przestępny: 70. dzień roku
    public void VacuumIsDueOnDaysOfYearDivisibleByTen(string day, bool expected) =>
        Assert.Equal(expected, DatabaseVacuum.IsDue(DateOnly.Parse(day)));

    [Fact]
    public void NothingHappensOnDaysWhenVacuumIsNotDue()
    {
        var missing = Path.Combine(Path.GetTempPath(), "librus-nie-ma-" + Guid.NewGuid().ToString("N") + ".db");

        Assert.Null(DatabaseVacuum.RunIfDue(new DateOnly(2026, 11, 12), missing, _log));

        Assert.False(File.Exists(missing));
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task VacuumShrinksFileAfterDeletesAndKeepsData()
    {
        await using (var db = _db.Create())
        {
            db.Elements.AddRange(Enumerable.Range(0, 300).Select(i => new ElementEntity
            {
                Type = "ocena",
                Key = $"k{i}",
                DataJson = new string('x', 4000),
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            }));
            await db.SaveChangesAsync();
            await db.Elements.Where(e => e.Id > 10).ExecuteDeleteAsync();
        }

        var report = DatabaseVacuum.RunIfDue(new DateOnly(2026, 11, 16), _db.FilePath, _log);

        Assert.NotNull(report);
        Assert.True(report.Succeeded);
        Assert.Null(report.Error);
        Assert.Equal(new FileInfo(_db.FilePath).Length, report.SizeAfter);
        Assert.True(report.SizeAfter < report.SizeBefore);
        Assert.Equal(report.SizeBefore - report.SizeAfter, report.Freed);
        await using var check = _db.Create();
        Assert.Equal(10, await check.Elements.CountAsync());
        Assert.Contains(_log.Messages(LogLevel.Info), m => m.StartsWith("Vacuum bazy:"));
    }

    [Fact]
    public void MissingDatabaseFileIsReportedAndNotCreated()
    {
        var missing = Path.Combine(Path.GetTempPath(), "librus-nie-ma-" + Guid.NewGuid().ToString("N") + ".db");

        var report = DatabaseVacuum.RunIfDue(new DateOnly(2026, 11, 16), missing, _log);

        Assert.NotNull(report);
        Assert.False(report.Succeeded);
        Assert.Equal("brak pliku bazy", report.Error);
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void CorruptedDatabaseMakesVacuumFailWithErrorInLogAndReport()
    {
        File.WriteAllBytes(_db.FilePath, Enumerable.Range(0, 4096).Select(i => (byte)(i * 7 + 3)).ToArray());

        var report = DatabaseVacuum.RunIfDue(new DateOnly(2026, 11, 16), _db.FilePath, _log);

        Assert.NotNull(report);
        Assert.False(report.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(report.Error));
        Assert.Equal(4096, report.SizeBefore);
        Assert.Contains(_log.Messages(LogLevel.Error), m => m.StartsWith("Vacuum bazy nie powiódł się:"));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1024 * 1024, "1 MB")]
    [InlineData(1310720, "1.25 MB")]
    public void SizeIsFormattedWithUnitAndDecimalPoint(long bytes, string expected) => Assert.Equal(expected, ByteSize.Format(bytes));

    private static DailyStats Stats() => new(new DateOnly(2026, 11, 16), new DateTime(2026, 11, 16, 0, 5, 0));

    private static string[] SummaryLines(VacuumReport? vacuum, DailyStats? stats = null) =>
        DailySummary.Build((stats ?? Stats()).Snapshot(), "dzień szkolny", "zmiana dnia", new DateTime(2026, 11, 16, 23, 40, 0), 0, ["admin@example.com"], vacuum)
            .Body.TrimEnd().Split(Environment.NewLine);

    [Fact]
    public void DailySummaryEndsWithVacuumReportSeparatedByBlankLine()
    {
        var report = new VacuumReport(new DateOnly(2026, 11, 16), true, 1310720, 1048576, TimeSpan.FromMilliseconds(420));

        var lines = SummaryLines(report);

        Assert.Equal("Vacuum bazy (dzień roku 320): wykonano w 0.42 s; rozmiar bazy 1.25 MB → 1 MB (zwolniono 256 KB)", lines[^1]);
        Assert.Equal("", lines[^2]);
        Assert.Equal("Ostrzeżenia: brak", lines[^3]);
    }

    [Fact]
    public void DailySummaryVacuumReportAfterListedProblemsHasSingleBlankLine()
    {
        var stats = Stats();
        stats.RecordProblem(new LogEntry(new DateTime(2026, 11, 16, 10, 0, 0), LogLevel.Warning, "Coś ostrzega"));
        var report = new VacuumReport(new DateOnly(2026, 11, 16), true, 1000, 1000, TimeSpan.Zero);

        var lines = SummaryLines(report, stats);

        Assert.Equal("Vacuum bazy (dzień roku 320): wykonano w 0 s; rozmiar bazy 1000 B → 1000 B (zwolniono 0 B)", lines[^1]);
        Assert.Equal("", lines[^2]);
        Assert.Contains("Coś ostrzega", lines[^3]);
    }

    [Fact]
    public void DailySummaryReportsFailedVacuumWithFirstLineOfError()
    {
        var report = new VacuumReport(new DateOnly(2026, 11, 16), false, 2048, 2048, TimeSpan.FromSeconds(30), "database is locked\nszczegóły");

        var lines = SummaryLines(report);

        Assert.Equal("Vacuum bazy (dzień roku 320): nie powiódł się – database is locked", lines[^1]);
    }

    [Fact]
    public void DailySummaryWithoutVacuumHasNoVacuumSection()
    {
        Assert.DoesNotContain(SummaryLines(null), line => line.Contains("Vacuum"));
        Assert.Equal("Ostrzeżenia: brak", SummaryLines(null)[^1]);
    }
}
