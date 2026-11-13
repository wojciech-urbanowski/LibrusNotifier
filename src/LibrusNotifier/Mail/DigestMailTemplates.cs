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
/// Treść maila z dziennym zestawieniem nadchodzących wydarzeń (HTML + tekst zapasowy). Każde wydarzenie to osobna ramka (karta) z paskiem
/// i plakietką typu w pastelowym kolorze rodzaju (<see cref="DigestPalette"/>); karta zajmuje całą szerokość, bo maile czyta się głównie
/// na telefonie. Sekcja główna (dziś / jutro / pierwszy dzień nauki po wolnym) jest wyróżniona w ramce.
/// W zestawieniu popołudniowym na końcu (przed stopką) jest lista ocen z bieżącego semestru (<see cref="GradeTable"/>), kolorowana
/// jak oceny w mailu o nowej ocenie; poranne („Odprawa poranna”) jej nie ma (wywołujący przekazuje wtedy <c>null</c>).
/// </summary>
public static class DigestMailTemplates
{
    private const int MaxLabelsInSubject = 3;

    // Style wpisane w elementy (klienci poczty różnie traktują <style>). Kolory tła i tekstu mają tylko pola typu zdarzenia
    // (jasne tło + ciemny tekst), reszta dziedziczy motyw klienta; ramki i przygaszony tekst są w średnim szarym.
    private const string PageStyle = "margin:0;padding:16px;font-family:-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:14px;line-height:1.45;";
    private const string TableStyle = "border-collapse:collapse;width:100%;max-width:760px;margin:0 0 8px;";
    // Karta wydarzenia: ramka z kolorowym paskiem z lewej (kolor dopisywany osobno); długie słowa (np. adresy) łamią się, zamiast poszerzać mail.
    private const string CardCellStyle = "border:1px solid #999999;padding:8px 10px;text-align:left;vertical-align:top;word-break:break-word;overflow-wrap:anywhere;";
    private const string MutedStyle = "color:#777777;";
    private const string PrimaryBoxStyle = "margin:0 0 8px;padding:8px 14px;border-left:6px solid #4a7dbf;background:rgba(74,125,191,0.16);max-width:740px;";
    private const string BannerStyle = "margin:0 0 16px;padding:8px 14px;border:1px solid #c9a227;background:rgba(255,200,0,0.18);max-width:740px;";

    /// <param name="grades">Lista ocen z bieżącego semestru na końcu maila (przed stopką); <c>null</c> (zestawienie poranne, wakacje letnie) - bez listy.</param>
    public static OutgoingMail Build(DailyDigest digest, IReadOnlyList<string> to, Clock clock, SchoolCalendar calendar, DateTime? dataAsOfLocal = null,
        GradeTable? grades = null)
    {
        var dates = new DateAnnotator(clock, calendar);
        var subject = BuildSubject(digest);
        return new OutgoingMail(to, subject, BuildText(digest, dates, dataAsOfLocal, grades), BuildHtml(subject, digest, dates, dataAsOfLocal, grades));
    }

    /// <summary>
    /// Temat. Poranne („Odprawa poranna”) mówi tylko o dniu dzisiejszym; popołudniowe dodaje liczbę kolejnych wydarzeń.
    /// </summary>
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
            if (digest.Kind == DigestKind.Morning)
            {
                text = $"{MorningTitle}, {DigestBuilder.DayName(date)} {iso}: {Summary(primary)}";
            }
            else
            {
                var when = primary.Title == "Jutro" ? $"jutro, {DigestBuilder.DayName(date)} {iso}" : $"{DigestBuilder.DayNameAccusative(date)} {iso}";
                text = $"Zdarzenia na {when}: {Summary(primary)}; kolejne: {upcoming}";
            }
        }
        return MailTemplates.CleanSubject(MailTemplates.SubjectPrefix + text);
    }

    /// <summary>Nazwa porannego zestawienia (temat i nagłówek maila).</summary>
    public const string MorningTitle = "Odprawa poranna";

    private const string AfternoonTitle = "Nadchodzące wydarzenia";

    private static string Title(DailyDigest digest) => digest.Kind == DigestKind.Morning ? MorningTitle : AfternoonTitle;

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
        text.AppendLine(Title(digest)).AppendLine();
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
        text.AppendLine("-- ").AppendLine(MailTemplates.FooterText);
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
            .AppendLine($"<h1 style=\"margin:0 0 12px;font-size:18px;font-weight:600;\">{Encode(Title(digest))}</h1>");

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
                AppendEventCards(html, day.Events, dates);
            }
        }

        AppendLegend(html, digest);
        if (grades is not null) AppendGradesHtml(html, grades);
        html.AppendLine($"<p style=\"margin:16px 0 0;font-size:12px;{MutedStyle}\">" +
            (dataAsOf is { } asOf ? $"Stan danych z Librusa: {asOf:yyyy-MM-dd HH:mm}. " : "") +
            Encode(MailTemplates.FooterText) + "</p>")
            .AppendLine("</body>")
            .AppendLine("</html>");
        return html.ToString();
    }

    /// <summary>
    /// Każde wydarzenie to osobna tabela z jedną komórką (karta): pasek w kolorze rodzaju z lewej, w pierwszej linii plakietka z rodzajem,
    /// numer lekcji i godziny, niżej przedmiot, opis i nauczyciel. Brak kolumn, więc na wąskim ekranie telefonu szczegóły mają całą szerokość.
    /// </summary>
    private static void AppendEventCards(StringBuilder html, IReadOnlyList<DigestEvent> events, DateAnnotator dates)
    {
        foreach (var e in events)
        {
            var body = DetailLines(e, dates);
            var head = $"<span style=\"display:inline-block;padding:1px 8px;font-size:12px;{DigestPalette.BadgeColors(e.Category)}\">{Encode(e.Label)}</span>";
            var when = WhenText(e);
            if (when.Length > 0) head += $" <span style=\"font-size:12px;\">&nbsp;{when}</span>";

            html.AppendLine($"<table role=\"presentation\" style=\"{TableStyle}\">")
                .AppendLine("<tr>")
                .AppendLine($"<td style=\"{CardCellStyle}border-left:6px solid {DigestPalette.Background(e.Category)};\">")
                .AppendLine(body.Count > 0 ? $"<div style=\"margin:0 0 4px;\">{head}</div>" : $"<div>{head}</div>");
            if (body.Count > 0) html.AppendLine(string.Join("<br>\n", body));
            html.AppendLine("</td>").AppendLine("</tr>").AppendLine("</table>");
        }
    }

    /// <summary>Plakietka w legendzie nie zawija się.</summary>
    private static string BadgeStyle(EventCategory category) => DigestPalette.BadgeColors(category) + "white-space:nowrap;";

    /// <summary>„<b>Lekcja 2</b> · 08:55 - 09:40”; gdy nie ma numeru lekcji, same godziny; gdy nie ma niczego, pusty napis.</summary>
    private static string WhenText(DigestEvent e)
    {
        var parts = new List<string>();
        if (e.Lesson is { } number) parts.Add($"<strong>Lekcja {number}</strong>");
        if (e.Hours.Length > 0) parts.Add($"<span style=\"{MutedStyle}\">{Encode(e.Hours)}</span>");
        return string.Join($" <span style=\"{MutedStyle}\">·</span> ", parts);
    }

    private static List<string> DetailLines(DigestEvent e, DateAnnotator dates)
    {
        var lines = new List<string>();
        if (e.Subject.Length > 0) lines.Add($"<strong>{Encode(e.Subject)}</strong>");
        if (e.Detail.Length > 0) lines.Add(dates.Html(e.Detail));
        if (e.Note.Length > 0) lines.Add($"<span style=\"font-size:12px;{MutedStyle}\">{Encode(e.Note)}</span>");
        return lines;
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

    // ---------- lista ocen ----------

    private const string GradesHeading = "Oceny z bieżącego semestru";
    private const string NoGradesText = "Brak ocen w bieżącym semestrze.";
    // Lista, nie tabela: nic nie ma stałej szerokości, więc nigdy nie jest szersza niż ekran telefonu (oceny zawijają się do kolejnych linii).
    private const string GradeListStyle = "margin:0 0 8px;border-bottom:1px solid rgba(128,128,128,0.4);max-width:760px;";
    private const string GradeRowStyle = "border-top:1px solid rgba(128,128,128,0.4);padding:6px 0 3px;";
    private const string GradeSubjectStyle = "margin-right:6px;";
    // Pole z oceną jest niepodzielne (white-space:nowrap), a zawijają się całe pola; kolor dopisywany osobno (GradePalette.CellStyle).
    private const string GradeChipStyle = "display:inline-block;min-width:18px;padding:2px 4px;margin:0 0 3px;border:1px solid #999999;text-align:center;white-space:nowrap;";

    private static string GradesSubtitle(GradeTable grades) =>
        $"Semestr {grades.Semester}, rok szkolny {grades.SchoolYearName}; oceny od najstarszej do najnowszej.";

    /// <summary>
    /// Lista na końcu maila: wiersz na przedmiot (alfabetycznie), w nim nazwa pogrubiona i zaraz za nią kolejne oceny z semestru
    /// (chronologicznie), każda w osobnym polu z kolorem wg skali (<see cref="GradePalette"/>, tak samo jak w mailu o nowej ocenie).
    /// Nazwa i oceny płyną w jednej linii (przedmiot z kilkoma ocenami zajmuje jeden wiersz), a dłuższe zawijają się pod nazwę, więc na
    /// wąskim ekranie telefonu nic nie wychodzi poza szerokość (wcześniej tabela z kolumnami na oceny miała po kilkaset pikseli).
    /// Wiersze rozdziela cienka linia; role „list” i „listitem” mówią czytnikom ekranu, że to lista przedmiotów.
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

        html.AppendLine($"<div role=\"list\" style=\"{GradeListStyle}\">");
        foreach (var row in grades.Rows)
        {
            var marks = string.Join(" ", row.Marks.Select(mark => $"<span style=\"{GradeChipStyle}{GradePalette.CellStyle(mark)}\">{Encode(mark)}</span>"));
            html.AppendLine($"<div role=\"listitem\" style=\"{GradeRowStyle}\"><strong style=\"{GradeSubjectStyle}\">{Encode(row.Subject)}</strong> {marks}</div>");
        }
        html.AppendLine("</div>");
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
