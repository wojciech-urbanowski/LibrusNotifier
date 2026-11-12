using System.Globalization;
using LibrusApixNet;

namespace LibrusNotifier.Librus;

/// <summary>
/// Pobieranie list modułów i zamiana na <see cref="ObservedItem"/>. Zakres list jest ograniczony, by wykonywać jak najmniej żądań:
/// wiadomości – pierwsza strona; terminarz – bieżący i następny miesiąc; oceny – oba semestry (jedno żądanie);
/// zadania domowe i zrealizowane lekcje – ostatnie 7 dni; plan lekcji – bieżący tydzień.
/// Klucze elementów nie zawierają identyfikatorów Librusa (różnych dla kont) ani numeru semestru.
/// </summary>
public static class LibrusLists
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static async Task<IReadOnlyList<ObservedItem>> FetchAsync(Client client, ModuleKind module, DateOnly today, CancellationToken ct) =>
        module switch
        {
            ModuleKind.Grades => FromGrades(await Grades.GetGradesAsync(client, "all", ct)),
            ModuleKind.Attendance => FromAttendance(await Attendances.GetAttendanceAsync(client, "all", ct)),
            ModuleKind.Messages => FromMessages(await Messages.GetReceivedAsync(client, 0, ct)),
            ModuleKind.Announcements => FromAnnouncements(await Announcements.GetAnnouncementsAsync(client, ct)),
            ModuleKind.Schedule => await ScheduleAsync(client, today, ct),
            ModuleKind.Homework => FromHomework(await HomeworkService.GetHomeworkAsync(client, Date(today.AddDays(-7)), Date(today), ct)),
            ModuleKind.Timetable => FromTimetable(await Timetable.GetTimetableAsync(client, MondayOf(today), ct)),
            ModuleKind.CompletedLessons => FromCompletedLessons(await CompletedLessons.GetCompletedAsync(client, Date(today.AddDays(-7)), Date(today), 0, ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(module), module, null)
        };

    public static bool HasDetails(ObservedItem item) =>
        item.Type is ElementTypes.Message or ElementTypes.Homework && !string.IsNullOrEmpty(item.DetailRef);

    public static async Task<IReadOnlyList<Field>?> FetchDetailsAsync(Client client, ObservedItem item, CancellationToken ct)
    {
        if (!HasDetails(item)) return null;
        switch (item.Type)
        {
            case ElementTypes.Message:
                var message = await Messages.MessageContentAsync(client, item.DetailRef!, ct);
                return Fields(("Treść", message.Content));
            case ElementTypes.Homework:
                var details = await HomeworkService.HomeworkDetailAsync(client, item.DetailRef!, ct);
                return details.Select(d => new Field(d.Key.Trim().TrimEnd(':').Trim(), d.Value.Trim()))
                    .Where(f => f.Label.Length > 0 && f.Value.Length > 0)
                    .ToList();
            default:
                return null;
        }
    }

    public static IReadOnlyList<ObservedItem> FromGrades(GradesResult result)
    {
        var items = new List<ObservedItem>();
        foreach (var grade in result.Semesters.SelectMany(s => s.Values).SelectMany(g => g))
        {
            string[] known = ["Ocena", "Przedmiot", "Kategoria", "Data", "Nauczyciel", "Licz do średniej", "Waga"];
            items.Add(new ObservedItem(ElementTypes.Grade,
                [grade.Title, grade.Mark, grade.Date, grade.Category, grade.Teacher, grade.Weight.ToString(Inv), grade.Counts ? "1" : "0"],
                Fields([
                    ("Przedmiot", grade.Title), ("Ocena", grade.Mark), ("Kategoria", grade.Category), ("Data", grade.Date),
                    ("Nauczyciel", grade.Teacher), ("Waga", grade.Weight.ToString(Inv)), ("Liczona do średniej", grade.Counts ? "tak" : "nie"),
                    ("Semestr", grade.Semester.ToString(Inv)), .. DescriptionFields(grade.Description, known)
                ])));
        }
        foreach (var grade in result.Descriptive.SelectMany(s => s.Values).SelectMany(g => g))
        {
            string[] known = ["Ocena", "Przedmiot", "Data", "Nauczyciel"];
            items.Add(new ObservedItem(ElementTypes.DescriptiveGrade,
                [grade.Title, grade.Mark, grade.Date, grade.Teacher, grade.Description],
                Fields([
                    ("Przedmiot", grade.Title), ("Ocena", grade.Mark), ("Data", grade.Date), ("Nauczyciel", grade.Teacher),
                    ("Semestr", grade.Semester.ToString(Inv)), .. DescriptionFields(grade.Description, known)
                ])));
        }
        return items;
    }

    /// <summary>Obecności („ob”) są pomijane: obserwowane są nieobecności, spóźnienia, zwolnienia itp.</summary>
    public static IReadOnlyList<ObservedItem> FromAttendance(IEnumerable<IEnumerable<Attendance>> semesters) =>
        semesters.SelectMany(s => s)
            .Where(a => !IsPresence(a))
            .Select(a => new ObservedItem(ElementTypes.Attendance,
                [a.Date, a.Period.ToString(Inv), a.Symbol, a.Type, a.Subject, a.Teacher],
                Fields(("Rodzaj", a.Type), ("Symbol", a.Symbol), ("Data", a.Date), ("Lekcja", a.Period.ToString(Inv)), ("Przedmiot", a.Subject),
                    ("Temat zajęć", a.Topic), ("Nauczyciel", a.Teacher), ("Wycieczka", a.Excursion ? "tak" : ""))))
            .ToList();

    public static IReadOnlyList<ObservedItem> FromMessages(IEnumerable<Message> messages) =>
        messages.Select(m => new ObservedItem(ElementTypes.Message,
                [m.Author, m.Title, m.Date],
                Fields(("Nadawca", m.Author), ("Temat", m.Title), ("Data", m.Date), ("Załącznik", m.HasAttachment ? "tak" : "")),
                m.Href))
            .ToList();

    public static IReadOnlyList<ObservedItem> FromAnnouncements(IEnumerable<Announcement> announcements) =>
        announcements.Select(a => new ObservedItem(ElementTypes.Announcement,
                [a.Title, a.Author, a.Date],
                Fields(("Tytuł", a.Title), ("Autor", a.Author), ("Data", a.Date), ("Treść", a.Description))))
            .ToList();

    public static IReadOnlyList<ObservedItem> FromSchedule(int year, int month, IReadOnlyDictionary<int, List<Event>> days)
    {
        var items = new List<ObservedItem>();
        foreach (var (day, events) in days.OrderBy(d => d.Key))
        {
            if (day < 1 || day > DateTime.DaysInMonth(year, month)) continue;
            var date = Date(new DateOnly(year, month, day));
            foreach (var e in events)
            {
                var description = e.Data.GetValueOrDefault("Opis", "");
                var teacher = e.Data.GetValueOrDefault("Nauczyciel", "");
                var number = e.Number?.ToString(Inv) ?? "";
                var hour = e.Hour == "unknown" ? "" : e.Hour;
                items.Add(new ObservedItem(ElementTypes.ScheduleEvent,
                    [date, e.Title, e.Subject, number, hour, description, teacher],
                    Fields([
                        ("Data", date), ("Godzina", hour), ("Nr lekcji", number), ("Przedmiot", e.Subject), ("Tytuł", e.Title),
                        .. e.Data.Select(d => (d.Key, (string?)(d.Value == "unknown" ? "" : d.Value)))
                    ])));
            }
        }
        return items;
    }

    public static IReadOnlyList<ObservedItem> FromHomework(IEnumerable<Homework> homework) =>
        homework.Select(h => new ObservedItem(ElementTypes.Homework,
                [h.Lesson, h.Teacher, h.Subject, h.Category, h.TaskDate, h.CompletionDate],
                Fields(("Przedmiot", h.Lesson), ("Temat", h.Subject), ("Kategoria", h.Category), ("Nauczyciel", h.Teacher),
                    ("Data udostępnienia", h.TaskDate), ("Termin wykonania", h.CompletionDate)),
                h.Href))
            .ToList();

    /// <summary>Z planu lekcji obserwowane są tylko zmiany (lekcje z adnotacją, np. zastępstwo lub odwołanie).</summary>
    public static IReadOnlyList<ObservedItem> FromTimetable(IEnumerable<IEnumerable<Period>> days)
    {
        var items = new List<ObservedItem>();
        foreach (var period in days.SelectMany(d => d))
        {
            if (period.Info.Count == 0) continue;
            var changes = period.Info.OrderBy(i => i.Key, StringComparer.Ordinal).ToList();
            var change = changes.Select(c => c.Value).FirstOrDefault(c => c is not null);
            var number = period.Number.ToString(Inv);
            items.Add(new ObservedItem(ElementTypes.TimetableChange,
                [
                    period.Date, number, period.Subject, period.TeacherAndClassroom,
                    .. changes.SelectMany(c => new[] { c.Key, c.Value?.SubjectSwap ?? "", c.Value?.TeacherSwap ?? "", c.Value?.ClassroomSwap ?? "" })
                ],
                Fields(("Zmiana", string.Join(", ", changes.Select(c => c.Key))), ("Data", period.Date), ("Nr lekcji", number),
                    ("Godziny", $"{period.DateFrom}–{period.DateTo}"), ("Przedmiot", period.Subject), ("Nauczyciel i sala", period.TeacherAndClassroom),
                    ("Przedmiot (zmiana)", change?.SubjectSwap), ("Nauczyciel (zmiana)", change?.TeacherSwap),
                    ("Sala (zmiana)", change?.ClassroomSwap), ("Data dodania", change?.DateAdded))));
        }
        return items;
    }

    public static IReadOnlyList<ObservedItem> FromCompletedLessons(IEnumerable<Lesson> lessons) =>
        lessons.Select(l => new ObservedItem(ElementTypes.CompletedLesson,
                [l.Date, l.LessonNumber.ToString(Inv), l.Subject, l.Teacher, l.Topic],
                Fields(("Data", l.Date), ("Dzień", l.Weekday), ("Nr lekcji", l.LessonNumber.ToString(Inv)), ("Przedmiot", l.Subject),
                    ("Nauczyciel", l.Teacher), ("Temat", l.Topic), ("Frekwencja", l.AttendanceSymbol))))
            .ToList();

    public static DateOnly MondayOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private static async Task<IReadOnlyList<ObservedItem>> ScheduleAsync(Client client, DateOnly today, CancellationToken ct)
    {
        var items = new List<ObservedItem>();
        var first = new DateOnly(today.Year, today.Month, 1);
        foreach (var month in new[] { first, first.AddMonths(1) })
            items.AddRange(FromSchedule(month.Year, month.Month, await Schedule.GetScheduleAsync(client, month.Month, month.Year, false, ct)));
        return items;
    }

    private static bool IsPresence(Attendance attendance) =>
        attendance.Symbol.Trim().Equals("ob", StringComparison.OrdinalIgnoreCase) ||
        attendance.Type.Trim().Equals("obecność", StringComparison.OrdinalIgnoreCase);

    /// <summary>Linie „Etykieta: wartość” z opisu, których nie ma wśród pól podstawowych (np. komentarz, „Dodał”).</summary>
    private static IEnumerable<(string, string?)> DescriptionFields(string description, string[] known)
    {
        var extra = new List<(string, string?)>();
        var loose = new List<string>();
        foreach (var raw in description.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                loose.Add(line);
                continue;
            }
            var label = line[..separator].Trim();
            if (!known.Contains(label, StringComparer.OrdinalIgnoreCase)) extra.Add((label, line[(separator + 1)..].Trim()));
        }
        if (loose.Count > 0) extra.Add(("Opis", string.Join("\n", loose)));
        return extra;
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", Inv);

    private static IReadOnlyList<Field> Fields(params IEnumerable<(string Label, string? Value)> pairs)
    {
        var fields = new List<Field>();
        foreach (var (label, value) in pairs)
        {
            var text = value?.Replace(' ', ' ').Trim();
            if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(label) || fields.Any(f => f.Label == label)) continue;
            fields.Add(new Field(label.Trim(), text));
        }
        return fields;
    }
}
