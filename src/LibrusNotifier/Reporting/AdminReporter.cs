using LibrusNotifier.Logging;
using LibrusNotifier.Mail;

namespace LibrusNotifier.Reporting;

/// <summary>
/// Natychmiastowe maile do administratorów (<c>[odbiorcy_log]</c>) o błędach krytycznych. Ten sam błąd (wg klucza)
/// jest zgłaszany w ciągu dnia tylko raz; proces działa najwyżej do końca dnia, więc wystarcza pamięć procesu.
/// </summary>
public sealed class AdminReporter(IMailSender sender, IReadOnlyList<string> adminRecipients, IAppLog log, DailyStats stats)
{
    public const string SubjectPrefix = "[LibrusNotifier] ";

    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    /// <returns><c>true</c>, gdy mail został wysłany.</returns>
    public async Task<bool> ReportCriticalAsync(string key, string subject, string details)
    {
        lock (_reported)
            if (!_reported.Add(key)) return false;
        return await SendAsync(new OutgoingMail(adminRecipients, MailTemplates.CleanSubject(SubjectPrefix + "Błąd: " + subject),
            details + "\n\nTen sam błąd nie będzie dziś ponownie zgłaszany mailem; szczegóły są w logu lokalnym i w podsumowaniu dnia.\n"));
    }

    /// <summary>Wysyła mail do administratorów; błąd wysyłki trafia tylko do logu lokalnego.</summary>
    public async Task<bool> SendAsync(OutgoingMail mail)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await sender.SendAsync(mail with { To = adminRecipients }, timeout.Token);
            stats.RecordAdminMail();
            log.Info($"Wysłano mail do administratorów: {mail.Subject}");
            return true;
        }
        catch (Exception e)
        {
            log.Error($"Nie udało się wysłać maila do administratorów ({mail.Subject}): {e.Message}");
            return false;
        }
    }
}
