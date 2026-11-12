using System.Globalization;
using System.Net;
using System.Text;
using LibrusNotifier.Calendar;
using LibrusNotifier.Data;
using LibrusNotifier.Digest;
using LibrusNotifier.Librus;
using LibrusNotifier.Timing;

namespace LibrusNotifier.Mail;

/// <summary>
/// Treść maila z dziennym zestawieniem nadchodzących wydarzeń (HTML + tekst zapasowy). Pola z typem zdarzenia mają pastelowe kolory
/// (<see cref="DigestPalette"/>); sekcja główna (dziś / jutro / pierwszy dzień nauki po wolnym) jest wyróżniona w ramce.
/// Na końcu (przed stopką) jest tabela ocen z bieżącego semestru (<see cref="GradeTable"/>), kolorowana jak oceny w mailu o nowej ocenie.
/// </summary>
public static class DigestMailTemplates
{
    private const int MaxLabelsInSubject = 3;

    // Style wpisane w elementy (klienci poczty różnie traktują <style>). Kolory tła i tekstu mają tylko pola typu zdarzenia
    // (jasne tło + ciemny tekst), reszta dziedziczy motyw klienta; ramki i przygaszony tekst są w średnim szarym.
    private const string PageStyle = "margin:0;padding:16px;font-family:-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:14px;line-height:1.45;";
    private const string TableStyle = "border-collapse:collapse;width:100%;max-width:760px;margin:0 0 8px;";
    private const string HeadCellStyle = "border:1px solid #999999;padding:4px 10px;text-align:left;vertical-align:top;font-size:12px;font-weight:600;background:rgba(128,128,128,0.15);";
    private const string CellStyle = "border:1px solid #999999;padding:6px 10px;text-align:left;vertical-align:top;word-break:break-word;";
    private const string MutedStyle = "color:#777777;";
    private const string PrimaryBoxStyle = "margin:0 0 8px;padding:8px 14px;border-left:6px solid #4a7dbf;background:rgba(74,125,191,0.16);max-width:740px;";
    private const string BannerStyle = "margin:0 0 16px;padding:8px 14px;border:1px solid #c9a227;background:rgba(255,200,0,0.18);max-width:740px;";

    /// <param name="grades">Tabela ocen z bieżącego semestru na końcu maila (przed stopką); <c>null</c> (wakacje letnie) – bez tabeli.</param>
    public static OutgoingMail Build(DailyDigest digest, IReadOnlyList<string> to, Clock clock, SchoolCalendar calendar, DateTime? dataAsOfLocal = null,
        GradeTable? grades = null)
    {
        var dates = new DateAnnotator(clock, calendar);
        var subject = BuildSubject(digest);
        return new OutgoingMail(to, subject, BuildText(digest, dates, dataAsOfLocal, grades), BuildHtml(subject, digest, dates, dataAsOfLocal, grades));
    }

    public static string BuildSubject(DailyDigest digest)
    {
        var primary = digest.PrimarySection;
        var upcoming = digest.Upcoming.EventCount;
        string text;
        if (primary is null || digest.PrimaryDate is not { } date)
        {
            text = $"Nadchodzące wydarzenia: {upcoming}";
        }
        else
        {
            var iso = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var when = digest.Kind == DigestKind.Morning ? $"dziś, {DigestBuilder.DayName(date)} {iso}"
                : primary.Title == "Jutro" ? $"jutro, {DigestBuilder.DayName(date)} {iso}"
                : $"{DigestBuilder.DayNameAccusative(date)} {iso}";
            text = $"Zdarzenia na {when}: {Summary(primary)}; kolejne: {upcoming}";
        }
        return MailTemplates.CleanSubject(MailTemplates.SubjectPrefix + text);
    }

    private static string Summary(DigestSection primary)
    {
        var events = primary.Days.SelectMany(d => d.Events).ToList();
        if (events.Count == 0) return "brak";
        // Rodzaje (nie własne etykiety wpisów), od najważniejszego: sprawdzian, kartkówka, zadanie domowe, zastępstwo itd.
        var labels = events.Select(e => e.Category).Distinct().Order().Select(c => DigestPalette.LegendLabel(c).ToLowerInvariant()).ToList();
        var shown = string.Join(", ", labels.Take(MaxLabelsInSubject)) + (labels.Count > MaxLabelsInSubject ? "…" : "");
        return $"{events.Count} ({shown})";
    }

    // ---------- tekst ----------

    private static string BuildText(DailyDigest digest, DateAnnotator dates, DateTime? dataAsOf, GradeTable? grades)
    {
        var text = new StringBuilder();
        text.AppendLine("Nadchodzące wydarzenia").AppendLine();
        if (digest.Banner is not null) text.AppendLine(digest.Banner).AppendLine();

        foreach (var section in digest.Sections)
        {
            var heading = section.Subtitle is null ? section.Title : $"{section.Title} - {section.Subtitle}";
            text.AppendLine(heading.ToUpperInvariant()).AppendLine(new string('=', Math.Min(heading.Length, 60)));
            if (section.EventCount == 0)
            {
                text.AppendLine(section.EmptyText).AppendLine();
                continue;
            }
            foreach (var day in section.Days)
            {
                if (!section.Primary) text.AppendLine(DigestBuilder.DayHeading(day.Date, dates));
                foreach (var e in day.Events)
                    text.AppendLine($"  - {PlainRow(e, dates)}");
                text.AppendLine();
            }
        }

        if (grades is not null) AppendGradesText(text, grades);

        if (dataAsOf is { } asOf) text.AppendLine($"Stan danych z Librusa: {asOf:yyyy-MM-dd HH:mm}");
        text.AppendLine("-- ").AppendLine("Wiadomość wysłana automatycznie przez LibrusNotifier.");
        return text.ToString();
    }

    private static void AppendGradesText(StringBuilder text, GradeTable grades)
    {
        var heading = GradesHeading.ToUpperInvariant();
        text.AppendLine(heading).AppendLine(new string('=', heading.Length)).AppendLine(GradesSubtitle(grades));
        if (grades.Rows.Count == 0)
        {
            text.AppendLine().AppendLine(NoGradesText).AppendLine();
            return;
        }
        text.AppendLine();
        foreach (var row in grades.Rows) text.AppendLine($"{row.Subject}: {string.Join(", ", row.Marks)}");
        text.AppendLine();
    }

    private static string PlainRow(DigestEvent e, DateAnnotator dates)
    {
        var parts = new List<string> { e.Label };
        if (e.Lesson is { } lesson) parts.Add($"lekcja {lesson}" + (e.Hours.Length > 0 ? $" ({e.Hours})" : ""));
        else if (e.Hours.Length > 0) parts.Add(e.Hours);
        if (e.Subject.Length > 0) parts.Add(e.Subject);
        if (e.Detail.Length > 0) parts.Add(dates.Plain(e.Detail.Replace("\r", "").Replace("\n", "; ")));
        if (e.Note.Length > 0) parts.Add(e.Note);
        return string.Join(" · ", parts);
    }

    // ---------- HTML ----------

    private static string BuildHtml(string title, DailyDigest digest, DateAnnotator dates, DateTime? dataAsOf, GradeTable? grades)
    {
        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>")
            .AppendLine("<html lang=\"pl\">")
            .AppendLine("<head>")
            .AppendLine("<meta charset=\"utf-8\">")
            .AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .AppendLine("<meta name=\"color-scheme\" content=\"light dark\">")
            .AppendLine("<meta name=\"supported-color-schemes\" content=\"light dark\">")
            .AppendLine($"<title>{Encode(title)}</title>")
            .AppendLine("</head>")
            .AppendLine($"<body style=\"{PageStyle}\">")
            .AppendLine("<h1 style=\"margin:0 0 12px;font-size:18px;font-weight:600;\">Nadchodzące wydarzenia</h1>");

        if (digest.Banner is not null) html.AppendLine($"<div style=\"{BannerStyle}\">{Encode(digest.Banner)}</div>");

        foreach (var section in digest.Sections)
        {
            if (section.Primary)
            {
                html.AppendLine($"<div style=\"{PrimaryBoxStyle}\">")
                    .AppendLine($"<div style=\"font-size:22px;font-weight:700;line-height:1.25;\">{Encode(section.Title)}</div>");
                if (section.Subtitle is not null) html.AppendLine($"<div style=\"font-size:12px;{MutedStyle}\">{Encode(section.Subtitle)}</div>");
                html.AppendLine("</div>");
            }
            else
            {
                html.AppendLine($"<h2 style=\"margin:24px 0 4px;font-size:16px;font-weight:600;\">{Encode(section.Title)}</h2>");
            }

            if (section.EventCount == 0)
            {
                html.AppendLine($"<p style=\"margin:0 0 16px;{MutedStyle}\"><em>{Encode(section.EmptyText)}</em></p>");
                continue;
            }
            foreach (var day in section.Days)
            {
                if (!section.Primary)
                    html.AppendLine($"<h3 style=\"margin:14px 0 6px;font-size:14px;font-weight:600;\">{Encode(DigestBuilder.DayHeading(day.Date, dates))}</h3>");
                AppendTable(html, day.Events, dates);
            }
        }

        AppendLegend(html, digest);
        if (grades is not null) AppendGradesHtml(html, grades);
        html.AppendLine($"<p style=\"margin:16px 0 0;font-size:12px;{MutedStyle}\">" +
            (dataAsOf is { } asOf ? $"Stan danych z Librusa: {asOf:yyyy-MM-dd HH:mm}. " : "") +
            "Wiadomość wysłana automatycznie przez LibrusNotifier.</p>")
            .AppendLine("</body>")
            .AppendLine("</html>");
        return html.ToString();
    }

    private static void AppendTable(StringBuilder html, IReadOnlyList<DigestEvent> events, DateAnnotator dates)
    {
        html.AppendLine($"<table style=\"{TableStyle}\">").AppendLine("<thead>").AppendLine("<tr>");
        html.AppendLine($"<th scope=\"col\" style=\"{HeadCellStyle}width:140px;\">Typ</th>")
            .AppendLine($"<th scope=\"col\" style=\"{HeadCellStyle}width:90px;\">Lekcja</th>")
            .AppendLine($"<th scope=\"col\" style=\"{HeadCellStyle}\">Szczegóły</th>")
            .AppendLine("</tr>").AppendLine("</thead>").AppendLine("<tbody>");
        foreach (var e in events)
        {
            html.AppendLine("<tr>");
            html.AppendLine($"<td style=\"{CellStyle}{BadgeStyle(e.Category)}\">{Encode(e.Label)}</td>")
                .AppendLine($"<td style=\"{CellStyle}\">{LessonCell(e)}</td>")
                .AppendLine($"<td style=\"{CellStyle}\">{DetailsCell(e, dates)}</td>")
                .AppendLine("</tr>");
        }
        html.AppendLine("</tbody>").AppendLine("</table>");
    }

    private static string BadgeStyle(EventCategory category) =>
        $"background:{DigestPalette.Background(category)};color:{DigestPalette.TextColor};font-weight:{(DigestPalette.Bold(category) ? "700" : "600")};white-space:nowrap;";

    private static string LessonCell(DigestEvent e)
    {
        var lesson = e.Lesson is { } number ? $"<strong>{number}</strong>" : "";
        var hours = e.Hours.Length > 0 ? $"<span style=\"font-size:12px;{MutedStyle}\">{Encode(e.Hours)}</span>" : "";
        if (lesson.Length == 0 && hours.Length == 0) return $"<span style=\"{MutedStyle}\">-</span>";
        return lesson.Length > 0 && hours.Length > 0 ? lesson + "<br>" + hours : lesson + hours;
    }

    private static string DetailsCell(DigestEvent e, DateAnnotator dates)
    {
        var lines = new List<string>();
        if (e.Subject.Length > 0) lines.Add($"<strong>{Encode(e.Subject)}</strong>");
        if (e.Detail.Length > 0) lines.Add(dates.Html(e.Detail));
        if (e.Note.Length > 0) lines.Add($"<span style=\"font-size:12px;{MutedStyle}\">{Encode(e.Note)}</span>");
        return lines.Count == 0 ? "&nbsp;" : string.Join("<br>\n", lines);
    }

    private static void AppendLegend(StringBuilder html, DailyDigest digest)
    {
        var used = digest.Sections.SelectMany(s => s.Days).SelectMany(d => d.Events).Select(e => e.Category).Distinct().Order().ToList();
        if (used.Count == 0) return;
        html.Append($"<p style=\"margin:16px 0 0;font-size:12px;{MutedStyle}\">Kolory: ");
        html.Append(string.Join(" ", used.Select(c =>
            $"<span style=\"display:inline-block;padding:1px 8px;margin:0 2px 2px 0;{BadgeStyle(c)}\">{Encode(DigestPalette.LegendLabel(c))}</span>")));
        html.AppendLine("</p>");
    }

    // ---------- tabela ocen ----------

    private const string GradesHeading = "Oceny z bieżącego semestru";
    private const string NoGradesText = "Brak ocen w bieżącym semestrze.";
    // Nazwa przedmiotu może się zawijać (na wąskim ekranie tabela z wieloma ocenami i tak jest szeroka); oceny są zawsze w jednej linii.
    private const string GradeLabelCellStyle = "border:1px solid #999999;padding:4px 8px;text-align:left;vertical-align:top;font-weight:600;background:rgba(128,128,128,0.15);";
    private const string GradeCellStyle = "border:1px solid #999999;padding:4px 6px;min-width:18px;text-align:center;vertical-align:top;white-space:nowrap;";

    private static string GradesSubtitle(GradeTable grades) =>
        $"Semestr {grades.Semester}, rok szkolny {grades.SchoolYearName}; oceny od najstarszej do najnowszej.";

    /// <summary>
    /// Tabela na końcu maila: wiersz na przedmiot (alfabetycznie), w nim kolejne oceny z semestru (chronologicznie), każda w osobnej
    /// komórce z kolorem wg skali (<see cref="GradePalette"/>, tak samo jak w mailu o nowej ocenie). Wiersze mają różną liczbę ocen,
    /// więc nagłówek „Oceny” obejmuje najdłuższy z nich, a krótsze wiersze po prostu się kończą.
    /// </summary>
    private static void AppendGradesHtml(StringBuilder html, GradeTable grades)
    {
        html.AppendLine($"<h2 style=\"margin:24px 0 4px;font-size:16px;font-weight:600;\">{Encode(GradesHeading)}</h2>")
            .AppendLine($"<p style=\"margin:0 0 8px;font-size:12px;{MutedStyle}\">{Encode(GradesSubtitle(grades))}</p>");
        if (grades.Rows.Count == 0)
        {
            html.AppendLine($"<p style=\"margin:0 0 16px;{MutedStyle}\"><em>{Encode(NoGradesText)}</em></p>");
            return;
        }

        html.AppendLine("<table style=\"border-collapse:collapse;margin:0 0 8px;\">")
            .AppendLine("<thead>")
            .AppendLine($"<tr><th scope=\"col\" style=\"{HeadCellStyle}\">Przedmiot</th><th scope=\"col\" colspan=\"{grades.Columns}\" style=\"{HeadCellStyle}\">Oceny</th></tr>")
            .AppendLine("</thead>")
            .AppendLine("<tbody>");
        foreach (var row in grades.Rows)
        {
            html.Append($"<tr><th scope=\"row\" style=\"{GradeLabelCellStyle}\">{Encode(row.Subject)}</th>");
            foreach (var mark in row.Marks) html.Append($"<td style=\"{GradeCellStyle}{GradePalette.CellStyle(mark)}\">{Encode(mark)}</td>");
            html.AppendLine("</tr>");
        }
        html.AppendLine("</tbody>").AppendLine("</table>");
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
