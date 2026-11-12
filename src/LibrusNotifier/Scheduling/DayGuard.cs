using LibrusNotifier.Timing;

namespace LibrusNotifier.Scheduling;

/// <summary>
/// Pilnuje, by proces obsługiwał zadania tylko do końca dnia, w którym wystartował. Sprawdzenie po zmianie dnia
/// anuluje <see cref="Token"/> (używany we wszystkich operacjach) i rzuca <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class DayGuard : IDisposable
{
    private readonly Clock _clock;
    private readonly CancellationTokenSource _source;
    private volatile bool _dayChanged;

    public DayGuard(Clock clock, DateOnly day, CancellationToken shutdown)
    {
        _clock = clock;
        Day = day;
        _source = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
    }

    public DateOnly Day { get; }

    public CancellationToken Token => _source.Token;

    public bool DayChanged => _dayChanged;

    /// <exception cref="OperationCanceledException">Dzień się zmienił albo zażądano zamknięcia.</exception>
    public void Check()
    {
        if (!_dayChanged && _clock.Today != Day)
        {
            _dayChanged = true;
            _source.Cancel();
        }
        _source.Token.ThrowIfCancellationRequested();
    }

    public void Dispose() => _source.Dispose();
}
