using PublicHoliday;

namespace LibrusNotifier.Calendar;

public enum DayType
{
    /// <summary>Dzień szkolny.</summary>
    School,

    /// <summary>Dzień wolny: weekend lub święto państwowe.</summary>
    Free,

    /// <summary>Wakacje lub ferie (OpenHolidays; ferie zimowe, przerwy świąteczne i wakacje letnie to jeden typ).</summary>
    Holiday
}

public sealed record SchoolHolidayPeriod(DateOnly Start, DateOnly End, string Name)
{
    public bool Contains(DateOnly date) => date >= Start && date <= End;
}

/// <param name="BeforeSchool">Dzień wolny lub wakacyjny, po którym następny dzień jest szkolny.</param>
public sealed record DayInfo(DateOnly Date, DayType Type, bool BeforeSchool, string? Description)
{
    public string DisplayName => (Type switch
    {
        DayType.School => "dzień szkolny",
        DayType.Free => "dzień wolny",
        _ => "wakacje/ferie"
    }) + (BeforeSchool ? " (przed szkołą)" : "") + (Description is null ? "" : $" – {Description}");
}

/// <summary>
/// Typ dnia wg priorytetu: wakacje/ferie &gt; dzień wolny (weekend, święto) &gt; dzień szkolny.
/// Dni wolne ustalane przez szkołę (np. dni dyrektorskie) nie są znane i liczą się jako szkolne.
/// </summary>
public sealed class SchoolCalendar(IReadOnlyList<SchoolHolidayPeriod> schoolHolidays)
{
    private readonly PolandPublicHoliday _publicHolidays = new();

    public DayType GetDayType(DateOnly date) => Classify(date).Type;

    public DayInfo GetDay(DateOnly date)
    {
        var (type, description) = Classify(date);
        var beforeSchool = type != DayType.School && GetDayType(date.AddDays(1)) == DayType.School;
        return new DayInfo(date, type, beforeSchool, description);
    }

    private (DayType Type, string? Description) Classify(DateOnly date)
    {
        var holiday = schoolHolidays.FirstOrDefault(p => p.Contains(date));
        if (holiday is not null) return (DayType.Holiday, holiday.Name);
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return (DayType.Free, null);
        if (_publicHolidays.IsPublicHoliday(date.ToDateTime(TimeOnly.MinValue))) return (DayType.Free, "święto");
        return (DayType.School, null);
    }
}
