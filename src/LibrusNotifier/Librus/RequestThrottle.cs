using LibrusNotifier.Timing;

namespace LibrusNotifier.Librus;

/// <summary>
/// Minimalny odstęp między kolejnymi żądaniami HTTP do Librusa, wspólny dla obu kont (to ten sam serwer).
/// Po zakończeniu każdego żądania losowany jest odstęp z przedziału [<see cref="MinGap"/>, <see cref="MaxGap"/>],
/// liczony od chwili zakończenia; kolejne żądanie czeka, jeśli naturalna przerwa była krótsza.
/// Żądania są szeregowane, więc odstęp obowiązuje także przy równoległych wywołaniach.
/// Kroki samego logowania (kolejne żądania handshake'u OAuth, także przekierowania) mają krótszy odstęp
/// z przedziału [<see cref="LoginMinGap"/>, <see cref="LoginMaxGap"/>] – kody autoryzacyjne OAuth żyją krótko,
/// a odstęp nigdy nie jest dłuższy niż zwykły.
/// </summary>
public sealed class RequestThrottle
{
    public static readonly TimeSpan DefaultMinGap = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultMaxGap = TimeSpan.FromSeconds(11);
    public static readonly TimeSpan DefaultLoginMinGap = TimeSpan.Zero;
    public static readonly TimeSpan DefaultLoginMaxGap = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Clock _clock;
    private readonly Random _random;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private DateTimeOffset? _nextAllowed;
    private DateTimeOffset? _lastFinished;

    public RequestThrottle(Clock clock, Random? random = null, TimeSpan? minGap = null, TimeSpan? maxGap = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? loginMinGap = null, TimeSpan? loginMaxGap = null)
    {
        MinGap = minGap ?? DefaultMinGap;
        MaxGap = maxGap ?? DefaultMaxGap;
        LoginMinGap = loginMinGap ?? DefaultLoginMinGap;
        LoginMaxGap = loginMaxGap ?? DefaultLoginMaxGap;
        if (MinGap < TimeSpan.Zero || MaxGap < MinGap) throw new ArgumentOutOfRangeException(nameof(maxGap), "Wymagane 0 <= minGap <= maxGap.");
        if (LoginMinGap < TimeSpan.Zero || LoginMaxGap < LoginMinGap)
            throw new ArgumentOutOfRangeException(nameof(loginMaxGap), "Wymagane 0 <= loginMinGap <= loginMaxGap.");
        _clock = clock;
        _random = random ?? Random.Shared;
        _delay = delay ?? Task.Delay;
    }

    public TimeSpan MinGap { get; }
    public TimeSpan MaxGap { get; }
    public TimeSpan LoginMinGap { get; }
    public TimeSpan LoginMaxGap { get; }

    /// <summary>Czeka na swoją kolej i wymagany odstęp. Zwolnienie zwróconego obiektu kończy żądanie i losuje następny odstęp.</summary>
    /// <param name="loginStep">
    /// Kolejny krok logowania (nie pierwszy): czeka krótki odstęp od zakończenia poprzedniego żądania zamiast zwykłego.
    /// </param>
    public async Task<Slot> AcquireAsync(CancellationToken cancellationToken, bool loginStep = false)
    {
        await _gate.WaitAsync(cancellationToken);
        var waited = TimeSpan.Zero;
        try
        {
            var target = _nextAllowed;
            if (loginStep && _lastFinished is { } finished)
            {
                var shortGap = LoginMinGap + (LoginMaxGap - LoginMinGap) * _random.NextDouble();
                if (target is null || finished + shortGap < target) target = finished + shortGap;
            }
            while (target is { } next)
            {
                var remaining = next - _clock.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                await _delay(remaining, cancellationToken);
                waited += remaining;
            }
        }
        catch
        {
            _gate.Release();
            throw;
        }
        return new Slot(this, waited);
    }

    private void Release()
    {
        _lastFinished = _clock.UtcNow;
        _nextAllowed = _lastFinished + MinGap + (MaxGap - MinGap) * _random.NextDouble();
        _gate.Release();
    }

    public sealed class Slot : IDisposable
    {
        private RequestThrottle? _owner;

        internal Slot(RequestThrottle owner, TimeSpan waited)
        {
            _owner = owner;
            Waited = waited;
        }

        /// <summary>Czas oczekiwania na wymagany odstęp przed tym żądaniem.</summary>
        public TimeSpan Waited { get; }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
