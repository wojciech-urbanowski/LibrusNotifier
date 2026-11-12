namespace LibrusNotifier.Timing;

/// <summary>Zegar aplikacji. Czas lokalny to zawsze strefa Europe/Warsaw, niezależnie od ustawień systemu.</summary>
public abstract class Clock
{
    protected Clock(TimeZoneInfo zone) => Zone = zone;

    public TimeZoneInfo Zone { get; }

    public abstract DateTimeOffset UtcNow { get; }

    /// <summary>Bieżący czas lokalny (strefa <see cref="Zone"/>), <see cref="DateTimeKind.Unspecified"/>.</summary>
    public DateTime Now => ToLocal(UtcNow);

    public DateOnly Today => DateOnly.FromDateTime(Now);

    public DateTime ToLocal(DateTimeOffset time) => DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(time, Zone).DateTime, DateTimeKind.Unspecified);

    /// <summary>
    /// Zamienia czas lokalny na UTC. Godzina nieistniejąca (wiosenna zmiana czasu) jest przesuwana o godzinę,
    /// dla niejednoznacznej (jesienna zmiana) wybierany jest wcześniejszy moment.
    /// </summary>
    public DateTimeOffset ToUtc(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (Zone.IsInvalidTime(local)) local = local.AddHours(1);
        var offset = Zone.IsAmbiguousTime(local) ? Zone.GetAmbiguousTimeOffsets(local).Max() : Zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}

public sealed class SystemClock(TimeZoneInfo zone, TimeProvider? provider = null) : Clock(zone)
{
    private readonly TimeProvider _provider = provider ?? TimeProvider.System;

    public override DateTimeOffset UtcNow => _provider.GetUtcNow();
}

/// <summary>TimeProvider dla biblioteki Librus: lokalna strefa to strefa zegara aplikacji.</summary>
public sealed class ClockTimeProvider(Clock clock) : TimeProvider
{
    public override TimeZoneInfo LocalTimeZone => clock.Zone;

    public override DateTimeOffset GetUtcNow() => clock.UtcNow;
}

public static class WarsawTime
{
    /// <exception cref="InvalidOperationException">Brak danych strefy czasowej w systemie.</exception>
    public static TimeZoneInfo LoadZone()
    {
        foreach (var id in new[] { "Europe/Warsaw", "Central European Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Następny identyfikator.
            }
        }
        throw new InvalidOperationException("Nie znaleziono strefy czasowej Europe/Warsaw (brak danych stref czasowych w systemie).");
    }
}
