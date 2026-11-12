using LibrusNotifier.Digest;
using LibrusNotifier.Logging;
using LibrusNotifier.Processing;
using LibrusNotifier.Reporting;
using LibrusNotifier.Timing;

namespace LibrusNotifier.Scheduling;

/// <summary>
/// Pętla pingów jednego dnia: pierwszy ping od razu, gdy start wypada w oknie (inaczej na początku następnego okna),
/// kolejne wg interwału. Kończy się, gdy do końca dnia nie ma już terminu w oknach.
/// </summary>
/// <remarks>
/// Zestawienia dzienne (<see cref="DigestService"/>) mają zawsze świeże dane, bo ping (oba konta: rodzica i ucznia, razem z wysyłką
/// powiadomień) jest zawsze przed nimi:
/// <list type="bullet">
/// <item>trwający ping jest dokańczany, a zestawienie należne w jego trakcie wychodzi zaraz po nim;</item>
/// <item>gdy ping i zestawienie są należne jednocześnie, najpierw jest ping;</item>
/// <item>zestawienie, którego termin minął przed startem aplikacji (także w nowej, pustej bazie), wychodzi po pierwszym pingu;
/// zestawienia nie są wysyłane, zanim ten ping się odbędzie (udany czy nie – zestawienie korzysta z danych w bazie);</item>
/// <item>dopiero gdy dziś nie będzie już żadnego pinga (start po ostatnim oknie), należne zestawienia wychodzą od razu.</item>
/// </list>
/// W czasie oczekiwania na kolejny ping terminy zestawień są sprawdzane co najwyżej co minutę, więc wychodzą punktualnie.
/// </remarks>
public sealed class PingLoop(PollingScheduler scheduler, NotifierCycle cycle, Clock clock, DayGuard dayGuard, IAppLog log, AdminReporter admin,
    Func<TimeSpan, CancellationToken, Task>? delay = null, DigestService? digest = null)
{
    private static readonly TimeSpan MaxWaitStep = TimeSpan.FromMinutes(1);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private bool _pinged;

    /// <exception cref="OperationCanceledException">Zmiana dnia lub zamknięcie aplikacji.</exception>
    public async Task RunAsync()
    {
        log.Info("Okna odpytywania na dziś: " + string.Join("; ", scheduler.Windows) + ".");
        var next = scheduler.FirstPing(clock.Now);
        if (next is null)
        {
            log.Info("Dziś nie ma już okien odpytywania.");
            // Nic nie odświeży danych, więc zestawienia należne od dawna wychodzą z tym, co jest w bazie.
            await RunDigestsAsync();
            return;
        }
        if (next > clock.Now) log.Info($"Poza oknem odpytywania – czekam do {next:HH:mm}.");

        while (next is { } at)
        {
            await WaitUntilAsync(at);
            var started = clock.Now;
            dayGuard.Check();
            try
            {
                await cycle.RunAsync(forceFullReview: false, dayGuard.Token);
            }
            catch (OperationCanceledException) when (dayGuard.Token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                log.Error($"Nieoczekiwany błąd cyklu: {e}");
                await admin.ReportCriticalAsync($"cycle|{e.GetType().FullName}|{e.Message}", "nieoczekiwany błąd cyklu",
                    $"Błąd: {NotifierCycle.Describe(e)}\n\nAplikacja działa dalej wg harmonogramu.");
            }
            _pinged = true;
            await RunDigestsAsync();
            next = scheduler.NextPing(started, clock.Now);
            log.Info(next is { } value ? $"Następny ping o {value:HH:mm:ss}." : "Do końca dnia nie ma już terminów w oknach odpytywania.");
        }
    }

    private async Task WaitUntilAsync(DateTime localTarget)
    {
        var target = clock.ToUtc(localTarget);
        while (true)
        {
            dayGuard.Check();
            // Ping jest należny: najpierw ping, zestawienia zaraz po nim.
            if (target - clock.UtcNow <= TimeSpan.Zero) return;
            if (_pinged) await RunDigestsAsync();
            var remaining = target - clock.UtcNow;
            if (remaining <= TimeSpan.Zero) return;
            await _delay(remaining < MaxWaitStep ? remaining : MaxWaitStep, dayGuard.Token);
        }
    }

    /// <summary>Błąd zestawienia nie zatrzymuje odpytywania Librusa (usługa sama zgłasza go administratorom).</summary>
    private async Task RunDigestsAsync()
    {
        if (digest is null) return;
        try
        {
            await digest.RunDueAsync(dayGuard.Token);
        }
        catch (OperationCanceledException) when (dayGuard.Token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            log.Error($"Nieoczekiwany błąd zestawień dziennych: {e}");
        }
    }
}
