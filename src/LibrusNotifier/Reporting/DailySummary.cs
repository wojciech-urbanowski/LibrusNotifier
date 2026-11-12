using System.Globalization;
using System.Text;
using LibrusNotifier.Data;
using LibrusNotifier.Librus;
using LibrusNotifier.Logging;
using LibrusNotifier.Mail;

namespace LibrusNotifier.Reporting;

/// <summary>Podsumowanie dnia dla administratorów, wysyłane przy zakończeniu działania.</summary>
public static class DailySummary
{
    private static readonly string[] DayNames = ["niedziela", "poniedziałek", "wtorek", "środa", "czwartek", "piątek", "sobota"];

    public static OutgoingMail Build(DailyStatsSnapshot stats, string? dayDescription, string endReason, DateTime endLocal, int? pendingNotifications,
        IReadOnlyList<string> recipients)
    {
        var body = new StringBuilder();
        body.AppendLine($"Podsumowanie dnia {stats.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} ({DayNames[(int)stats.Day.DayOfWeek]})");
        if (dayDescription is not null) body.AppendLine($"Typ dnia: {dayDescription}");
        body.AppendLine($"Działanie: {stats.StartedLocal:HH:mm:ss} – {endLocal:HH:mm:ss} ({endReason})");
        body.AppendLine();

        body.AppendLine($"Pingi: {stats.Pings} (w tym z przeglądem wszystkich list: {stats.FullReviews})");
        body.AppendLine($"Żądania HTTP do Librusa: {stats.TotalRequests} (rodzic: {stats.Requests.GetValueOrDefault(AccountKind.Parent)}, " +
            $"uczeń: {stats.Requests.GetValueOrDefault(AccountKind.Student)})");
        var failed = stats.FailedRequests.Values.Sum();
        if (failed > 0)
            body.AppendLine($"  w tym nieudanych: {failed} (rodzic: {stats.FailedRequests.GetValueOrDefault(AccountKind.Parent)}, " +
                $"uczeń: {stats.FailedRequests.GetValueOrDefault(AccountKind.Student)})");
        body.AppendLine($"Logowania: rodzic {stats.Logins.GetValueOrDefault(AccountKind.Parent)}, uczeń {stats.Logins.GetValueOrDefault(AccountKind.Student)}");
        body.AppendLine();

        body.AppendLine(stats.NewElements.Count == 0
            ? "Nowe elementy: brak"
            : "Nowe elementy: " + string.Join(", ", stats.NewElements.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key} {e.Value}")));
        if (stats.BaselineElements > 0) body.AppendLine($"Zapis początkowy (bez powiadomień): {stats.BaselineElements} elementów list");
        body.AppendLine($"Wysłane powiadomienia: rodzice {stats.MailsSent.GetValueOrDefault(RecipientGroup.Parents)}, " +
            $"uczeń {stats.MailsSent.GetValueOrDefault(RecipientGroup.Student)}; nieudane próby: {stats.MailFailures}");
        if (pendingNotifications is not null) body.AppendLine($"Oczekujące powiadomienia: {pendingNotifications}");
        body.AppendLine($"Maile do administratorów (przed podsumowaniem): {stats.AdminMails}");
        body.AppendLine();

        var errors = stats.Problems.Where(p => p.Level == LogLevel.Error).ToList();
        var warnings = stats.Problems.Where(p => p.Level == LogLevel.Warning).ToList();
        AppendProblems(body, "Błędy", errors);
        AppendProblems(body, "Ostrzeżenia", warnings);

        var subject = $"{AdminReporter.SubjectPrefix}Podsumowanie dnia {stats.Day:yyyy-MM-dd}" +
            (errors.Count > 0 ? $" – błędy: {errors.Sum(e => e.Count)}" : "");
        return new OutgoingMail(recipients, subject, body.ToString());
    }

    private static void AppendProblems(StringBuilder body, string title, IReadOnlyList<ProblemSummary> problems)
    {
        if (problems.Count == 0)
        {
            body.AppendLine($"{title}: brak");
            return;
        }
        body.AppendLine($"{title} ({problems.Sum(p => p.Count)}):");
        foreach (var problem in problems)
        {
            var message = problem.Message.Split('\n')[0];
            body.AppendLine($"  - {problem.FirstTime:HH:mm:ss}{(problem.Count > 1 ? $" (×{problem.Count})" : "")} {message}");
        }
        body.AppendLine();
    }
}
