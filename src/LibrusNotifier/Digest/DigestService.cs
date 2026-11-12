using LibrusNotifier.Calendar;
using LibrusNotifier.Data;
using LibrusNotifier.Librus;
using LibrusNotifier.Logging;
using LibrusNotifier.Mail;
using LibrusNotifier.Processing;
using LibrusNotifier.Reporting;
using LibrusNotifier.Timing;
using Microsoft.EntityFrameworkCore;

namespace LibrusNotifier.Digest;

/// <summary>Stałe terminy zestawień (czas lokalny Europe/Warsaw); nie podlegają konfiguracji.</summary>
public static class DigestSchedule
{
    public static readonly TimeSpan MorningAt = new(6, 30, 0);
    public static readonly TimeSpan AfternoonAt = new(15, 0, 0);

    /// <summary>
    /// Zestawienie, którego termin minął podczas przerwy w pracy aplikacji (restart), jest jeszcze wysyłane przez tyle czasu;
    /// później byłoby nieaktualne (poranne „na dziś” po rozpoczęciu lekcji), więc jest pomijane.
    /// </summary>
    public static readonly TimeSpan MaxLateness = TimeSpan.FromHours(2);

    /// <summary>Pauza po nieudanej wysyłce, zanim aplikacja spróbuje ponownie (pętla sprawdza terminy co minutę).</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    public static TimeSpan DueTime(DigestKind kind) => kind == DigestKind.Morning ? MorningAt : AfternoonAt;

    /// <summary>Poranne tylko w dni nauki (pn–pt poza dniami wolnymi, świętami i feriami); popołudniowe każdego dnia.</summary>
    public static bool AppliesTo(DigestKind kind, DayType today) => kind == DigestKind.Afternoon || today == DayType.School;
}

/// <summary>
/// Wysyła dzienne zestawienia nadchodzących wydarzeń: poranne o 6:30 i popołudniowe o 15:00, osobno dla rodziców i dla ucznia
/// (uczeń widzi tylko wydarzenia z elementów widocznych na jego koncie). Dane pochodzą z bazy, bez żądań do Librusa.
/// Zestawienie popołudniowe w wakacje i ferie jest wysyłane tylko wtedy, gdy są jakiekolwiek przyszłe wydarzenia.
/// Wysłane zestawienie jest zapisywane w bazie (<see cref="DigestLogEntity"/>), więc restart aplikacji nie powoduje ponownej wysyłki.
/// </summary>
public sealed class DigestService(IMailSender sender, IReadOnlyList<string> parentRecipients, IReadOnlyList<string> studentRecipients,
    Func<NotifierDbContext> dbFactory, SchoolCalendar calendar, Clock clock, IAppLog log, AdminReporter admin, DailyStats stats)
{
    private readonly HashSet<(DateOnly Day, DigestKind Kind, RecipientGroup Group)> _resolved = [];
    private readonly Dictionary<(DateOnly Day, DigestKind Kind, RecipientGroup Group), DateTime> _retryAt = [];

    /// <summary>Wysyła zestawienia, których termin już minął, a które jeszcze nie zostały wysłane. Tanie, gdy nic nie jest należne.</summary>
    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        var now = clock.Now;
        var today = DateOnly.FromDateTime(now);
        var todayType = calendar.GetDayType(today);

        foreach (var kind in new[] { DigestKind.Morning, DigestKind.Afternoon })
        {
            if (!DigestSchedule.AppliesTo(kind, todayType)) continue;
            var due = today.ToDateTime(TimeOnly.FromTimeSpan(DigestSchedule.DueTime(kind)));
            if (now < due) continue;

            foreach (var group in new[] { RecipientGroup.Parents, RecipientGroup.Student })
            {
                var key = (today, kind, group);
                if (_resolved.Contains(key)) continue;
                if (_retryAt.TryGetValue(key, out var retryAt) && now < retryAt) continue;
                await RunOneAsync(kind, group, today, todayType, due, now, cancellationToken);
            }
        }
    }

    private async Task RunOneAsync(DigestKind kind, RecipientGroup group, DateOnly today, DayType todayType, DateTime due, DateTime now,
        CancellationToken ct)
    {
        var key = (today, kind, group);
        var recipients = group == RecipientGroup.Parents ? parentRecipients : studentRecipients;
        var name = $"{KindName(kind)}, {NotificationDispatcher.GroupName(group)}";
        if (recipients.Count == 0)
        {
            _resolved.Add(key);
            return;
        }

        try
        {
            await using var db = dbFactory();
            if (await db.DigestLogs.AnyAsync(d => d.Day == today && d.Kind == kind && d.Group == group, ct))
            {
                _resolved.Add(key);
                return;
            }
            if (now >= due + DigestSchedule.MaxLateness)
            {
                log.Info($"Zestawienie ({name}) pominięte: termin {due:HH:mm} minął ponad {DigestSchedule.MaxLateness.TotalHours:0} h temu, a nie zostało wysłane.");
                _resolved.Add(key);
                return;
            }

            var elements = await db.Elements.AsNoTracking().Where(e => DigestEventBuilder.SourceTypes.Contains(e.Type)).ToListAsync(ct);
            var baselines = await db.Baselines.AsNoTracking().ToListAsync(ct);
            var events = DigestEventBuilder.Build(elements, baselines, group, calendar);
            var digest = DigestBuilder.Build(kind, events, clock, calendar);

            if (kind == DigestKind.Afternoon && todayType == DayType.Holiday && !digest.HasFutureEvents)
            {
                log.Info($"Zestawienie ({name}) pominięte: wakacje/ferie i brak przyszłych wydarzeń.");
                _resolved.Add(key);
                return;
            }

            var mail = DigestMailTemplates.Build(digest, recipients, clock, calendar, DataAsOf(baselines));
            try
            {
                await sender.SendAsync(mail, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _retryAt[key] = now + DigestSchedule.RetryDelay;
                stats.RecordMail(group, success: false);
                log.Error($"Nieudana wysyłka zestawienia ({name}): {e.Message}");
                await admin.ReportCriticalAsync($"digest|{kind}|{group}|{e.GetType().FullName}|{e.Message}", "nieudana wysyłka zestawienia",
                    $"Nie udało się wysłać zestawienia „{KindName(kind)}” do grupy „{NotificationDispatcher.GroupName(group)}”.\nBłąd: {e.Message}\n\n" +
                    $"Aplikacja ponowi próbę za {DigestSchedule.RetryDelay.TotalMinutes:0} min (najdłużej {DigestSchedule.MaxLateness.TotalHours:0} h po terminie).");
                return;
            }
            finally
            {
                await sender.CloseAsync();
            }

            // Po wysyłce zestawienie jest już „załatwione” w tym procesie, nawet gdyby zapis znacznika w bazie się nie udał.
            _resolved.Add(key);
            stats.RecordDigest(group);
            log.Info($"Wysłano zestawienie ({name}, {recipients.Count} adr.): {mail.Subject}");
            db.DigestLogs.Add(new DigestLogEntity
            {
                Day = today, Kind = kind, Group = group, SentUtc = clock.UtcNow.UtcDateTime, EventCount = digest.Sections.Sum(s => s.EventCount)
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _retryAt[key] = now + DigestSchedule.RetryDelay;
            log.Error($"Błąd przygotowania zestawienia ({name}): {e}");
            await admin.ReportCriticalAsync($"digest-build|{kind}|{group}|{e.GetType().FullName}|{e.Message}", "błąd przygotowania zestawienia",
                $"Błąd: {e.Message}\n\nAplikacja ponowi próbę za {DigestSchedule.RetryDelay.TotalMinutes:0} min.");
        }
    }

    /// <summary>Kiedy ostatnio pobrano z Librusa terminarz lub plan lekcji (czas lokalny) – widać po nim, jak świeże są dane.</summary>
    private DateTime? DataAsOf(IReadOnlyList<BaselineEntity> baselines)
    {
        var fetches = baselines.Where(b => b.Module is ModuleKind.Schedule).Select(b => b.LastFetchUtc).ToList();
        return fetches.Count == 0 ? null : clock.ToLocal(new DateTimeOffset(fetches.Max(), TimeSpan.Zero));
    }

    private static string KindName(DigestKind kind) => kind == DigestKind.Morning ? "poranne" : "popołudniowe";

}
