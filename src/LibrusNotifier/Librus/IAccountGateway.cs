namespace LibrusNotifier.Librus;

/// <summary>Dostęp do jednego konta Librusa (rodzica lub ucznia).</summary>
public interface IAccountGateway : IDisposable
{
    AccountKind Account { get; }

    /// <summary>Nowe logowanie (poprzedni token i ciasteczka są odrzucane), a zaraz po nim odczyt panelu powiadomień.</summary>
    Task<PanelCounters> LoginAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sprawdza sesję przed operacją: po przerwie dłuższej niż limit bezczynności loguje ponownie i zwraca liczniki panelu,
    /// w przeciwnym razie zwraca <c>null</c>.
    /// </summary>
    Task<PanelCounters?> EnsureSessionAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ObservedItem>> FetchListAsync(ModuleKind module, CancellationToken cancellationToken);

    /// <summary>Szczegóły elementu do treści maila; <c>null</c>, gdy dla typu lista wystarcza.</summary>
    Task<IReadOnlyList<Field>?> FetchDetailsAsync(ObservedItem item, CancellationToken cancellationToken);

    /// <summary>Szczegóły są pobierane tylko dla typów, w których zawierają więcej niż lista.</summary>
    bool HasDetails(ObservedItem item);

    /// <summary>Usuwa lokalny token i ciasteczka (np. po błędzie).</summary>
    void Invalidate();
}
