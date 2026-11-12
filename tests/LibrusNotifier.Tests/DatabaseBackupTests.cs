using LibrusNotifier.Data;
using LibrusNotifier.Logging;
using Microsoft.EntityFrameworkCore;

namespace LibrusNotifier.Tests;

public sealed class DatabaseBackupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "librus-backup-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TestDatabase _db = new();
    private readonly FakeClock _clock = new(new DateTime(2026, 11, 12, 14, 30, 45));
    private readonly ListLog _log = new();

    public DatabaseBackupTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private string BackupDirectory => Path.Combine(_directory, "backups");

    private string BackupPath(string name) => Path.Combine(BackupDirectory, name);

    private bool Run() => new DatabaseBackup(_clock, _log).Run(_db.FilePath, BackupDirectory);

    private void CreateFile(string name, string content = "x")
    {
        Directory.CreateDirectory(BackupDirectory);
        File.WriteAllText(BackupPath(name), content);
    }

    [Fact]
    public async Task BackupNameHasDateAndTimeToTheMinuteAndContainsTheData()
    {
        await using (var db = _db.Create())
        {
            db.Elements.Add(new ElementEntity { Type = "ocena", Key = "k1", DataJson = "{}", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        Assert.True(Run());

        var path = BackupPath("librus-2026-11-12_1430.db");
        Assert.Equal(path, Directory.EnumerateFiles(BackupDirectory).Single());
        await using var backup = NotifierDbContext.Create(path);
        Assert.Equal("k1", (await backup.Elements.SingleAsync()).Key);
        Assert.Contains(_log.Messages(LogLevel.Info), m => m.Contains("librus-2026-11-12_1430.db"));
    }

    [Fact]
    public void BackupWithTheSameNameIsNotRepeatedButNextMinuteGetsNewOne()
    {
        CreateFile("librus-2026-11-12_1430.db", "pierwsza kopia");

        Assert.True(Run());

        Assert.Equal("pierwsza kopia", File.ReadAllText(BackupPath("librus-2026-11-12_1430.db")));
        Assert.Single(Directory.EnumerateFiles(BackupDirectory));
        Assert.Contains(_log.Messages(LogLevel.Info), m => m.Contains("już istnieje"));

        _clock.Advance(TimeSpan.FromSeconds(10)); // 14:30:55 – wciąż ta sama minuta
        Assert.True(Run());
        Assert.Single(Directory.EnumerateFiles(BackupDirectory));

        _clock.Advance(TimeSpan.FromSeconds(10)); // 14:31:05
        Assert.True(Run());
        Assert.Equal(["librus-2026-11-12_1430.db", "librus-2026-11-12_1431.db"],
            Directory.EnumerateFiles(BackupDirectory).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void BackupsOlderThanAMonthAreDeletedAndOtherFilesAreKept()
    {
        // Granica: 2026-11-12 14:30 (zegar: 14:30:45) minus miesiąc to 2026-10-12 14:30; starsze (wg czasu w nazwie) są kasowane.
        CreateFile("librus-2026-10-12_1429.db");
        CreateFile("librus-2026-10-12_1430.db");
        CreateFile("librus-2026-11-01_0800.db");
        CreateFile("librus-2026-09-01_0800.db.tmp");
        CreateFile("librus-2026-09-01_0800.db.bak");
        CreateFile("librus-abc.db");
        CreateFile("notatka.txt");

        Assert.True(Run());

        Assert.Equal(
            ["librus-2026-09-01_0800.db.bak", "librus-2026-10-12_1430.db", "librus-2026-11-01_0800.db", "librus-2026-11-12_1430.db", "librus-abc.db", "notatka.txt"],
            Directory.EnumerateFiles(BackupDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Contains(_log.Messages(LogLevel.Info), m => m.Contains("Usunięto stare kopie zapasowe bazy: 2"));
    }

    [Fact]
    public void FailedBackupKeepsOldBackupsAndLeavesNoPartialFile()
    {
        File.WriteAllBytes(_db.FilePath, Enumerable.Range(0, 4096).Select(i => (byte)(i * 7 + 3)).ToArray());
        CreateFile("librus-2026-01-01_0800.db");

        Assert.False(Run());

        Assert.Equal(["librus-2026-01-01_0800.db"], Directory.EnumerateFiles(BackupDirectory).Select(Path.GetFileName));
        Assert.Contains(_log.Messages(LogLevel.Error), m => m.Contains("Nie można wykonać kopii zapasowej bazy (librus-2026-11-12_1430.db)"));
    }

    [Fact]
    public void MissingDatabaseSkipsBackupButStillDeletesOldOnes()
    {
        CreateFile("librus-2026-01-01_0800.db");
        File.Delete(_db.FilePath);

        Assert.True(new DatabaseBackup(_clock, _log).Run(_db.FilePath, BackupDirectory));

        Assert.Empty(Directory.EnumerateFiles(BackupDirectory));
        Assert.Contains(_log.Messages(LogLevel.Info), m => m.Contains("Brak pliku bazy"));
    }

    [Fact]
    public void MissingBackupDirectoryIsCreatedOnlyWhenThereIsSomethingToBackUp()
    {
        File.Delete(_db.FilePath);
        Assert.True(new DatabaseBackup(_clock, _log).Run(_db.FilePath, BackupDirectory));
        Assert.False(Directory.Exists(BackupDirectory));
    }
}
