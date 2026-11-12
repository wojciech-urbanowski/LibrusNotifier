using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using LibrusNotifier.Calendar;
using LibrusNotifier.Configuration;
using LibrusNotifier.Data;
using LibrusNotifier.Digest;
using LibrusNotifier.Librus;
using LibrusNotifier.Logging;
using LibrusNotifier.Mail;
using LibrusNotifier.Processing;
using LibrusNotifier.Reporting;
using LibrusNotifier.Scheduling;
using LibrusNotifier.Timing;
using Microsoft.EntityFrameworkCore;

namespace LibrusNotifier;

public static class App
{
    public const int ExitOk = 0;
    public const int ExitError = 1;
    public const int ExitConfiguration = 2;
    public const int ExitAlreadyRunning = 3;
    public const int ExitInterrupted = 130;

    public const int LogRetentionDays = 30;

    public static string Version =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    public static async Task<int> RunAsync(string[] args)
    {
        TryUseUtf8Console();

        CommandLineOptions options;
        try
        {
            options = CommandLineOptions.Parse(args);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(CommandLineOptions.Usage);
            return ExitConfiguration;
        }
        if (options.ShowHelp)
        {
            Console.WriteLine(CommandLineOptions.Usage);
            return ExitOk;
        }

        var paths = new AppPaths(options.Directory ?? AppPaths.DefaultAppDirectory);
        if (!Directory.Exists(paths.AppDirectory))
        {
            Console.Error.WriteLine($"Katalog aplikacji nie istnieje: {paths.AppDirectory}");
            return ExitConfiguration;
        }

        Clock clock;
        try
        {
            clock = new SystemClock(WarsawTime.LoadZone());
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine(e.Message);
            return ExitError;
        }

        var log = new AppLog(clock, paths.LogDirectory);
        InstanceLock? instanceLock;
        try
        {
            instanceLock = InstanceLock.TryAcquire(paths.LockFile);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            log.Error($"Nie można utworzyć pliku blokady {paths.LockFile}: {e.Message}");
            return ExitError;
        }
        if (instanceLock is null)
        {
            log.Info($"Inna instancja LibrusNotifier już działa (blokada {paths.LockFile} jest zajęta) – kończę.");
            return ExitAlreadyRunning;
        }

        using (instanceLock)
        {
            try
            {
                return await RunLockedAsync(options, paths, clock, log);
            }
            catch (Exception e)
            {
                log.Error($"Nieobsłużony błąd: {e}");
                return ExitError;
            }
        }
    }

    private static async Task<int> RunLockedAsync(CommandLineOptions options, AppPaths paths, Clock clock, AppLog log)
    {
        var startedLocal = clock.Now;
        var day = DateOnly.FromDateTime(startedLocal);
        var stats = new DailyStats(day, startedLocal);
        stats.Attach(log);

        log.Info($"Start LibrusNotifier {Version} (katalog: {paths.AppDirectory}{(options.RunOnce ? ", tryb --run-once" : "")}).");
        var deleted = log.DeleteOldFiles(LogRetentionDays);
        if (deleted > 0) log.Info($"Usunięto stare pliki logu: {deleted}.");

        AppConfig config;
        try
        {
            config = ConfigParser.Load(paths.ConfigFile);
        }
        catch (ConfigException e)
        {
            log.Error(e.Message);
            return ExitConfiguration;
        }
        foreach (var secret in config.Secrets()) log.AddSecret(secret);
        log.Info($"Konfiguracja wczytana: rodzice {config.ParentRecipients.Count} adr., uczeń {config.StudentRecipients.Count} adr., " +
            $"administratorzy {config.AdminRecipients.Count} adr., województwo {config.Voivodeship.Name}, " +
            $"limit bezczynności sesji {config.IdleLimit.TotalMinutes:0.##} min.");

        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        using var sigterm = TryRegisterSigterm(shutdown);
        using var dayGuard = new DayGuard(clock, day, shutdown.Token);

        await using var sender = new GmailSender(config.Gmail);
        var admin = new AdminReporter(sender, config.AdminRecipients, log, stats);
        DayInfo? dayInfo = null;
        var endReason = "brak dalszych okien odpytywania";
        var exitCode = ExitOk;
        try
        {
            Directory.CreateDirectory(paths.DataDirectory);
            // Kopia przed migracją, żeby zachować stan bazy sprzed ewentualnej zmiany schematu.
            new DatabaseBackup(clock, log).Run(paths.DatabaseFile, paths.BackupDirectory);
            await using (var db = NotifierDbContext.Create(paths.DatabaseFile))
                await db.Database.MigrateAsync(dayGuard.Token);

            var holidays = await new SchoolHolidayProvider(paths.HolidayCacheFile, clock, log).LoadAsync(config.Voivodeship.Code, dayGuard.Token);
            var calendar = new SchoolCalendar(holidays);
            dayInfo = calendar.GetDay(day);
            log.Info($"Dzisiaj ({day:yyyy-MM-dd}): {dayInfo.DisplayName}.");

            var throttle = new RequestThrottle(clock);
            using var parent = new LibrusAccount(AccountKind.Parent, config.Parent, config.IdleLimit, clock, dayGuard, log, stats, throttle);
            using var student = new LibrusAccount(AccountKind.Student, config.Student, config.IdleLimit, clock, dayGuard, log, stats, throttle);
            var dispatcher = new NotificationDispatcher(sender, config.ParentRecipients, config.StudentRecipients, admin, clock, log, stats, calendar);
            var cycle = new NotifierCycle([parent, student], () => NotifierDbContext.Create(paths.DatabaseFile), dispatcher, sender, admin,
                clock, log, stats, studentNotifications: config.StudentRecipients.Count > 0);

            if (options.RunOnce)
            {
                dayGuard.Check();
                await cycle.RunAsync(forceFullReview: true, dayGuard.Token);
                endReason = "--run-once";
            }
            else
            {
                var scheduler = new PollingScheduler(day, PollingPlan.ForDay(dayInfo), Random.Shared);
                var digest = new DigestService(sender, config.ParentRecipients, config.StudentRecipients, () => NotifierDbContext.Create(paths.DatabaseFile),
                    calendar, clock, log, admin, stats);
                await new PingLoop(scheduler, cycle, clock, dayGuard, log, admin, digest: digest).RunAsync();
            }
        }
        catch (OperationCanceledException) when (dayGuard.DayChanged)
        {
            log.Info("Zmiana dnia – kończę działanie.");
            endReason = "zmiana dnia";
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            log.Info("Przerwano działanie (Ctrl+C / SIGTERM) – kończę bez podsumowania dnia.");
            return ExitInterrupted;
        }
        catch (Exception e)
        {
            log.Error($"Błąd krytyczny: {e}");
            await admin.ReportCriticalAsync($"fatal|{e.GetType().FullName}|{e.Message}", "błąd krytyczny aplikacji",
                $"Aplikacja kończy działanie z powodu błędu.\nBłąd: {NotifierCycle.Describe(e)}");
            endReason = "błąd krytyczny";
            exitCode = ExitError;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        // Wszystkie połączenia z bazą są już zamknięte; wynik vacuum trafia na koniec podsumowania dnia.
        var vacuum = DatabaseVacuum.RunIfDue(day, paths.DatabaseFile, log);
        await SendDailySummaryAsync(paths, clock, log, stats, admin, sender, dayInfo, endReason, config.AdminRecipients, vacuum);
        log.Info($"Koniec działania ({endReason}).");
        return exitCode;
    }

    private static async Task SendDailySummaryAsync(AppPaths paths, Clock clock, AppLog log, DailyStats stats, AdminReporter admin,
        IMailSender sender, DayInfo? dayInfo, string endReason, IReadOnlyList<string> recipients, VacuumReport? vacuum)
    {
        int? pending = null;
        try
        {
            if (File.Exists(paths.DatabaseFile))
            {
                await using var db = NotifierDbContext.Create(paths.DatabaseFile);
                pending = await db.Notifications.CountAsync(n => n.Status == NotificationStatus.Pending);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log.Warn($"Nie można policzyć oczekujących powiadomień: {e.Message}");
        }
        var mail = DailySummary.Build(stats.Snapshot(), dayInfo?.DisplayName, endReason, clock.Now, pending, recipients, vacuum);
        await admin.SendAsync(mail);
        await sender.CloseAsync();
    }

    private static IDisposable? TryRegisterSigterm(CancellationTokenSource shutdown)
    {
        try
        {
            return PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                shutdown.Cancel();
            });
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static void TryUseUtf8Console()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch (Exception e) when (e is IOException or PlatformNotSupportedException or System.Security.SecurityException)
        {
            // Pozostaje domyślne kodowanie konsoli.
        }
    }
}
