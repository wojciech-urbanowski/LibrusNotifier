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

    /// <summary>Pauza po nieudanej wysyłce, zanim aplikacja spróbuje ponownie (pętla sprawdza terminy co minutę).</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    public static TimeSpan DueTime(DigestKind kind) => kind == DigestKind.Morning ? MorningAt : AfternoonAt;

    /// <summary>
    /// Poranne „na dziś” wychodzi (także z opóźnieniem po starcie aplikacji) tylko do terminu popołudniowego; od 15:00 zastępuje je
    /// popołudniowe, więc gdy żadne z nich nie zostało dziś wysłane, wychodzi wyłącznie popołudniowe.
    /// </summary>
    public static bool IsSuperseded(DigestKind kind, DateTime now) => kind == DigestKind.Morning && now.TimeOfDay >= AfternoonAt;

    /// <summary>Poranne tylko w dni nauki (pn–pt poza dniami wolnymi, świętami i feriami); popołudniowe każdego dnia.</summary>
    public static bool AppliesTo(DigestKind kind, DayType today) => kind == DigestKind.Afternoon || today == DayType.School;
}

/// <summary>
/// Wysyła dzienne zestawienia nadchodzących wydarzeń: poranne o 6:30 i popołudniowe o 15:00, osobno dla rodziców i dla ucznia
/// (uczeń widzi tylko wydarzenia z elementów widocznych na jego koncie). Dane pochodzą z bazy, bez żądań do Librusa.
/// Zestawienie popołudniowe w wakacje i ferie jest wysyłane tylko wtedy, gdy są jakiekolwiek przyszłe wydarzenia.
/// Na końcu każdego zestawienia jest tabela ocen z bieżącego semestru (<see cref="GradeTable"/>), też z bazy.
/// Wysłane zestawienie jest zapisywane w bazie (<see cref="DigestLogEntity"/>), więc restart aplikacji nie powoduje ponownej wysyłki,
/// a w nowo założonej bazie (bez wpisów) każde zestawienie, którego termin już minął, jest należne.
/// Zestawienie, którego termin minął przed startem aplikacji, wychodzi po pierwszym pingu (kolejność pilnuje <see cref="Scheduling.PingLoop"/>)
/// bez limitu opóźnienia; poranne tylko do 15:00 (<see cref="DigestSchedule.IsSuperseded"/>). Dopóki z konta grupy nie pobrano
/// terminarza ani razu (nowa baza, nieudany pierwszy ping), zestawienie czeka, żeby nie obwieścić „brak zdarzeń” bez danych.
/// </summary>
public sealed class DigestService(IMailSender sender, IReadOnlyList<string> parentRecipients, IReadOnlyList<string> studentRecipients,
    Func<NotifierDbContext> dbFactory, SchoolCalendar calendar, Clock clock, IAppLog log, AdminReporter admin, DailyStats stats)
{
    private readonly HashSet<(DateOnly Day, DigestKind Kind, RecipientGroup Group)> _resolved = [];
    private readonly Dictionary<(DateOnly Day, DigestKind Kind, RecipientGroup Group), DateTime> _retryAt = [];
    private readonly HashSet<(DateOnly Day, DigestKind Kind, RecipientGroup Group)> _waitingForDataLogged = [];

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
                await RunOneAsync(kind, group, today, todayType, now, cancellationToken);
            }
        }
    }

    private async Task RunOneAsync(DigestKind kind, RecipientGroup group, DateOnly today, DayType todayType, DateTime now, CancellationToken ct)
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
            if (DigestSchedule.IsSuperseded(kind, now))
            {
                log.Info($"Zestawienie ({name}) pominięte: jest już po {DigestSchedule.AfternoonAt:hh\\:mm}, więc wychodzi tylko popołudniowe.");
                _resolved.Add(key);
                return;
            }

            // Bez ani jednego pobrania terminarza (nowa baza, pierwszy ping nieudany) zestawienie byłoby fałszywym „brak zdarzeń”.
            var account = group == RecipientGroup.Parents ? AccountKind.Parent : AccountKind.Student;
            if (!await db.Baselines.AnyAsync(b => b.Account == account && b.Module == ModuleKind.Schedule, ct))
            {
                if (_waitingForDataLogged.Add(key))
                    log.Info($"Zestawienie ({name}) czeka: terminarz konta {account.DisplayName()} nie został jeszcze pobrany z Librusa.");
                return;
            }

            var elements = await db.Elements.AsNoTracking().Where(e => DigestEventBuilder.SourceTypes.Contains(e.Type)).ToListAsync(ct);
            var grades = await db.Elements.AsNoTracking().Where(e => e.Type == ElementTypes.Grade).ToListAsync(ct);
            var baselines = await db.Baselines.AsNoTracking().ToListAsync(ct);
            var events = DigestEventBuilder.Build(elements, baselines, group, calendar);
            var digest = DigestBuilder.Build(kind, events, clock, calendar);

            if (kind == DigestKind.Afternoon && todayType == DayType.Holiday && !digest.HasFutureEvents)
            {
                log.Info($"Zestawienie ({name}) pominięte: wakacje/ferie i brak przyszłych wydarzeń.");
                _resolved.Add(key);
                return;
            }

            var gradeTable = GradeTable.Build(grades, baselines, group, today);
            var mail = DigestMailTemplates.Build(digest, recipients, clock, calendar, DataAsOf(baselines), gradeTable);
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
                    $"Aplikacja ponowi próbę za {DigestSchedule.RetryDelay.TotalMinutes:0} min " +
                    (kind == DigestKind.Morning ? $"(poranne najdłużej do {DigestSchedule.AfternoonAt:hh\\:mm}, potem wychodzi tylko popołudniowe)." : "(do końca dnia)."));
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
