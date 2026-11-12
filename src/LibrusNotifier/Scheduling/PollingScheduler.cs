namespace LibrusNotifier.Scheduling;

/// <summary>
/// Wyznacza terminy pingów w obrębie jednego dnia (czas lokalny). Kolejny ping to poprzedni + interwał okna
/// z losowym odchyleniem; wejście w kolejne okno zaczyna się pingiem na jego początku. Poza oknami Librus nie jest odpytywany.
/// </summary>
public sealed class PollingScheduler(DateOnly day, IReadOnlyList<PollingWindow> windows, Random random)
{
    private DateTime DayStart => day.ToDateTime(TimeOnly.MinValue);
    private DateTime DayEnd => DayStart.AddDays(1);

    public IReadOnlyList<PollingWindow> Windows => windows;

    /// <summary>Pierwszy ping: od razu, gdy <paramref name="now"/> jest w oknie; inaczej początek następnego okna tego dnia albo <c>null</c>.</summary>
    public DateTime? FirstPing(DateTime now)
    {
        if (now < DayStart || now >= DayEnd) return null;
        return WindowAt(now) is not null ? now : NextWindowStartAfter(now);
    }

    /// <summary>
    /// Następny ping po pingu rozpoczętym o <paramref name="previousPing"/>; <c>null</c>, gdy do końca dnia nie ma już terminu w oknie.
    /// </summary>
    public DateTime? NextPing(DateTime previousPing, DateTime now)
    {
        var window = WindowAt(previousPing);
        if (window is null) return FirstPing(now);
        DateTime? next = previousPing + window.Interval + Jitter(window);
        var nextStart = NextWindowStartAfter(previousPing);
        if (nextStart is { } start && start <= next) next = start;
        else if (WindowAt(next.Value) is null) next = nextStart;
        if (next is { } value && value < now) return FirstPing(now);
        return next;
    }

    public PollingWindow? WindowAt(DateTime time) =>
        time >= DayStart && time < DayEnd ? windows.FirstOrDefault(w => w.Contains(time - DayStart)) : null;

    private DateTime? NextWindowStartAfter(DateTime time)
    {
        DateTime? best = null;
        foreach (var window in windows)
        {
            var start = DayStart + window.Start;
            if (start > time && start < DayEnd && (best is null || start < best)) best = start;
        }
        return best;
    }

    private TimeSpan Jitter(PollingWindow window) => window.Mode == JitterMode.Symmetric
        ? window.Jitter * (random.NextDouble() * 2 - 1)
        : window.Jitter * random.NextDouble();
}
