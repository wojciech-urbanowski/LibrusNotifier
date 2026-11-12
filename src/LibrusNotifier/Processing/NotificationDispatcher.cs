using LibrusNotifier.Calendar;
using LibrusNotifier.Data;
using LibrusNotifier.Logging;
using LibrusNotifier.Mail;
using LibrusNotifier.Reporting;
using LibrusNotifier.Timing;
using Microsoft.EntityFrameworkCore;

namespace LibrusNotifier.Processing;

/// <summary>
/// Wysyła oczekujące powiadomienia, od najstarszych, najwyżej <see cref="MaxMailsPerCycle"/> maili na cykl (łącznie dla grup).
/// Znacznik „wysłano” jest zapisywany dopiero po udanej wysyłce; pozostałe czekają na kolejne pingi.
/// </summary>
public sealed class NotificationDispatcher(IMailSender sender, IReadOnlyList<string> parentRecipients, IReadOnlyList<string> studentRecipients,
    AdminReporter admin, Clock clock, IAppLog log, DailyStats stats, SchoolCalendar? calendar = null)
{
    public const int MaxMailsPerCycle = 20;

    public async Task<int> DispatchAsync(NotifierDbContext db, CancellationToken cancellationToken)
    {
        var pending = await db.Notifications
            .Include(n => n.Element)
            .Where(n => n.Status == NotificationStatus.Pending)
            .OrderBy(n => n.Element.FirstSeenUtc).ThenBy(n => n.ElementId).ThenBy(n => n.Id)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0) return 0;

        var attempts = 0;
        var sent = 0;
        foreach (var notification in pending)
        {
            var recipients = notification.Group == RecipientGroup.Parents ? parentRecipients : studentRecipients;
            if (recipients.Count == 0)
            {
                notification.Status = NotificationStatus.Skipped;
                notification.LastError = "Brak adresów odbiorców tej grupy.";
                await db.SaveChangesAsync(CancellationToken.None);
                continue;
            }
            if (attempts >= MaxMailsPerCycle) break;
            attempts++;

            var mail = MailTemplates.ForElement(notification.Element, recipients, clock, calendar);
            var group = GroupName(notification.Group);
            try
            {
                await sender.SendAsync(mail, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                notification.Attempts++;
                notification.LastError = e.Message.Length > 500 ? e.Message[..500] : e.Message;
                await db.SaveChangesAsync(CancellationToken.None);
                stats.RecordMail(notification.Group, success: false);
                log.Error($"Nieudana wysyłka powiadomienia ({group}): {mail.Subject}: {e.Message}");
                await admin.ReportCriticalAsync($"mail|{e.GetType().FullName}|{e.Message}", "nieudana wysyłka powiadomienia",
                    $"Nie udało się wysłać powiadomienia do grupy „{group}”.\nBłąd: {e.Message}\n\nPowiadomienie zostanie ponowione w kolejnym pingu.");
                if (e is MailTransportException) break;
                continue;
            }

            notification.Attempts++;
            notification.Status = NotificationStatus.Sent;
            notification.SentUtc = clock.UtcNow.UtcDateTime;
            notification.LastError = null;
            await db.SaveChangesAsync(CancellationToken.None);
            sent++;
            stats.RecordMail(notification.Group, success: true);
            log.Info($"Wysłano powiadomienie ({group}, {recipients.Count} adr.): {mail.Subject}");
        }

        var remaining = pending.Count(n => n.Status == NotificationStatus.Pending);
        log.Info($"Powiadomienia: wysłano {sent}, oczekuje {remaining}.");
        return sent;
    }

    public static string GroupName(RecipientGroup group) => group == RecipientGroup.Parents ? "rodzice" : "uczeń";
}
