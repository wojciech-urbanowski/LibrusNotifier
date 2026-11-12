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

    /// <summary>
    /// Data nadania wiadomości jest czysto techniczna: dostaje tylko dzień tygodnia, bez „dziś/jutro” i bez pogrubienia
    /// (ważne jest, o czym wiadomość, a nie kiedy ją wysłano).
    /// </summary>
    private static DateAnnotator.DateMode DateModeFor(string type, string label) =>
        type == ElementTypes.Message && label == "Data" ? DateAnnotator.DateMode.WeekdayOnly : DateAnnotator.DateMode.Relative;

    /// <summary>Tekst stopki każdego automatycznego maila, z numerem wersji aplikacji.</summary>
    public static string FooterText => $"Wiadomość wysłana automatycznie przez LibrusNotifier {App.Version}.";

    /// <param name="calendar">Kalendarz szkolny do pogrubiania dat po dniach wolnych; bez niego liczą się tylko weekendy i święta.</param>
    /// <param name="kind">Rodzaj powiadomienia: nowy element, zmiana terminu (element zastąpił zniknięty) albo usunięcie.</param>
    /// <param name="related">Zmiana terminu: poprzedni element. Usunięcie: element, na który wpis prawdopodobnie przeniesiono (albo <c>null</c>).</param>
    /// <param name="detectedUtc">Kiedy wykryto zdarzenie; domyślnie pierwsze zobaczenie elementu.</param>
    /// <param name="subjectGrades">Nowa ocena: oceny z tego samego przedmiotu do raportu pod treścią (<see cref="GradeHistory.ForSubject"/>);
    /// bez nich mail nie ma raportu.</param>
    public static OutgoingMail ForElement(ElementEntity element, IReadOnlyList<string> to, Clock clock, SchoolCalendar? calendar = null,
        NotificationKind kind = NotificationKind.New, ElementEntity? related = null, DateTime? detectedUtc = null,
        IReadOnlyList<ElementEntity>? subjectGrades = null)
    {
        var elementFields = FieldJson.Deserialize(element.DataJson);
        var details = FieldJson.Deserialize(element.DetailsJson);
        var dates = new DateAnnotator(clock, calendar);
        string F(string label) => elementFields.FirstOrDefault(f => f.Label == label)?.Value ?? "";
        if (kind == NotificationKind.Moved && related is null) kind = NotificationKind.New;
        var fields = LeadFields(element, kind, related).Concat(elementFields).ToList();

        var subject = SubjectPrefix + Join(kind switch
        {
            NotificationKind.Moved => MovedParts(element, related!, F),
            NotificationKind.Removed => RemovedParts(element, related, F),
            _ => NewParts(element, F)
        });
        var report = element.Type == ElementTypes.Grade && kind == NotificationKind.New && subjectGrades is not null
            ? GradeReport.Build(element, subjectGrades)
            : null;
        return Build(element, to, dates, clock, fields, details, subject, kind, detectedUtc, report);
    }

    private static IEnumerable<string> NewParts(ElementEntity element, Func<string, string> F) =>
        element.Type switch
        {
            ElementTypes.Grade => ["Nowa ocena: ", F("Przedmiot"), " - ", F("Ocena")],
            ElementTypes.DescriptiveGrade => ["Nowa ocena opisowa: ", F("Przedmiot"), Optional(" - ", F("Ocena"))],
            ElementTypes.Attendance => ["Frekwencja: ", F("Rodzaj"), Optional(" - ", F("Przedmiot")), Optional(", ", F("Data")),
                Optional(", lekcja ", F("Lekcja"))],
            ElementTypes.Message => ["Nowa wiadomość od ", F("Nadawca"), ": ", F("Temat")],
            ElementTypes.Announcement => ["Nowe ogłoszenie: ", F("Tytuł")],
            ElementTypes.ScheduleEvent => ["Terminarz: ", F("Tytuł"), Optional(" - ", F("Przedmiot")), Optional(", ", F("Data"))],
            ElementTypes.Homework => ["Nowe zadanie domowe: ", F("Przedmiot"), Optional(" - ", F("Temat"))],
            ElementTypes.TimetableChange => ["Zmiana w planie lekcji: ", F("Data"), Optional(", lekcja ", F("Nr lekcji")), Optional(" - ", F("Zmiana"))],
            ElementTypes.CompletedLesson => ["Zrealizowana lekcja: ", F("Przedmiot"), Optional(", ", F("Data")), Optional(", lekcja ", F("Nr lekcji"))],
            _ => ["Nowy element: ", element.Type]
        };

    private static OutgoingMail Build(ElementEntity element, IReadOnlyList<string> to, DateAnnotator dates, Clock clock, IReadOnlyList<Field> fields,
        IReadOnlyList<Field> details, string subject, NotificationKind kind, DateTime? detectedUtc, GradeReport? report)
    {
        var extra = details.Where(d => !fields.Any(f => f.Label == d.Label && f.Value == d.Value)).ToList();
        var note = extra.Count == 0 && element.Type is ElementTypes.Message && element.DetailsJson is null
            ? "(Treść niedostępna - sprawdź w Librusie)"
            : null;
        var visibility = Visibility(element);
        var detected = $"{clock.ToLocal(new DateTimeOffset(detectedUtc ?? element.FirstSeenUtc, TimeSpan.Zero)):yyyy-MM-dd HH:mm}";
        var typeName = TypeName(element.Type, kind);

        var body = new StringBuilder();
        body.AppendLine(typeName).AppendLine();
        foreach (var field in fields) AppendField(body, field, dates, element.Type);
        if (extra.Count > 0)
        {
            body.AppendLine();
            foreach (var field in extra) AppendField(body, field, dates, element.Type);
        }
        else if (note is not null)
        {
            body.AppendLine().AppendLine(note);
        }
        if (report is not null) AppendReportText(body, report);
        body.AppendLine()
            .AppendLine($"Widoczne na koncie: {visibility}")
            .AppendLine($"Wykryto: {detected}")
            .AppendLine()
            .AppendLine("-- ")
            .AppendLine(FooterText);

        // Opisy dat wchodzą do tematu przed ewentualnym skróceniem, które dotyka tekstu przed datą, a nie opisu.
        var subjectLine = CleanSubject(dates.PlainFitting(NormalizeSubject(subject), MaxSubjectLength));
        return new OutgoingMail(to, subjectLine, body.ToString(),
            BuildHtml(subjectLine, typeName, fields, extra, note, visibility, detected, dates, MarkStyleFor(element.Type), report, element.Type));
    }

    /// <summary>Pola na początku maila o zmianie terminu lub usunięciu (przed polami samego elementu).</summary>
    private static IEnumerable<Field> LeadFields(ElementEntity element, NotificationKind kind, ElementEntity? related)
    {
        switch (kind)
        {
            case NotificationKind.Moved:
                yield return new Field("Zmiana terminu", $"z {When(related!, withLesson: true)} na {When(element, withLesson: true)}");
                break;
            case NotificationKind.Removed:
                yield return new Field("Status", "usunięty z Librusa (zniknął z listy)");
                if (related is not null) yield return new Field("Możliwe przeniesienie", $"na {When(related, withLesson: true)}");
                break;
        }
    }

    /// <summary>Data zdarzenia elementu, opcjonalnie z numerem lekcji, np. <c>2026-11-20, lekcja 5</c>.</summary>
    private static string When(ElementEntity element, bool withLesson = false)
    {
        var fields = TrackedElements.Fields(element);
        var date = fields.GetValueOrDefault(TrackedElements.DateLabel(element.Type), "?");
        var lesson = fields.GetValueOrDefault("Nr lekcji", "");
        return !withLesson || lesson.Length == 0 ? date : $"{date}, lekcja {lesson}";
    }

    private static IEnumerable<string> MovedParts(ElementEntity element, ElementEntity previous, Func<string, string> F) => element.Type switch
    {
        ElementTypes.Homework => ["Zmiana terminu zadania domowego: ", F("Przedmiot"), Optional(" - ", F("Temat")), ", z ", When(previous), " na ", When(element)],
        _ => ["Zmiana terminu: ", F("Tytuł"), Optional(" - ", F("Przedmiot")), ", z ", When(previous), " na ", When(element)]
    };

    private static IEnumerable<string> RemovedParts(ElementEntity element, ElementEntity? movedTo, Func<string, string> F)
    {
        var hint = movedTo is null ? "" : " - możliwe przeniesienie na " + When(movedTo);
        IEnumerable<string> main = element.Type switch
        {
            ElementTypes.ScheduleEvent => ["Usunięto z terminarza: ", F("Tytuł"), F("Przedmiot") == F("Tytuł") ? "" : Optional(" - ", F("Przedmiot")), ", ", F("Data")],
            ElementTypes.TimetableChange => ["Usunięto zmianę w planie lekcji: ", F("Data"), Optional(", lekcja ", F("Nr lekcji")), Optional(" - ", F("Zmiana"))],
            ElementTypes.Homework => ["Usunięto zadanie domowe: ", F("Przedmiot"), Optional(" - ", F("Temat")), ", termin ", F("Termin wykonania")],
            _ => ["Usunięto element: ", element.Type]
        };
        return main.Append(hint);
    }

    public static string TypeName(string type, NotificationKind kind) => kind switch
    {
        NotificationKind.Moved => type == ElementTypes.Homework ? "Zmiana terminu zadania domowego" : "Zmiana terminu w terminarzu",
        NotificationKind.Removed => type switch
        {
            ElementTypes.ScheduleEvent => "Usunięto z terminarza",
            ElementTypes.TimetableChange => "Usunięto zmianę w planie lekcji",
            ElementTypes.Homework => "Usunięto zadanie domowe",
            _ => "Usunięto element"
        },
        _ => TypeName(type)
    };

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
    /// jedyne znaczniki, jakie dodajemy w wartościach, to <c>&lt;strong&gt;</c> wokół dat „na teraz” (dziś, jutro, pierwszy dzień po wolnym);
    /// dat technicznych (<see cref="DateModeFor"/>) to nie dotyczy.
    /// </summary>
    private static string BuildHtml(string title, string typeName, IReadOnlyList<Field> fields, IReadOnlyList<Field> extra, string? note,
        string visibility, string detected, DateAnnotator dates, Func<string, string>? markStyle, GradeReport? report, string type)
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
        AppendFieldTable(html, fields, dates, type, markStyle);
        AppendFieldTable(html, extra, dates, type);
        if (note is not null) html.AppendLine($"<p><em>{Encode(note)}</em></p>");
        if (report is not null) AppendReportHtml(html, report);
        html.AppendLine($"<table style=\"{MetaTableStyle}\">")
            .AppendLine("<tbody>")
            .AppendLine($"<tr><th scope=\"row\" style=\"{MetaCellStyle}font-weight:normal;\">Widoczne na koncie</th><td style=\"{MetaCellStyle}\">{Encode(visibility)}</td></tr>")
            .AppendLine($"<tr><th scope=\"row\" style=\"{MetaCellStyle}font-weight:normal;\">Wykryto</th><td style=\"{MetaCellStyle}\">{Encode(detected)}</td></tr>")
            .AppendLine("</tbody>")
            .AppendLine("</table>")
            .AppendLine($"<p style=\"margin:12px 0 0;font-size:12px;color:#777777;\">{Encode(FooterText)}</p>")
            .AppendLine("</body>")
            .AppendLine("</html>");
        return html.ToString();
    }

    /// <summary>Styl pola „Ocena” w mailu o ocenie: kolor wg skali (ocena) albo szary (ocena opisowa); inne typy nie mają koloru.</summary>
    private static Func<string, string>? MarkStyleFor(string type) => type switch
    {
        ElementTypes.Grade => mark => GradePalette.CellStyle(mark),
        ElementTypes.DescriptiveGrade => mark => GradePalette.CellStyle(mark, descriptive: true),
        _ => null
    };

    private static void AppendFieldTable(StringBuilder html, IReadOnlyList<Field> fields, DateAnnotator dates, string type, Func<string, string>? markStyle = null)
    {
        if (fields.Count == 0) return;
        html.AppendLine($"<table style=\"{TableStyle}\">").AppendLine("<tbody>");
        foreach (var field in fields)
        {
            // Data techniczna (kiedy wpis dodano) jest szara i bez opisu, żeby nie mylić jej z datą, której dotyczy powiadomienie.
            var technical = TechnicalDateLabels.Contains(field.Label);
            var value = technical ? EncodeMultiline(field.Value) : dates.Html(field.Value, DateModeFor(type, field.Label));
            // Szara jest też waga: cyfra obok oceny łatwo się z nią myli. Etykieta i wartość są szare jak „Data dodania”.
            var muted = technical || field.Label == WeightLabel;
            var color = muted ? TechnicalColorStyle : markStyle is not null && field.Label == GradeLabel ? markStyle(field.Value) : "";
            html.AppendLine($"<tr><th scope=\"row\" style=\"{LabelCellStyle}{(muted ? color : "")}\">{Encode(field.Label)}</th><td style=\"{ValueCellStyle}{color}\">{value}</td></tr>");
        }
        html.AppendLine("</tbody>").AppendLine("</table>");
    }

    private const string GradeLabel = "Ocena";
    private const string WeightLabel = "Waga";
    private const string ReportHeadingStyle = "margin:20px 0 8px;font-size:15px;font-weight:600;";
    private const string ReportHeadCellStyle = "border:1px solid #999999;padding:4px 10px;text-align:left;vertical-align:top;font-size:12px;font-weight:600;background:rgba(128,128,128,0.15);";

    /// <summary>Raport ocen z przedmiotu pod treścią: najnowsza na górze, ocena, której dotyczy mail, jest pogrubiona i oznaczona jako nowa.</summary>
    private static void AppendReportHtml(StringBuilder html, GradeReport report)
    {
        html.AppendLine($"<h2 style=\"{ReportHeadingStyle}\">{Encode(ReportTitle(report))}</h2>")
            .AppendLine($"<table style=\"{TableStyle}\">")
            .AppendLine("<thead>")
            .AppendLine("<tr>" + string.Concat(new[] { "Data", "Ocena", "Kategoria", WeightLabel }
                .Select(h => $"<th scope=\"col\" style=\"{ReportHeadCellStyle}{(h == WeightLabel ? TechnicalColorStyle : "")}\">{h}</th>")) + "</tr>")
            .AppendLine("</thead>")
            .AppendLine("<tbody>");
        foreach (var row in report.Rows)
        {
            var weight = row.Counts ? row.Weight : (row.Weight.Length == 0 ? "" : row.Weight + " ") + "(poza średnią)";
            var bold = row.IsCurrent ? "font-weight:700;" : "";
            var date = Encode(row.Date) + (row.IsCurrent ? " (nowa)" : "");
            // Ocena ma ten sam kolor co w treści maila (oznaczenia spoza skali, np. „np”, są szare); puste pole jest tylko pogrubione,
            // gdy to ocena z powiadomienia.
            var markStyle = ValueCellStyle + (GradePalette.CellStyle(row.Mark) is { Length: > 0 } colored ? colored : bold);
            html.AppendLine($"<tr><td style=\"{ValueCellStyle}white-space:nowrap;{bold}\">{date}</td>" +
                            $"<td style=\"{markStyle}\">{Encode(row.Mark)}</td>" +
                            $"<td style=\"{ValueCellStyle}{bold}\">{Encode(row.Category)}</td>" +
                            $"<td style=\"{ValueCellStyle}{TechnicalColorStyle}\">{Encode(weight)}</td></tr>"); // waga szara i niepogrubiona
        }
        html.AppendLine("</tbody>").AppendLine("</table>");
    }

    private static void AppendReportText(StringBuilder body, GradeReport report)
    {
        body.AppendLine().AppendLine(ReportTitle(report) + ":");
        foreach (var row in report.Rows)
        {
            var details = string.Join(", ", new[]
            {
                row.Category,
                row.Weight.Length == 0 ? "" : $"waga {row.Weight}",
                row.Counts ? "" : "poza średnią"
            }.Where(p => p.Length > 0));
            body.AppendLine($"{row.Date}  {row.Mark,-3}  {details}{(row.IsCurrent ? " (nowa)" : "")}".TrimEnd());
        }
    }

    private static string ReportTitle(GradeReport report) =>
        $"Oceny z przedmiotu {report.Subject}{(report.Semester is { } semester ? $", semestr {semester}" : "")} (od najnowszej)";

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static string EncodeMultiline(string text) =>
        Encode(text.Replace("\r\n", "\n").Replace('\r', '\n')).Replace("\n", "<br>\n");

    private static void AppendField(StringBuilder body, Field field, DateAnnotator dates, string type)
    {
        var value = TechnicalDateLabels.Contains(field.Label) ? field.Value : dates.Plain(field.Value, DateModeFor(type, field.Label));
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
