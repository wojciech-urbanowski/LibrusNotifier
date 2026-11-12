using System.Globalization;
using LibrusNotifier.Calendar;
using LibrusNotifier.Data;
using LibrusNotifier.Mail;
using LibrusNotifier.Timing;

namespace LibrusNotifier.Digest;

public sealed record DigestDay(DateOnly Date, IReadOnlyList<DigestEvent> Events);

/// <param name="Title">Nagłówek sekcji (np. „Dzisiaj”, „Jutro”, „Poniedziałek”, „Wtorek, 2.11.2027”).</param>
/// <param name="Subtitle">Przygaszony podtytuł (data i odległość od dziś).</param>
/// <param name="Primary">Sekcja główna (wydarzenia jednego dnia); pozostałe to lista kolejnych wydarzeń.</param>
/// <param name="EmptyText">Tekst, gdy sekcja nie ma zdarzeń.</param>
public sealed record DigestSection(string Title, string? Subtitle, bool Primary, IReadOnlyList<DigestDay> Days, string EmptyText)
{
    public int EventCount => Days.Sum(d => d.Events.Count);
}

/// <param name="Banner">Komunikat nad sekcjami (jutro nie ma nauki), albo <c>null</c>.</param>
/// <param name="PrimaryDate">Dzień sekcji głównej (<c>null</c>, gdy nie da się go wskazać).</param>
/// <param name="HasFutureEvents">Czy istnieje jakiekolwiek zdarzenie po dzisiejszym dniu (decyduje o wysyłce w wakacje).</param>
public sealed record DailyDigest(DigestKind Kind, DateOnly Today, DateOnly? PrimaryDate, string? Banner, IReadOnlyList<DigestSection> Sections,
    bool HasFutureEvents)
{
    public DigestSection? PrimarySection => Sections.FirstOrDefault(s => s.Primary);
    public DigestSection Upcoming => Sections.First(s => !s.Primary);
}

/// <summary>
/// Składa zestawienie z listy zdarzeń. Poranne (dzień nauki): sekcja „Dzisiaj”, potem kolejne wydarzenia. Popołudniowe: sekcja „Jutro”;
/// gdy jutro nie ma nauki – wyróżniona sekcja pierwszego dnia nauki po wolnym („Poniedziałek” po weekendzie, inaczej np. „Wtorek, 2.11.2027”).
/// Po sekcji głównej idą pozostałe nadchodzące zdarzenia, chronologicznie.
/// </summary>
public static class DigestBuilder
{
    /// <summary>Tyle dni do przodu szukany jest pierwszy dzień nauki po wolnym (dłuższa przerwa niż wakacje się nie zdarza).</summary>
    private const int SchoolDaySearchLimit = 120;

    private static readonly string[] DayNames = ["niedziela", "poniedziałek", "wtorek", "środa", "czwartek", "piątek", "sobota"];
    private static readonly string[] DayNamesAccusative = ["niedzielę", "poniedziałek", "wtorek", "środę", "czwartek", "piątek", "sobotę"];

    public static DailyDigest Build(DigestKind kind, IReadOnlyList<DigestEvent> events, Clock clock, SchoolCalendar calendar)
    {
        var today = clock.Today;
        var dates = new DateAnnotator(clock, calendar);
        var tomorrow = today.AddDays(1);

        DateOnly? primaryDate;
        string? title = null;
        string? subtitle = null;
        string? banner = null;
        if (kind == DigestKind.Morning)
        {
            primaryDate = today;
            title = "Dzisiaj";
            subtitle = Describe(today, dates);
        }
        else if (calendar.GetDayType(tomorrow) == DayType.School)
        {
            primaryDate = tomorrow;
            title = "Jutro";
            subtitle = Describe(tomorrow, dates);
        }
        else
        {
            primaryDate = FirstSchoolDayAfter(today, calendar);
            if (primaryDate is { } first)
            {
                var weekendOnly = IsWeekendOnlyBreak(tomorrow, first, calendar);
                title = weekendOnly ? Capitalize(DayName(first)) : $"{Capitalize(DayName(first))}, {Short(first)}";
                subtitle = weekendOnly
                    ? $"{Short(first)} ({RelativePart(dates, first)}) · pierwszy dzień nauki po weekendzie"
                    : $"{RelativePart(dates, first)} · pierwszy dzień nauki po wolnym";
                banner = $"Jutro ({DayName(tomorrow)}, {Short(tomorrow)}) nie ma nauki: {Reason(calendar.GetDay(tomorrow))}. " +
                    $"Pierwszy dzień nauki to {DayName(first)}, {Short(first)} ({RelativePart(dates, first)}) - jego lista jest poniżej, jako pierwsza.";
            }
        }

        var byDay = events.Where(e => e.Date > today && e.Date != primaryDate)
            .GroupBy(e => e.Date).OrderBy(g => g.Key)
            .Select(g => new DigestDay(g.Key, g.ToList()))
            .ToList();

        var sections = new List<DigestSection>();
        if (primaryDate is { } date && title is not null)
        {
            var primaryEvents = events.Where(e => e.Date == date).ToList();
            sections.Add(new DigestSection(title, subtitle, Primary: true,
                primaryEvents.Count == 0 ? [] : [new DigestDay(date, primaryEvents)],
                kind == DigestKind.Morning ? "Na dziś nie ma zdarzeń w terminarzu." : "Na ten dzień nie ma zdarzeń w terminarzu."));
        }
        sections.Add(new DigestSection("Kolejne wydarzenia", null, Primary: false, byDay, "Brak kolejnych wydarzeń w terminarzu."));

        return new DailyDigest(kind, today, primaryDate, banner, sections, events.Any(e => e.Date > today));
    }

    public static string DayName(DateOnly date) => DayNames[(int)date.DayOfWeek];

    /// <summary>Nazwa dnia w bierniku, do zwrotu „na środę”.</summary>
    public static string DayNameAccusative(DateOnly date) => DayNamesAccusative[(int)date.DayOfWeek];

    /// <summary>Data w zapisie bez zer wiodących przy dniu, np. <c>2.11.2027</c>.</summary>
    public static string Short(DateOnly date) => date.ToString("d.MM.yyyy", CultureInfo.InvariantCulture);

    public static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Nagłówek dnia na liście kolejnych wydarzeń, np. „Piątek, 13.11.2026 (jutro)”.</summary>
    public static string DayHeading(DateOnly date, DateAnnotator dates) => $"{Capitalize(DayName(date))}, {Short(date)} ({RelativePart(dates, date)})";

    private static string Describe(DateOnly date, DateAnnotator dates) => $"{Short(date)} ({dates.Describe(date)})";

    // „piątek, jutro” → „jutro”: nazwa dnia jest już w nagłówku.
    private static string RelativePart(DateAnnotator dates, DateOnly date)
    {
        var text = dates.Describe(date);
        var comma = text.IndexOf(", ", StringComparison.Ordinal);
        return comma < 0 ? text : text[(comma + 2)..];
    }

    private static DateOnly? FirstSchoolDayAfter(DateOnly today, SchoolCalendar calendar)
    {
        for (var i = 1; i <= SchoolDaySearchLimit; i++)
        {
            var day = today.AddDays(i);
            if (calendar.GetDayType(day) == DayType.School) return day;
        }
        return null;
    }

    /// <summary>Przerwa to zwykły weekend: każdy dzień od jutra do dnia przed pierwszym dniem nauki jest sobotą lub niedzielą (bez ferii).</summary>
    private static bool IsWeekendOnlyBreak(DateOnly firstFree, DateOnly firstSchool, SchoolCalendar calendar)
    {
        for (var day = firstFree; day < firstSchool; day = day.AddDays(1))
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) || calendar.GetDayType(day) != DayType.Free) return false;
        return true;
    }

    private static string Reason(DayInfo day)
    {
        if (day.Type == DayType.Holiday) return day.Description ?? "wakacje/ferie";
        if (day.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return "weekend";
        return day.Description ?? "dzień wolny";
    }
}
