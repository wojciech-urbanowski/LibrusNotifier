using System.Net;
using System.Text;
using LibrusNotifier.Calendar;
using LibrusNotifier.Data;
using LibrusNotifier.Librus;
using LibrusNotifier.Processing;
using LibrusNotifier.Timing;

namespace LibrusNotifier.Mail;

/// <summary>Treść maili o nowych elementach: osobny mail na element, temat opisuje element.</summary>
public static class MailTemplates
{
    public const string SubjectPrefix = "[Librus] ";
    private const int MaxSubjectLength = 160;

    /// <summary>
    /// Pola z datą technicznie opisującą wpis (kiedy został dodany) nie dostają opisu względem dzisiaj;
    /// „Wykryto” jest osobną linią i też go nie dostaje.
    /// </summary>
    private static readonly HashSet<string> TechnicalDateLabels = new(StringComparer.OrdinalIgnoreCase) { "Data dodania" };

    /// <param name="calendar">Kalendarz szkolny do pogrubiania dat po dniach wolnych; bez niego liczą się tylko weekendy i święta.</param>
    public static OutgoingMail ForElement(ElementEntity element, IReadOnlyList<string> to, Clock clock, SchoolCalendar? calendar = null)
    {
        var fields = FieldJson.Deserialize(element.DataJson);
        var details = FieldJson.Deserialize(element.DetailsJson);
        var dates = new DateAnnotator(clock, calendar);
        string F(string label) => fields.FirstOrDefault(f => f.Label == label)?.Value ?? "";

        var subject = SubjectPrefix + Join(element.Type switch
        {
            ElementTypes.Grade => ["Nowa ocena: ", F("Przedmiot"), " – ", F("Ocena")],
            ElementTypes.DescriptiveGrade => ["Nowa ocena opisowa: ", F("Przedmiot"), Optional(" – ", F("Ocena"))],
            ElementTypes.Attendance => ["Frekwencja: ", F("Rodzaj"), Optional(" – ", F("Przedmiot")), Optional(", ", F("Data")),
                Optional(", lekcja ", F("Lekcja"))],
            ElementTypes.Message => ["Nowa wiadomość od ", F("Nadawca"), ": ", F("Temat")],
            ElementTypes.Announcement => ["Nowe ogłoszenie: ", F("Tytuł")],
            ElementTypes.ScheduleEvent => ["Terminarz: ", F("Tytuł"), Optional(" – ", F("Przedmiot")), Optional(", ", F("Data"))],
            ElementTypes.Homework => ["Nowe zadanie domowe: ", F("Przedmiot"), Optional(" – ", F("Temat"))],
            ElementTypes.TimetableChange => ["Zmiana w planie lekcji: ", F("Data"), Optional(", lekcja ", F("Nr lekcji")), Optional(" – ", F("Zmiana"))],
            ElementTypes.CompletedLesson => ["Zrealizowana lekcja: ", F("Przedmiot"), Optional(", ", F("Data")), Optional(", lekcja ", F("Nr lekcji"))],
            _ => ["Nowy element: ", element.Type]
        });

        var extra = details.Where(d => !fields.Any(f => f.Label == d.Label && f.Value == d.Value)).ToList();
        var note = extra.Count == 0 && element.Type is ElementTypes.Message && element.DetailsJson is null
            ? "(Treść niedostępna – sprawdź w Librusie.)"
            : null;
        var visibility = Visibility(element);
        var detected = $"{clock.ToLocal(new DateTimeOffset(element.FirstSeenUtc, TimeSpan.Zero)):yyyy-MM-dd HH:mm}";
        var typeName = TypeName(element.Type);

        var body = new StringBuilder();
        body.AppendLine(typeName).AppendLine();
        foreach (var field in fields) AppendField(body, field, dates);
        if (extra.Count > 0)
        {
            body.AppendLine();
            foreach (var field in extra) AppendField(body, field, dates);
        }
        else if (note is not null)
        {
            body.AppendLine().AppendLine(note);
        }
        body.AppendLine()
            .AppendLine($"Widoczne na koncie: {visibility}")
            .AppendLine($"Wykryto: {detected}")
            .AppendLine()
            .AppendLine("-- ")
            .AppendLine("Wiadomość wysłana automatycznie przez LibrusNotifier.");

        // Opisy dat wchodzą do tematu przed ewentualnym skróceniem, które dotyka tekstu przed datą, a nie opisu.
        var subjectLine = CleanSubject(dates.PlainFitting(NormalizeSubject(subject), MaxSubjectLength));
        return new OutgoingMail(to, subjectLine, body.ToString(), BuildHtml(subjectLine, typeName, fields, extra, note, visibility, detected, dates));
    }

    public static string TypeName(string type) => type switch
    {
        ElementTypes.Grade => "Nowa ocena",
        ElementTypes.DescriptiveGrade => "Nowa ocena opisowa",
        ElementTypes.Attendance => "Nowy wpis frekwencji",
        ElementTypes.Message => "Nowa wiadomość",
        ElementTypes.Announcement => "Nowe ogłoszenie",
        ElementTypes.ScheduleEvent => "Nowy wpis w terminarzu",
        ElementTypes.Homework => "Nowe zadanie domowe",
        ElementTypes.TimetableChange => "Zmiana w planie lekcji",
        ElementTypes.CompletedLesson => "Zrealizowana lekcja",
        _ => "Nowy element"
    };

    public static string CleanSubject(string subject)
    {
        var clean = NormalizeSubject(subject);
        return clean.Length <= MaxSubjectLength ? clean : clean[..(MaxSubjectLength - 1)] + "…";
    }

    private static string NormalizeSubject(string subject) => ElementKeys.Normalize(subject.Replace('\r', ' ').Replace('\n', ' '));

    // Style są wpisane w elementy (klienci poczty różnie traktują <style>). Bez kolorów tła i tekstu treści, żeby
    // czytało się to w jasnym i ciemnym motywie; tło etykiet jest półprzezroczyste, ramki w średnim szarym.
    private const string PageStyle = "margin:0;padding:16px;font-family:-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:14px;line-height:1.45;";
    private const string HeadingStyle = "margin:0 0 12px;font-size:18px;font-weight:600;";
    private const string TableStyle = "border-collapse:collapse;width:100%;max-width:640px;margin:0 0 12px;";
    private const string LabelCellStyle = "border:1px solid #999999;padding:6px 10px;width:160px;text-align:left;vertical-align:top;font-weight:600;background:rgba(128,128,128,0.15);";
    private const string ValueCellStyle = "border:1px solid #999999;padding:6px 10px;text-align:left;vertical-align:top;word-break:break-word;";
    // Ten sam średni szary co w stopce (#777777): czytelny w jasnym i ciemnym motywie.
    private const string TechnicalColorStyle = "color:#777777;";
    private const string MetaTableStyle = "border-collapse:collapse;margin:12px 0 0;font-size:12px;color:#777777;";
    private const string MetaCellStyle = "padding:2px 12px 2px 0;text-align:left;vertical-align:top;";

    /// <summary>
    /// Wersja HTML maila: pola jako prawdziwe tabele (etykieta w <c>&lt;th scope="row"&gt;</c>, wartość w <c>&lt;td&gt;</c>),
    /// szczegóły (np. treść wiadomości) w osobnej tabeli. Wszystkie wartości są kodowane, nic z Librusa nie trafia do HTML-a jako znacznik;
    /// jedyne znaczniki, jakie dodajemy w wartościach, to <c>&lt;strong&gt;</c> wokół dat „na teraz” (dziś, jutro, pierwszy dzień po wolnym).
    /// </summary>
    private static string BuildHtml(string title, string typeName, IReadOnlyList<Field> fields, IReadOnlyList<Field> extra, string? note,
        string visibility, string detected, DateAnnotator dates)
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
        AppendFieldTable(html, fields, dates);
        AppendFieldTable(html, extra, dates);
        if (note is not null) html.AppendLine($"<p><em>{Encode(note)}</em></p>");
        html.AppendLine($"<table style=\"{MetaTableStyle}\">")
            .AppendLine("<tbody>")
            .AppendLine($"<tr><th scope=\"row\" style=\"{MetaCellStyle}font-weight:normal;\">Widoczne na koncie</th><td style=\"{MetaCellStyle}\">{Encode(visibility)}</td></tr>")
            .AppendLine($"<tr><th scope=\"row\" style=\"{MetaCellStyle}font-weight:normal;\">Wykryto</th><td style=\"{MetaCellStyle}\">{Encode(detected)}</td></tr>")
            .AppendLine("</tbody>")
            .AppendLine("</table>")
            .AppendLine("<p style=\"margin:12px 0 0;font-size:12px;color:#777777;\">Wiadomość wysłana automatycznie przez LibrusNotifier.</p>")
            .AppendLine("</body>")
            .AppendLine("</html>");
        return html.ToString();
    }

    private static void AppendFieldTable(StringBuilder html, IReadOnlyList<Field> fields, DateAnnotator dates)
    {
        if (fields.Count == 0) return;
        html.AppendLine($"<table style=\"{TableStyle}\">").AppendLine("<tbody>");
        foreach (var field in fields)
        {
            // Data techniczna (kiedy wpis dodano) jest szara i bez opisu, żeby nie mylić jej z datą, której dotyczy powiadomienie.
            var technical = TechnicalDateLabels.Contains(field.Label);
            var value = technical ? EncodeMultiline(field.Value) : dates.Html(field.Value);
            var color = technical ? TechnicalColorStyle : "";
            html.AppendLine($"<tr><th scope=\"row\" style=\"{LabelCellStyle}{color}\">{Encode(field.Label)}</th><td style=\"{ValueCellStyle}{color}\">{value}</td></tr>");
        }
        html.AppendLine("</tbody>").AppendLine("</table>");
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static string EncodeMultiline(string text) =>
        Encode(text.Replace("\r\n", "\n").Replace('\r', '\n')).Replace("\n", "<br>\n");

    private static void AppendField(StringBuilder body, Field field, DateAnnotator dates)
    {
        var value = TechnicalDateLabels.Contains(field.Label) ? field.Value : dates.Plain(field.Value);
        if (value.Contains('\n'))
            body.AppendLine($"{field.Label}:").AppendLine(value.Replace("\r", "")).AppendLine();
        else
            body.AppendLine($"{field.Label}: {value}");
    }

    private static string Visibility(ElementEntity element) => (element.VisibleToParent, element.VisibleToStudent) switch
    {
        (true, true) => "rodzica i ucznia",
        (true, false) => "rodzica",
        (false, true) => "ucznia",
        _ => "-"
    };

    private static string Optional(string prefix, string value) => value.Length == 0 ? "" : prefix + value;

    private static string Join(params IEnumerable<string> parts) => string.Concat(parts);
}
