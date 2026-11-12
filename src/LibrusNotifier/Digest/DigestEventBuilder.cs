using System.Globalization;
using System.Text.RegularExpressions;
using LibrusNotifier.Calendar;
using LibrusNotifier.Data;
using LibrusNotifier.Librus;
using LibrusNotifier.Processing;

namespace LibrusNotifier.Digest;

/// <summary>
/// Zamienia zapisane w bazie elementy (terminarz, zmiany w planie lekcji, zadania domowe) na zdarzenia zestawienia.
/// Nie wymaga żądań do Librusa: korzysta wyłącznie z danych zebranych przez pingi.
/// </summary>
public static partial class DigestEventBuilder
{
    /// <summary>Typy elementów, z których powstają zdarzenia zestawienia.</summary>
    public static IReadOnlyList<string> SourceTypes { get; } = [ElementTypes.ScheduleEvent, ElementTypes.TimetableChange, ElementTypes.Homework];

    // „Zastępstwo z Karina Mirowska na lekcji nr: 1 (Język polski)” i „Izabela Śmieszniak na lekcji nr: 2 (Matematyka)”.
    [GeneratedRegex(@"^(?:Zastępstwo z\s+)?(?<teacher>.+?)\s+na lekcji nr:?\s*(?<lesson>\d+)\s*\((?<subject>.+)\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LessonChangePattern();

    /// <param name="elements">Elementy z bazy (dowolne typy; nieobsługiwane są pomijane).</param>
    /// <param name="baselines">Znaczniki pobrań list; służą do odrzucenia elementów, które zniknęły z listy.</param>
    /// <param name="group">Grupa odbiorców: rodzice widzą wszystko, uczeń tylko elementy widoczne na swoim koncie.</param>
    /// <param name="calendar">Kalendarz szkolny: wpis szkoły na cały dzień w dniu, w którym nie ma nauki, to dzień wolny.</param>
    /// <returns>Zdarzenia ze wszystkich dni, posortowane chronologicznie.</returns>
    public static IReadOnlyList<DigestEvent> Build(IEnumerable<ElementEntity> elements, IReadOnlyList<BaselineEntity> baselines, RecipientGroup group,
        SchoolCalendar calendar)
    {
        var events = new List<DigestEvent>();
        foreach (var element in elements)
        {
            if (!SourceTypes.Contains(element.Type)) continue;
            if (group == RecipientGroup.Student && !element.VisibleToStudent) continue;
            if (!IsCurrent(element, baselines)) continue;

            var fields = FieldJson.Deserialize(element.DataJson)
                .GroupBy(f => f.Label, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
            var parsed = element.Type switch
            {
                ElementTypes.ScheduleEvent => FromSchedule(fields, calendar),
                ElementTypes.TimetableChange => FromTimetable(fields),
                _ => FromHomework(fields)
            };
            if (parsed is not null) events.Add(parsed);
        }

        return Merge(events)
            .OrderBy(e => e.Date)
            .ThenBy(e => e.TimeOrder.Group).ThenBy(e => e.TimeOrder.Position)
            .ThenBy(e => e.Category)
            .ThenBy(e => e.Subject, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Terminarz i plan lekcji są pobierane wraz z zapisem znacznika w <see cref="BaselineEntity.LastFetchUtc"/>. Element, którego
    /// <see cref="ElementEntity.LastSeenUtc"/> jest starszy niż ostatnie pobranie listy z kont, na których był widoczny, zniknął z niej
    /// (np. sprawdzian przeniesiony na inny dzień dostaje nowy klucz, a stary wpis zostaje w bazie) i nie trafia do zestawienia.
    /// Zadania domowe nie podlegają tej regule: lista obejmuje tylko ostatnie 7 dni, a termin wykonania może być dalszy.
    /// </summary>
    internal static bool IsCurrent(ElementEntity element, IReadOnlyList<BaselineEntity> baselines)
    {
        var module = element.Type switch
        {
            ElementTypes.ScheduleEvent => ModuleKind.Schedule,
            ElementTypes.TimetableChange => ModuleKind.Timetable,
            _ => (ModuleKind?)null
        };
        if (module is null) return true;
        var latest = baselines.Where(b => b.Module == module && element.IsVisibleTo(b.Account)).Select(b => b.LastFetchUtc).DefaultIfEmpty().Max();
        return element.LastSeenUtc >= latest;
    }

    private static DigestEvent? FromSchedule(IReadOnlyDictionary<string, string> f, SchoolCalendar calendar)
    {
        if (!TryDate(f, "Data", out var date)) return null;
        var title = Text(f, "Tytuł");
        var subject = Text(f, "Przedmiot");
        var teacher = Text(f, "Nauczyciel");
        var description = Text(f, "Opis");
        var hours = Text(f, "Godzina");
        int? lesson = int.TryParse(Text(f, "Nr lekcji"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
        var shown = title.Length > 0 ? title : subject;
        var lower = shown.ToLowerInvariant();
        var subjectLower = subject.ToLowerInvariant();

        if (subjectLower.StartsWith("odwołane", StringComparison.Ordinal) || lower.StartsWith("odwołane", StringComparison.Ordinal))
        {
            var change = Parse(title) ?? Parse(subject);
            return new DigestEvent(date, EventCategory.Cancelled, "Odwołane zajęcia", change?.Lesson ?? lesson, hours,
                change?.Subject ?? shown, description, change?.Teacher ?? teacher, EventSource.Schedule);
        }
        if (lower.StartsWith("zastępstwo", StringComparison.Ordinal) || subjectLower.StartsWith("zastępstwo", StringComparison.Ordinal))
        {
            var change = Parse(title) ?? Parse(subject);
            var detail = change is null ? "" : $"Zastępstwo: {change.Teacher}";
            return new DigestEvent(date, EventCategory.Substitution, "Zastępstwo", change?.Lesson ?? lesson, hours,
                change?.Subject ?? shown, Join(detail, description), teacher, EventSource.Schedule);
        }

        // Wpis szkoły na cały dzień ma tylko nazwę (np. „Święto Niepodległości: SP 33”). Gdy w tym dniu nie ma nauki (święto, weekend, ferie),
        // to dzień wolny; w dniu nauki (np. „Rozpoczęcie roku”) – zwykłe wydarzenie szkolne.
        var allDay = subject == title && lesson is null && hours.Length == 0 && teacher.Length == 0 && description.Length == 0;
        var category = Classify(lower, allDay ? calendar.GetDayType(date) : null);
        return category switch
        {
            EventCategory.DayOff => new DigestEvent(date, category, "Dzień wolny", null, "", ElementKeys.Normalize(shown), "", "", EventSource.Schedule),
            EventCategory.Event when allDay => new DigestEvent(date, category, "Wydarzenie szkolne", null, "", ElementKeys.Normalize(shown), "", "", EventSource.Schedule),
            _ => new DigestEvent(date, category, LabelFor(category, shown), lesson, hours, subject.Length > 0 ? subject : shown,
                description, teacher, EventSource.Schedule)
        };
    }

    /// <param name="allDayType">Typ dnia, gdy wpis ma tylko nazwę (cały dzień); <c>null</c> dla wpisów z lekcją, godziną, nauczycielem lub opisem.</param>
    private static EventCategory Classify(string lowerTitle, DayType? allDayType)
    {
        if (ContainsAny(lowerTitle, "sprawdzian", "praca klasowa", "klasówk", "egzamin") || TestWord().IsMatch(lowerTitle)) return EventCategory.Test;
        if (lowerTitle.Contains("kartkówk", StringComparison.Ordinal) || lowerTitle.Contains("kartkowk", StringComparison.Ordinal)) return EventCategory.Quiz;
        if (ContainsAny(lowerTitle, "zadanie domowe", "praca domowa")) return EventCategory.Homework;
        if (ContainsAny(lowerTitle, "dzień wolny", "dni wolne", "wolne od zajęć")) return EventCategory.DayOff;
        if (allDayType is { } type) return type == DayType.School ? EventCategory.Event : EventCategory.DayOff;
        if (ContainsAny(lowerTitle, "wycieczk", "zebranie", "wyjście", "wyjscie", "apel", "konkurs", "akademi", "rada pedagog", "uroczysto",
                "przedstawieni", "spektakl", "zawody", "dzień ")) return EventCategory.Event;
        return EventCategory.Other;
    }

    [GeneratedRegex(@"(?<![\p{L}])test(?![\p{L}])")]
    private static partial Regex TestWord();

    private static string LabelFor(EventCategory category, string title) => category switch
    {
        EventCategory.Quiz => "Kartkówka",
        EventCategory.Homework => "Zadanie domowe",
        _ => Capitalize(ElementKeys.Normalize(title))
    };

    private static DigestEvent? FromTimetable(IReadOnlyDictionary<string, string> f)
    {
        if (!TryDate(f, "Data", out var date)) return null;
        var change = Text(f, "Zmiana");
        var lower = change.ToLowerInvariant();
        var subject = Text(f, "Przedmiot");
        var teacherRoom = Text(f, "Nauczyciel i sala");
        var hours = Text(f, "Godziny").Trim('–', '-', ' ');
        int? lesson = int.TryParse(Text(f, "Nr lekcji"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
        var subjectSwap = Swap(Text(f, "Przedmiot (zmiana)"));
        var teacherSwap = Swap(Text(f, "Nauczyciel (zmiana)"));
        var roomSwap = Swap(Text(f, "Sala (zmiana)"));

        if (lower.Contains("dzień wolny", StringComparison.Ordinal))
            return new DigestEvent(date, EventCategory.DayOff, "Dzień wolny", null, "", "Dzień wolny od zajęć", "", "", EventSource.Timetable);
        if (lower.Contains("odwołan", StringComparison.Ordinal))
            return new DigestEvent(date, EventCategory.Cancelled, "Odwołane zajęcia", lesson, hours, subject, "", teacherRoom, EventSource.Timetable);

        var detail = Join(
            teacherSwap.Length > 0 ? $"Zastępstwo: {teacherSwap}" : "",
            subjectSwap.Length > 0 && !string.Equals(subjectSwap, subject, StringComparison.OrdinalIgnoreCase) ? $"Przedmiot: {subjectSwap}" : "",
            roomSwap.Length > 0 ? $"Sala: {roomSwap}" : "");
        if (lower.Contains("zastępstw", StringComparison.Ordinal))
            return new DigestEvent(date, EventCategory.Substitution, "Zastępstwo", lesson, hours, subject, detail, "", EventSource.Timetable);
        return new DigestEvent(date, EventCategory.Other, change.Length > 0 ? Capitalize(change) : "Zmiana w planie", lesson, hours, subject,
            detail, teacherRoom, EventSource.Timetable);
    }

    /// <summary>Librus zapisuje zmianę jako „stare -> nowe” (brak jako „[brak]”).</summary>
    private static string Swap(string text) => text.Replace("->", "→", StringComparison.Ordinal).Replace("[brak]", "brak", StringComparison.Ordinal);

    private static DigestEvent? FromHomework(IReadOnlyDictionary<string, string> f)
    {
        if (!TryDate(f, "Termin wykonania", out var date)) return null;
        return new DigestEvent(date, EventCategory.Homework, "Zadanie domowe", null, "", Text(f, "Przedmiot"), Text(f, "Temat"),
            Text(f, "Nauczyciel"), EventSource.Homework);
    }

    /// <summary>
    /// Ten sam fakt bywa w kilku listach: zastępstwo lub odwołana lekcja jest i w terminarzu, i w planie lekcji; zadanie domowe w terminarzu
    /// i w zadaniach domowych; dzień wolny w terminarzu i w planie (osobno dla każdej lekcji). Zostaje jedno zdarzenie z najbogatszego źródła.
    /// </summary>
    private static IEnumerable<DigestEvent> Merge(List<DigestEvent> events)
    {
        var result = new List<DigestEvent>();
        foreach (var group in events.GroupBy(MergeKey))
        {
            var items = group.ToList();
            if (group.Key is null || items.Count == 1)
            {
                result.AddRange(items);
                continue;
            }
            var primary = items.OrderBy(e => Preference(e)).First();
            var others = items.Where(e => !ReferenceEquals(e, primary)).ToList();
            var merged = primary with
            {
                Hours = primary.Hours.Length > 0 ? primary.Hours : others.Select(e => e.Hours).FirstOrDefault(h => h.Length > 0) ?? "",
                Lesson = primary.Lesson ?? others.Select(e => e.Lesson).FirstOrDefault(l => l is not null),
                Detail = primary.Detail.Length > 0 ? primary.Detail : others.Select(e => e.Detail).FirstOrDefault(d => d.Length > 0) ?? "",
                Note = primary.Note.Length > 0 ? primary.Note : others.Select(e => e.Note).FirstOrDefault(n => n.Length > 0) ?? ""
            };
            if (primary.Category == EventCategory.Homework)
            {
                // Opis z terminarza uzupełnia temat z zadań domowych, o ile go jeszcze nie ma.
                var extra = others.Select(e => e.Detail).FirstOrDefault(d => d.Length > 0 && !merged.Detail.Contains(d, StringComparison.OrdinalIgnoreCase));
                if (extra is not null && merged.Detail.Length > 0) merged = merged with { Detail = merged.Detail + "\n" + extra };
            }
            result.Add(merged);
        }
        return result;
    }

    private static string? MergeKey(DigestEvent e) => e.Category switch
    {
        EventCategory.Substitution or EventCategory.Cancelled when e.Lesson is not null => $"{e.Date:O}|{e.Category}|{e.Lesson}",
        EventCategory.DayOff => $"{e.Date:O}|{e.Category}",
        EventCategory.Homework => $"{e.Date:O}|{e.Category}|{ElementKeys.Normalize(e.Subject).ToLowerInvariant()}",
        _ => null
    };

    // Plan lekcji ma godziny i dane o zmianie; zadania domowe mają temat; w terminarzu dzień wolny ma nazwę (np. święta).
    private static int Preference(DigestEvent e) => e.Category switch
    {
        EventCategory.Homework => e.Source == EventSource.Homework ? 0 : 1,
        EventCategory.DayOff => e.Source == EventSource.Schedule ? 0 : 1,
        _ => e.Source == EventSource.Timetable ? 0 : 1
    };

    private static LessonChange? Parse(string text)
    {
        var match = LessonChangePattern().Match(ElementKeys.Normalize(text));
        return match.Success
            ? new LessonChange(match.Groups["teacher"].Value, int.Parse(match.Groups["lesson"].Value, CultureInfo.InvariantCulture), match.Groups["subject"].Value)
            : null;
    }

    private sealed record LessonChange(string Teacher, int Lesson, string Subject);

    private static string Text(IReadOnlyDictionary<string, string> f, string label) => f.TryGetValue(label, out var value) ? value.Trim() : "";

    private static bool TryDate(IReadOnlyDictionary<string, string> f, string label, out DateOnly date)
    {
        var text = Text(f, label);
        // Pole może mieć godzinę po dacie; liczy się pierwsze 10 znaków.
        return DateOnly.TryParseExact(text.Length >= 10 ? text[..10] : text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool ContainsAny(string text, params string[] needles) => needles.Any(n => text.Contains(n, StringComparison.Ordinal));

    private static string Join(params string[] parts) => string.Join("\n", parts.Where(p => p.Length > 0));

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
