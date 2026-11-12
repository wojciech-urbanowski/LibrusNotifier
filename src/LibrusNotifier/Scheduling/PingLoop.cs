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
/// Zestawienia dzienne (<see cref="DigestService"/>) są sprawdzane przy każdym kroku oczekiwania (co najwyżej minuta) i tuż przed
/// każdym pingiem, więc wychodzą punktualnie, a w czasie trwania pinga (kilka minut) – zaraz po nim.
/// </remarks>
public sealed class PingLoop(PollingScheduler scheduler, NotifierCycle cycle, Clock clock, DayGuard dayGuard, IAppLog log, AdminReporter admin,
    Func<TimeSpan, CancellationToken, Task>? delay = null, DigestService? digest = null)
{
    private static readonly TimeSpan MaxWaitStep = TimeSpan.FromMinutes(1);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    /// <exception cref="OperationCanceledException">Zmiana dnia lub zamknięcie aplikacji.</exception>
    public async Task RunAsync()
    {
        log.Info("Okna odpytywania na dziś: " + string.Join("; ", scheduler.Windows) + ".");
        var next = scheduler.FirstPing(clock.Now);
        if (next is null)
        {
            log.Info("Dziś nie ma już okien odpytywania.");
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
            await RunDigestsAsync();
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
