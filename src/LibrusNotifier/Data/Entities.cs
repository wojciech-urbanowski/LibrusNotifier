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

    public List<NotificationEntity> Notifications { get; set; } = [];

    public bool IsVisibleTo(AccountKind account) => account == AccountKind.Parent ? VisibleToParent : VisibleToStudent;

    public void MarkVisibleTo(AccountKind account)
    {
        if (account == AccountKind.Parent) VisibleToParent = true;
        else VisibleToStudent = true;
    }
}

/// <summary>Powiadomienie elementu dla jednej grupy odbiorców; najwyżej jedno na parę (element, grupa).</summary>
public sealed class NotificationEntity
{
    public int Id { get; set; }
    public int ElementId { get; set; }
    public ElementEntity Element { get; set; } = null!;
    public RecipientGroup Group { get; set; }
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
}
