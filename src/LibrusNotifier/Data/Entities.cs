using LibrusNotifier.Librus;

namespace LibrusNotifier.Data;

public enum RecipientGroup
{
    Parents,
    Student
}

public enum NotificationStatus
{
    Pending,
    Sent,

    /// <summary>Grupa nie ma adresów (pusta lista <c>[odbiorcy_uczen]</c>).</summary>
    Skipped
}

/// <summary>Obserwowany element Librusa. Jeden wpis na element, niezależnie od liczby kont, na których jest widoczny.</summary>
public sealed class ElementEntity
{
    public int Id { get; set; }

    /// <summary>Typ elementu (<see cref="ElementTypes"/>).</summary>
    public required string Type { get; set; }

    /// <summary>Klucz wyliczony z treści (SHA-256), niezależny od identyfikatorów konta.</summary>
    public required string Key { get; set; }

    /// <summary>Dane z listy: obiekt JSON etykieta → wartość.</summary>
    public required string DataJson { get; set; }

    /// <summary>Szczegóły pobrane do treści maila (np. treść wiadomości), obiekt JSON etykieta → wartość.</summary>
    public string? DetailsJson { get; set; }

    public bool VisibleToParent { get; set; }
    public bool VisibleToStudent { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }

    /// <summary>
    /// Kiedy wykryto, że element zniknął z listy w Librusie (usunięty lub przeniesiony), i obsłużono to (powiadomienie o usunięciu
    /// albo o zmianie terminu). <c>null</c>: element jest na liście albo zniknięcie nie zostało jeszcze wykryte. Ponowne pojawienie się
    /// elementu na liście czyści znacznik.
    /// </summary>
    public DateTime? GoneHandledUtc { get; set; }

    public List<NotificationEntity> Notifications { get; set; } = [];

    public bool IsVisibleTo(AccountKind account) => account == AccountKind.Parent ? VisibleToParent : VisibleToStudent;

    public void MarkVisibleTo(AccountKind account)
    {
        if (account == AccountKind.Parent) VisibleToParent = true;
        else VisibleToStudent = true;
    }
}

/// <summary>Rodzaj powiadomienia o elemencie.</summary>
public enum NotificationKind
{
    /// <summary>Nowy element.</summary>
    New,

    /// <summary>Zmiana terminu: zamiast powiadomienia „nowy” dla elementu, który zastąpił zniknięty (<see cref="NotificationEntity.RelatedElementId"/>).</summary>
    Moved,

    /// <summary>Element zniknął z listy w Librusie (usunięty); przy prawdopodobnym przeniesieniu wskazuje nowy element w <see cref="NotificationEntity.RelatedElementId"/>.</summary>
    Removed
}

/// <summary>Powiadomienie elementu dla jednej grupy odbiorców; najwyżej jedno na trójkę (element, grupa, rodzaj).</summary>
public sealed class NotificationEntity
{
    public int Id { get; set; }
    public int ElementId { get; set; }
    public ElementEntity Element { get; set; } = null!;
    public RecipientGroup Group { get; set; }
    public NotificationKind Kind { get; set; } = NotificationKind.New;

    /// <summary>
    /// <see cref="NotificationKind.Moved"/>: poprzedni (zniknięty) element, z którego termin zmieniono na ten element.
    /// <see cref="NotificationKind.Removed"/>: element, na który prawdopodobnie przeniesiono zniknięty wpis.
    /// </summary>
    public int? RelatedElementId { get; set; }
    public NotificationStatus Status { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? SentUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// Znacznik pierwszego udanego pobrania listy modułu z danego konta. Pierwsze pobranie tylko zapisuje dotychczasowe
/// dane, bez powiadomień (pusta baza, ale też np. konto, którego logowanie wcześniej się nie udawało).
/// </summary>
public sealed class BaselineEntity
{
    public AccountKind Account { get; set; }
    public ModuleKind Module { get; set; }
    public DateTime CompletedUtc { get; set; }

    /// <summary>
    /// Czas ostatniego udanego pobrania listy modułu z tego konta (ten sam znacznik, który dostają widziane wtedy elementy
    /// w <see cref="ElementEntity.LastSeenUtc"/>). Pozwala poznać, że element zniknął z listy (np. po edycji w Librusie).
    /// </summary>
    public DateTime LastFetchUtc { get; set; }
}

/// <summary>Rodzaj dziennego zestawienia nadchodzących zdarzeń.</summary>
public enum DigestKind
{
    /// <summary>Poranne (6:30, dni nauki): zaczyna się od zdarzeń na dziś.</summary>
    Morning,

    /// <summary>Popołudniowe (15:00): zaczyna się od zdarzeń na jutro lub pierwszy dzień nauki po wolnym.</summary>
    Afternoon
}

/// <summary>Zestawienie wysłane danego dnia danej grupie; chroni przed ponowną wysyłką po restarcie aplikacji.</summary>
public sealed class DigestLogEntity
{
    public int Id { get; set; }
    public DateOnly Day { get; set; }
    public DigestKind Kind { get; set; }
    public RecipientGroup Group { get; set; }
    public DateTime SentUtc { get; set; }
    public int EventCount { get; set; }
}
