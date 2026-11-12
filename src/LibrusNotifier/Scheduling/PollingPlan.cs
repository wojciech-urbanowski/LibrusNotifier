using LibrusNotifier.Calendar;

namespace LibrusNotifier.Scheduling;

public enum JitterMode
{
    /// <summary>„±”: odchylenie symetryczne od interwału.</summary>
    Symmetric,

    /// <summary>„+”: wyłącznie dodatnie odchylenie.</summary>
    PositiveOnly
}

/// <summary>Okno odpytywania w ciągu doby (czas lokalny Europe/Warsaw); <see cref="End"/> może wynosić 24:00.</summary>
public sealed record PollingWindow(string Name, TimeSpan Start, TimeSpan End, TimeSpan Interval, TimeSpan Jitter, JitterMode Mode)
{
    public bool Contains(TimeSpan timeOfDay) => timeOfDay >= Start && timeOfDay < End;

    public override string ToString() =>
        $"{Name} {Format(Start)} - {Format(End)}, co {Interval.TotalMinutes:0} min {(Mode == JitterMode.Symmetric ? "±" : "+0…")}{Jitter.TotalMinutes:0} min";

    private static string Format(TimeSpan time) => $"{(int)time.TotalHours:00}:{time.Minutes:00}";
}

/// <summary>Stały, zakodowany harmonogram odpytywania Librusa.</summary>
public static class PollingPlan
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    public static IReadOnlyList<PollingWindow> School { get; } =
    [
        Window("szkolny, noc", 0, 6, 60, 10),
        Window("szkolny", 6, 16, 15, 4),
        Window("szkolny, wieczór", 16, 22, 25, 5),
        Window("szkolny, noc", 22, 24, 60, 10)
    ];

    public static IReadOnlyList<PollingWindow> Free { get; } =
    [
        Window("wolny", 8, 22, 180, 20)
    ];

    /// <summary>Dzień wolny lub wakacyjny, po którym jest dzień szkolny.</summary>
    public static IReadOnlyList<PollingWindow> BeforeSchool { get; } =
    [
        Window("przed szkołą, rano", 8, 12, 180, 20),
        Window("przed szkołą, popołudnie", 12, 20, 60, 10),
        Window("przed szkołą, wieczór", 20, 23, 25, 5)
    ];

    public static IReadOnlyList<PollingWindow> Holiday { get; } =
    [
        Window("wakacje/ferie", 0, 24, 360, 60, JitterMode.PositiveOnly)
    ];

    public static IReadOnlyList<PollingWindow> ForDay(DayInfo day) => day switch
    {
        { BeforeSchool: true } => BeforeSchool,
        { Type: DayType.School } => School,
        { Type: DayType.Free } => Free,
        _ => Holiday
    };

    private static PollingWindow Window(string name, int startHour, int endHour, int intervalMinutes, int jitterMinutes,
        JitterMode mode = JitterMode.Symmetric) =>
        new(name, TimeSpan.FromHours(startHour), TimeSpan.FromHours(endHour), intervalMinutes * Minute, jitterMinutes * Minute, mode);
}
