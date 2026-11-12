using LibrusNotifier.Timing;

namespace LibrusNotifier.Librus;

/// <summary>
/// Czas ostatniej aktywności sesji jednego konta: ustawiany po poprawnym logowaniu i po każdej odpowiedzi 2xx
/// w zalogowanej sesji. Sesja wygasa lokalnie dopiero po przerwie dłuższej niż limit (na granicy jeszcze działa).
/// </summary>
public sealed class SessionActivity(Clock clock, TimeSpan idleLimit)
{
    private readonly Lock _gate = new();
    private DateTimeOffset? _lastActivity;

    public TimeSpan IdleLimit => idleLimit;

    public bool IsLoggedIn
    {
        get
        {
            lock (_gate) return _lastActivity is not null;
        }
    }

    /// <summary>Sesja nie istnieje albo od ostatniej aktywności minęło więcej niż limit.</summary>
    public bool IsExpired
    {
        get
        {
            lock (_gate) return _lastActivity is not { } last || clock.UtcNow - last > idleLimit;
        }
    }

    public void LoggedIn()
    {
        lock (_gate) _lastActivity = clock.UtcNow;
    }

    /// <summary>Odpowiedź HTTP 2xx: odnawia aktywność tylko w zalogowanej sesji.</summary>
    public void Touch()
    {
        lock (_gate)
            if (_lastActivity is not null) _lastActivity = clock.UtcNow;
    }

    public void Clear()
    {
        lock (_gate) _lastActivity = null;
    }
}
