using System.Globalization;
using System.Text;
using LibrusNotifier.Timing;

namespace LibrusNotifier.Logging;

public enum LogLevel
{
    Info,
    Warning,
    Error
}

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message);

public interface IAppLog
{
    void Write(LogLevel level, string message);
}

public static class AppLogExtensions
{
    public static void Info(this IAppLog log, string message) => log.Write(LogLevel.Info, message);
    public static void Warn(this IAppLog log, string message) => log.Write(LogLevel.Warning, message);
    public static void Error(this IAppLog log, string message) => log.Write(LogLevel.Error, message);
}

/// <summary>
/// Log lokalny: konsola i plik dzienny <c>data/logs/librus-RRRR-MM-DD.log</c> (czas Europe/Warsaw).
/// Znane sekrety (hasła) są zamieniane na gwiazdki przed zapisem.
/// </summary>
public sealed class AppLog : IAppLog
{
    public const string FilePrefix = "librus-";
    public const string FileExtension = ".log";

    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly Lock _gate = new();
    private readonly Clock _clock;
    private readonly string? _directory;
    private readonly List<string> _secrets = [];
    private bool _fileErrorReported;

    public AppLog(Clock clock, string? directory)
    {
        _clock = clock;
        _directory = directory;
    }

    /// <summary>Wywoływane po zapisaniu wpisu (np. do zbierania błędów do podsumowania dnia).</summary>
    public event Action<LogEntry>? EntryWritten;

    public void AddSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < 4) return;
        lock (_gate)
        {
            if (_secrets.Contains(secret)) return;
            _secrets.Add(secret);
            _secrets.Sort((a, b) => b.Length.CompareTo(a.Length));
        }
    }

    public void Write(LogLevel level, string message)
    {
        var now = _clock.Now;
        LogEntry entry;
        lock (_gate)
        {
            message = Redact(message);
            entry = new LogEntry(now, level, message);
            var line = $"{now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {Label(level)} {message}";
            try
            {
                (level == LogLevel.Error ? Console.Error : Console.Out).WriteLine(line);
            }
            catch (IOException)
            {
                // Konsola może być niedostępna (np. zamknięty potok).
            }
            if (_directory is not null) AppendToFile(now, line);
        }
        EntryWritten?.Invoke(entry);
    }

    /// <summary>Usuwa pliki logu starsze niż podana liczba dni (wg daty w nazwie pliku).</summary>
    public int DeleteOldFiles(int maxAgeDays)
    {
        if (_directory is null || !Directory.Exists(_directory)) return 0;
        var cutoff = _clock.Today.AddDays(-maxAgeDays);
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(_directory, FilePrefix + "*" + FileExtension))
        {
            var name = Path.GetFileNameWithoutExtension(file)[FilePrefix.Length..];
            if (!DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date >= cutoff)
                continue;
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                this.Warn($"Nie można usunąć starego pliku logu {Path.GetFileName(file)}: {e.Message}");
            }
        }
        return deleted;
    }

    public static string FileNameFor(DateOnly day) => FilePrefix + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + FileExtension;

    private void AppendToFile(DateTime now, string line)
    {
        try
        {
            Directory.CreateDirectory(_directory!);
            var path = Path.Combine(_directory!, FileNameFor(DateOnly.FromDateTime(now)));
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var bytes = Utf8.GetBytes(line + Environment.NewLine);
            stream.Write(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (_fileErrorReported) return;
            _fileErrorReported = true;
            try
            {
                Console.Error.WriteLine($"Nie można zapisać pliku logu w {_directory}: {e.Message}");
            }
            catch (IOException)
            {
                // Brak konsoli.
            }
        }
    }

    private string Redact(string message)
    {
        foreach (var secret in _secrets)
            message = message.Replace(secret, "***", StringComparison.Ordinal);
        return message;
    }

    private static string Label(LogLevel level) => level switch
    {
        LogLevel.Info => "INFO ",
        LogLevel.Warning => "UWAGA",
        _ => "BŁĄD "
    };
}
