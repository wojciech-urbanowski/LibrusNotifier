using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using LibrusNotifier.Data;
using LibrusNotifier.Digest;
using LibrusNotifier.Librus;

namespace LibrusNotifier.Mail;

// Układ HTML maila o elemencie. Maile są czytane głównie na telefonie, więc nic nie ma stałej szerokości ani kolumn:
// zmiana w planie lekcji, wpis w terminarzu i wiadomość są kartami (ramka z kolorowym paskiem, jak wydarzenia w zestawieniach dziennych),
// pozostałe typy to linie „etykieta: wartość”, które zawijają się na całą szerokość ekranu.
public static partial class MailTemplates
{
    // Style są wpisane w elementy (klienci poczty różnie traktują <style>). Bez kolorów tła i tekstu treści, żeby
    // czytało się to w jasnym i ciemnym motywie; ramki i przygaszony tekst są w średnim szarym (#777777 jak w stopce).
    private const string PageStyle = "margin:0;padding:12px;font-family:-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:14px;line-height:1.45;";
    private const string HeadingStyle = "margin:0 0 12px;font-size:18px;font-weight:600;";
    private const string MutedStyle = "color:#777777;";
    private const string RuleColor = "rgba(128,128,128,0.4)";
    private const string BlockWidthStyle = "max-width:640px;";

    // Długie słowa (np. adresy w treści wiadomości) łamią się, zamiast poszerzać mail ponad ekran.
    private const string WrapStyle = "overflow-wrap:anywhere;word-break:break-word;";

    private const string LinesStyle = "margin:0 0 4px;border-top:1px solid " + RuleColor + ";border-bottom:1px solid " + RuleColor + ";" + BlockWidthStyle;
    private const string LineStyle = "padding:5px 0 6px;" + WrapStyle;
    private const string LineRuleStyle = "border-top:1px solid " + RuleColor + ";";

    private const string CardTableStyle = "border-collapse:collapse;width:100%;" + BlockWidthStyle + "margin:0 0 10px;";
    private const string CardCellStyle = "border:1px solid #999999;padding:8px 10px;text-align:left;vertical-align:top;" + WrapStyle;
    private const string BadgeBaseStyle = "display:inline-block;padding:1px 8px;margin:0 6px 0 0;font-size:12px;";
    private const string DateLineStyle = "font-size:16px;line-height:1.3;";
    private const string BodyStyle = "font-size:15px;line-height:1.5;" + WrapStyle;
    private const string GradeChipStyle = "display:inline-block;min-width:18px;padding:1px 6px;border:1px solid #999999;text-align:center;white-space:nowrap;";

    // Kolory pasków kart: zmiana w planie ma kolor rodzaju zmiany (jak w zestawieniach), wpis w terminarzu jest szary, wiadomość niebieska.
    private static readonly string ScheduleBarColor = DigestPalette.Background(EventCategory.Other);
    private const string MessageBarColor = "#bcd9f5";

    private const string GradeLabel = "Ocena";
    private const string WeightLabel = "Waga";
    private const string SwapSuffix = " (zmiana)";

    // Librus podaje nadawcę jako „Nazwisko Imię (Nazwisko Imię) [Rola]”; powtórzone nazwisko w nawiasie nic nie wnosi.
    [GeneratedRegex(@"^(?<name>.+?)\s*\(\k<name>\)(?<rest>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedNamePattern();

    /// <summary>Karta z elementu: gotowy HTML i pola techniczne (np. „Data dodania”), które trafiają do stopki.</summary>
    private sealed record Card(string Html, IReadOnlyList<Field> Technical);

    /// <summary>
    /// Wersja HTML maila. Wszystkie wartości są kodowane, nic z Librusa nie trafia do HTML-a jako znacznik; jedyne znaczniki, jakie dodajemy
    /// w wartościach, to <c>&lt;strong&gt;</c> wokół dat „na teraz” (dziś, jutro, pierwszy dzień po wolnym; dat technicznych
    /// (<see cref="DateModeFor"/>) to nie dotyczy) oraz formatowanie kart. Treść wiadomości ma całą szerokość ekranu, w akapitach.
    /// </summary>
    private static string BuildHtml(string title, string typeName, IReadOnlyList<Field> lead, IReadOnlyList<Field> fields, IReadOnlyList<Field> extra,
        string? note, string visibility, string detected, DateAnnotator dates, Func<string, string>? markStyle, GradeReport? report, string type)
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
            .AppendLine($"<h1 style=\"{HeadingStyle}\">{Encode(typeName)}</h1>");

        var card = BuildCard(type, fields, dates);
        if (card is null)
        {
            AppendFieldLines(html, [.. lead, .. fields], dates, type, markStyle);
        }
        else
        {
            AppendFieldLines(html, lead, dates, type, markStyle);
            html.Append(card.Html);
        }
        AppendDetails(html, extra, dates, type);
        if (note is not null) html.AppendLine($"<p style=\"margin:12px 0 0;\"><em>{Encode(note)}</em></p>");
        if (report is not null) AppendReportHtml(html, report);
        AppendFooter(html, card?.Technical ?? [], visibility, detected);
        html.AppendLine("</body>").AppendLine("</html>");
        return html.ToString();
    }

    /// <summary>Styl pola „Ocena” w mailu o ocenie: kolor wg skali (ocena) albo szary (ocena opisowa); inne typy nie mają koloru.</summary>
    private static Func<string, string>? MarkStyleFor(string type) => type switch
    {
        ElementTypes.Grade => mark => GradePalette.CellStyle(mark),
        ElementTypes.DescriptiveGrade => mark => GradePalette.CellStyle(mark, descriptive: true),
        _ => null
    };

    // ---------- linie „etykieta: wartość” ----------

    /// <summary>
    /// Pola jako linie rozdzielone cienką kreską: szara etykieta i wartość w jednej linii, dłuższa wartość zawija się pod etykietę.
    /// Data techniczna (kiedy wpis dodano) jest szara i bez opisu, żeby nie mylić jej z datą, której dotyczy powiadomienie; szara jest też
    /// waga, bo cyfra obok oceny łatwo się z nią myli. Ocena ma pole w kolorze skali (<see cref="GradePalette"/>).
    /// </summary>
    private static void AppendFieldLines(StringBuilder html, IReadOnlyList<Field> fields, DateAnnotator dates, string type, Func<string, string>? markStyle)
    {
        if (fields.Count == 0) return;
        html.AppendLine($"<div style=\"{LinesStyle}\">");
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var technical = TechnicalDateLabels.Contains(field.Label);
            var value = technical ? EncodeMultiline(field.Value) : dates.Html(field.Value, DateModeFor(type, field.Label));
            var muted = technical || field.Label == WeightLabel;
            if (!muted && markStyle is not null && field.Label == GradeLabel)
                value = $"<span style=\"{GradeChipStyle}{markStyle(field.Value)}\">{value}</span>";
            html.AppendLine($"<div style=\"{(i == 0 ? "" : LineRuleStyle)}{LineStyle}{(muted ? MutedStyle : "")}\">" +
                            $"<span style=\"{MutedStyle}\">{Encode(field.Label)}:</span> {value}</div>");
        }
        html.AppendLine("</div>");
    }

    /// <summary>
    /// Szczegóły pod kartą lub liniami (np. treść wiadomości) na całą szerokość ekranu, w akapitach. Treść wiadomości nie ma etykiety,
    /// inne szczegóły mają małą szarą etykietę nad tekstem.
    /// </summary>
    private static void AppendDetails(StringBuilder html, IReadOnlyList<Field> extra, DateAnnotator dates, string type)
    {
        foreach (var field in extra)
        {
            html.AppendLine($"<div style=\"margin:12px 0 0;{BlockWidthStyle}\">");
            if (type != ElementTypes.Message) html.AppendLine($"<div style=\"font-size:12px;{MutedStyle}\">{Encode(field.Label)}</div>");
            html.AppendLine($"<div style=\"margin:2px 0 0;{BodyStyle}\">");
            html.Append(Paragraphs(field.Value, dates, DateModeFor(type, field.Label)));
            html.AppendLine("</div>").AppendLine("</div>");
        }
    }

    private static string Paragraphs(string text, DateAnnotator dates, DateAnnotator.DateMode mode)
    {
        var paragraphs = SplitParagraphs(text).ToList();
        var html = new StringBuilder();
        for (var i = 0; i < paragraphs.Count; i++)
            html.AppendLine($"<p style=\"margin:{(i == paragraphs.Count - 1 ? "0" : "0 0 10px")};\">{dates.Html(paragraphs[i], mode)}</p>");
        return html.ToString();
    }

    /// <summary>Akapity oddzielone pustą linią; pojedyncze nowe linie zostają (jako <c>&lt;br&gt;</c>).</summary>
    private static IEnumerable<string> SplitParagraphs(string text)
    {
        var current = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (line.Trim().Length > 0)
            {
                current.Add(line);
                continue;
            }
            if (current.Count == 0) continue;
            yield return string.Join('\n', current);
            current.Clear();
        }
        if (current.Count > 0) yield return string.Join('\n', current);
    }

    private static void AppendFooter(StringBuilder html, IReadOnlyList<Field> technical, string visibility, string detected)
    {
        html.AppendLine($"<div style=\"margin:14px 0 0;padding:8px 0 0;border-top:1px solid {RuleColor};font-size:12px;{MutedStyle}\">");
        foreach (var field in technical) html.AppendLine($"<div>{Encode(field.Label)}: {EncodeMultiline(field.Value)}</div>");
        html.AppendLine($"<div>Widoczne na koncie: {Encode(visibility)}</div>")
            .AppendLine($"<div>Wykryto: {Encode(detected)}</div>")
            .AppendLine($"<div>{Encode(FooterText)}</div>")
            .AppendLine("</div>");
    }

    // ---------- karty ----------

    private static Card? BuildCard(string type, IReadOnlyList<Field> fields, DateAnnotator dates) => type switch
    {
        ElementTypes.TimetableChange => TimetableCard(fields, dates),
        ElementTypes.ScheduleEvent => ScheduleCard(fields, dates),
        ElementTypes.Message => MessageCard(fields, dates),
        _ => null
    };

    /// <summary>
    /// Pola elementu z rozliczeniem, które karta już ułożyła sama; reszta (np. „Załącznik”, „Opis”) trafia na koniec karty jako linie
    /// „etykieta: wartość”, żeby nic nie zginęło. Pola techniczne (<see cref="TechnicalDateLabels"/>) idą do stopki.
    /// </summary>
    private sealed class CardFields(IReadOnlyList<Field> fields)
    {
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);

        public string Peek(string label) => fields.FirstOrDefault(f => f.Label == label)?.Value ?? "";

        public string Take(string label)
        {
            _used.Add(label);
            return Peek(label);
        }

        public IEnumerable<Field> Rest => fields.Where(f => !_used.Contains(f.Label) && !TechnicalDateLabels.Contains(f.Label));

        public IReadOnlyList<Field> Technical => fields.Where(f => TechnicalDateLabels.Contains(f.Label)).ToList();
    }

    private static string CardHtml(string barColor, string inner) =>
        $"<table role=\"presentation\" style=\"{CardTableStyle}\">\n<tr>\n<td style=\"{CardCellStyle}border-left:6px solid {barColor};\">\n{inner}</td>\n</tr>\n</table>\n";

    private static string DateLine(string date, DateAnnotator dates) =>
        date.Length == 0 ? "" : $"<div style=\"{DateLineStyle}\">{dates.Html(date)}</div>\n";

    private static string Badge(string text, EventCategory category) =>
        $"<span style=\"{BadgeBaseStyle}{DigestPalette.BadgeColors(category)}\">{Encode(text)}</span>";

    /// <summary>„<b>Lekcja 2</b> · 08:55 - 09:40”; gdy nie ma numeru lekcji, same godziny; gdy nie ma niczego, pusty tekst.</summary>
    private static string LessonHtml(string lesson, string hours)
    {
        var parts = new List<string>();
        if (lesson.Length > 0) parts.Add($"<strong>Lekcja {Encode(lesson)}</strong>");
        if (hours.Length > 0) parts.Add($"<span style=\"{MutedStyle}\">{Encode(hours)}</span>");
        return parts.Count == 0 ? "" : $"<span style=\"font-size:12px;\">{string.Join($" <span style=\"{MutedStyle}\">·</span> ", parts)}</span>";
    }

    /// <summary>Linia „etykieta: wartość” w karcie (pola, których karta nie układa sama).</summary>
    private static string DetailLine(Field field, DateAnnotator dates, string type) =>
        $"<div style=\"margin:2px 0 0;\"><span style=\"{MutedStyle}\">{Encode(field.Label)}:</span> {dates.Html(field.Value, DateModeFor(type, field.Label))}</div>\n";

    private static string RestLines(CardFields fields, DateAnnotator dates, string type) =>
        string.Concat(fields.Rest.Select(f => DetailLine(f, dates, type)));

    /// <summary>
    /// Zmiana w planie lekcji: data, plakietka z rodzajem zmiany (kolor jak w zestawieniach) i lekcja z godzinami, przedmiot,
    /// nauczyciel i sala, a pod kreską „Co się zmieniło”: każde pole „X (zmiana)” jako „stare -> nowe” ze skreślonym starym
    /// (pole bez zmiany, np. ten sam przedmiot, pomijane).
    /// </summary>
    private static Card TimetableCard(IReadOnlyList<Field> fields, DateAnnotator dates)
    {
        const string type = ElementTypes.TimetableChange;
        var f = new CardFields(fields);
        var change = f.Take("Zmiana");
        var date = f.Take("Data");
        var lesson = f.Take("Nr lekcji");
        var hours = f.Take("Godziny");
        var subject = f.Take("Przedmiot");
        var teacherRoom = f.Take("Nauczyciel i sala");
        var swaps = fields.Where(x => x.Label.EndsWith(SwapSuffix, StringComparison.Ordinal)).ToList();
        foreach (var swap in swaps) f.Take(swap.Label);
        var category = DigestEventBuilder.TimetableCategory(change);

        var inner = new StringBuilder(DateLine(date, dates));
        var badge = change.Length == 0 ? "" : Badge(change, category);
        var when = LessonHtml(lesson, hours);
        if (badge.Length + when.Length > 0) inner.AppendLine($"<div style=\"margin:4px 0;\">{badge}{when}</div>");
        if (subject.Length > 0) inner.AppendLine($"<div style=\"font-size:15px;\"><strong>{dates.Html(subject)}</strong></div>");
        if (teacherRoom.Length > 0)
            inner.AppendLine($"<div style=\"font-size:12px;{MutedStyle}\">Nauczyciel i sala: {dates.Html(teacherRoom)}</div>");

        var changes = string.Concat(swaps.Select(s =>
            SwapBlock(s.Label[..^SwapSuffix.Length], s.Value, f.Peek(s.Label[..^SwapSuffix.Length]))));
        if (changes.Length > 0)
            inner.AppendLine($"<div style=\"margin:8px 0 0;padding:6px 0 0;border-top:1px solid {RuleColor};\">")
                .AppendLine("<div style=\"font-size:12px;font-weight:600;\">Co się zmieniło</div>")
                .Append(changes)
                .AppendLine("</div>");
        inner.Append(RestLines(f, dates, type));
        return new Card(CardHtml(DigestPalette.Background(category), inner.ToString()), f.Technical);
    }

    /// <summary>
    /// Zmiana jednego pola planu: etykieta i „stare -> nowe” ze skreślonym starym. Wartość bez strzałki to samo nowe pole; gdy jest równa
    /// bieżącej wartości pola (<paramref name="current"/>, np. przedmiot bez zmiany), nic się nie zmieniło i zwracany jest pusty tekst.
    /// </summary>
    private static string SwapBlock(string label, string value, string current)
    {
        string valueHtml;
        var arrow = value.IndexOf("->", StringComparison.Ordinal);
        if (arrow >= 0)
        {
            var was = value[..arrow].Trim();
            var now = value[(arrow + 2)..].Trim();
            valueHtml = (was.Length > 0 ? $"<s style=\"{MutedStyle}\">{Encode(was)}</s> -&gt; " : "") + $"<strong>{Encode(now.Length > 0 ? now : "[brak]")}</strong>";
        }
        else if (string.Equals(value, current, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }
        else
        {
            valueHtml = $"<strong>{Encode(value)}</strong>";
        }
        return $"<div style=\"margin:6px 0 0;\">\n<div style=\"font-size:12px;{MutedStyle}\">{Encode(label)}</div>\n<div>{valueHtml}</div>\n</div>\n";
    }

    /// <summary>
    /// Wpis w terminarzu: data, lekcja z godziną, tytuł wpisu. Przedmiot, który jest tym samym tekstem co tytuł (częste w Librusie),
    /// nie jest powtarzany; inny przedmiot i pozostałe pola (opis, nauczyciel) są liniami „etykieta: wartość” pod tytułem.
    /// </summary>
    private static Card ScheduleCard(IReadOnlyList<Field> fields, DateAnnotator dates)
    {
        const string type = ElementTypes.ScheduleEvent;
        var f = new CardFields(fields);
        var date = f.Take("Data");
        var lesson = f.Take("Nr lekcji");
        var hours = f.Take("Godzina");
        var title = f.Take("Tytuł");
        var main = title;
        if (title.Length == 0) main = f.Take("Przedmiot");
        else if (string.Equals(f.Peek("Przedmiot"), title, StringComparison.OrdinalIgnoreCase)) f.Take("Przedmiot");

        var inner = new StringBuilder(DateLine(date, dates));
        var when = LessonHtml(lesson, hours);
        if (when.Length > 0) inner.AppendLine($"<div style=\"margin:4px 0;\">{when}</div>");
        if (main.Length > 0) inner.AppendLine($"<div style=\"font-size:15px;\"><strong>{dates.Html(main)}</strong></div>");
        inner.Append(RestLines(f, dates, type));
        return new Card(CardHtml(ScheduleBarColor, inner.ToString()), f.Technical);
    }

    /// <summary>
    /// Wiadomość: w karcie temat, nadawca i data nadania (tylko z dniem tygodnia, patrz <see cref="DateModeFor"/>); pod kartą treść
    /// (<see cref="AppendDetails"/>). Pola spoza układu (np. „Załącznik”) są liniami na końcu karty.
    /// </summary>
    private static Card MessageCard(IReadOnlyList<Field> fields, DateAnnotator dates)
    {
        const string type = ElementTypes.Message;
        var f = new CardFields(fields);
        var topic = f.Take("Temat");
        var sender = f.Take("Nadawca");
        var date = f.Take("Data");

        var inner = new StringBuilder();
        if (topic.Length > 0) inner.AppendLine($"<div style=\"font-size:16px;font-weight:700;line-height:1.3;\">{dates.Html(topic)}</div>");
        if (sender.Length > 0) inner.AppendLine($"<div style=\"margin:3px 0 0;\">od <strong>{Encode(WithoutRepeatedName(sender))}</strong></div>");
        if (date.Length > 0)
            inner.AppendLine($"<div style=\"margin:2px 0 0;font-size:12px;{MutedStyle}\">{dates.Html(date, DateModeFor(type, "Data"))}</div>");
        inner.Append(RestLines(f, dates, type));
        return new Card(CardHtml(MessageBarColor, inner.ToString()), f.Technical);
    }

    /// <summary>„Selwesiuk Jolanta (Selwesiuk Jolanta) [Nauczyciel]” → „Selwesiuk Jolanta [Nauczyciel]”; inny tekst bez zmian.</summary>
    internal static string WithoutRepeatedName(string sender)
    {
        var match = RepeatedNamePattern().Match(sender);
        return match.Success ? (match.Groups["name"].Value + match.Groups["rest"].Value).Trim() : sender;
    }

    // ---------- raport ocen ----------

    private const string ReportHeadingStyle = "margin:20px 0 8px;font-size:15px;font-weight:600;";
    private const string ReportTableStyle = "border-collapse:collapse;width:100%;max-width:640px;margin:0 0 12px;";
    private const string ReportCellStyle = "border:1px solid #999999;padding:6px 10px;text-align:left;vertical-align:top;word-break:break-word;";
    private const string ReportHeadCellStyle = "border:1px solid #999999;padding:4px 10px;text-align:left;vertical-align:top;font-size:12px;font-weight:600;background:rgba(128,128,128,0.15);";

    /// <summary>Raport ocen z przedmiotu pod treścią: najnowsza na górze, ocena, której dotyczy mail, jest pogrubiona i oznaczona jako nowa.</summary>
    private static void AppendReportHtml(StringBuilder html, GradeReport report)
    {
        html.AppendLine($"<h2 style=\"{ReportHeadingStyle}\">{Encode(ReportTitle(report))}</h2>")
            .AppendLine($"<table style=\"{ReportTableStyle}\">")
            .AppendLine("<thead>")
            .AppendLine("<tr>" + string.Concat(new[] { "Data", "Ocena", "Kategoria", WeightLabel }
                .Select(h => $"<th scope=\"col\" style=\"{ReportHeadCellStyle}{(h == WeightLabel ? MutedStyle : "")}\">{h}</th>")) + "</tr>")
            .AppendLine("</thead>")
            .AppendLine("<tbody>");
        foreach (var row in report.Rows)
        {
            var weight = row.Counts ? row.Weight : (row.Weight.Length == 0 ? "" : row.Weight + " ") + "(poza średnią)";
            var bold = row.IsCurrent ? "font-weight:700;" : "";
            var date = Encode(row.Date) + (row.IsCurrent ? " (nowa)" : "");
            // Ocena ma ten sam kolor co w treści maila (oznaczenia spoza skali, np. „np”, są szare); puste pole jest tylko pogrubione,
            // gdy to ocena z powiadomienia.
            var markStyle = ReportCellStyle + (GradePalette.CellStyle(row.Mark) is { Length: > 0 } colored ? colored : bold);
            html.AppendLine($"<tr><td style=\"{ReportCellStyle}white-space:nowrap;{bold}\">{date}</td>" +
                            $"<td style=\"{markStyle}\">{Encode(row.Mark)}</td>" +
                            $"<td style=\"{ReportCellStyle}{bold}\">{Encode(row.Category)}</td>" +
                            $"<td style=\"{ReportCellStyle}{MutedStyle}\">{Encode(weight)}</td></tr>"); // waga szara i niepogrubiona
        }
        html.AppendLine("</tbody>").AppendLine("</table>");
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static string EncodeMultiline(string text) =>
        Encode(text.Replace("\r\n", "\n").Replace('\r', '\n')).Replace("\n", "<br>\n");
}
