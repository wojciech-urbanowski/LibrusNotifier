using System.Text;
using LibrusNotifier.Calendar;
using LibrusNotifier.Data;
using LibrusNotifier.Librus;
using LibrusNotifier.Processing;
using LibrusNotifier.Timing;

namespace LibrusNotifier.Mail;

/// <summary>
/// Treść maili o nowych elementach: osobny mail na element, temat opisuje element. Ten plik to temat i wersja tekstowa;
/// układ HTML jest w <c>MailTemplates.Html.cs</c>.
/// </summary>
public static partial class MailTemplates
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
        var lead = LeadFields(element, kind, related).ToList();

        var subject = SubjectPrefix + Join(kind switch
        {
            NotificationKind.Moved => MovedParts(element, related!, F),
            NotificationKind.Removed => RemovedParts(element, related, F),
            _ => NewParts(element, F)
        });
        var report = element.Type == ElementTypes.Grade && kind == NotificationKind.New && subjectGrades is not null
            ? GradeReport.Build(element, subjectGrades)
            : null;
        return Build(element, to, dates, clock, lead, elementFields, details, subject, kind, detectedUtc, report);
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

    /// <param name="lead">Pola na początku maila o zmianie terminu lub usunięciu (<see cref="LeadFields"/>).</param>
    /// <param name="elementFields">Pola samego elementu.</param>
    private static OutgoingMail Build(ElementEntity element, IReadOnlyList<string> to, DateAnnotator dates, Clock clock, IReadOnlyList<Field> lead,
        IReadOnlyList<Field> elementFields, IReadOnlyList<Field> details, string subject, NotificationKind kind, DateTime? detectedUtc, GradeReport? report)
    {
        var fields = lead.Concat(elementFields).ToList();
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
            BuildHtml(subjectLine, typeName, lead, elementFields, extra, note, visibility, detected, dates, MarkStyleFor(element.Type), report, element.Type));
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
