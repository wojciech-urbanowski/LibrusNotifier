using LibrusNotifier.Librus;

namespace LibrusNotifier.Configuration;

/// <summary>Dane logowania konta Librus. <see cref="ToString"/> nie ujawnia hasła.</summary>
public sealed class AccountCredentials(string login, string password)
{
    public string Login { get; } = login;
    public string Password { get; } = password;

    public override string ToString() => $"{Login} (hasło ukryte)";
}

/// <summary>Konto Gmail nadawcy. <see cref="ToString"/> nie ujawnia hasła aplikacji.</summary>
public sealed class GmailSettings(string email, string appPassword)
{
    public string Email { get; } = email;
    public string AppPassword { get; } = appPassword;

    public override string ToString() => $"{Email} (hasło aplikacji ukryte)";
}

/// <param name="Name">Nazwa województwa.</param>
/// <param name="Code">Kod podziału administracyjnego w OpenHolidays API (np. PL-DS).</param>
public sealed record Voivodeship(string Name, string Code);

public sealed class AppConfig
{
    public required AccountCredentials Parent { get; init; }
    public required AccountCredentials Student { get; init; }
    public required TimeSpan IdleLimit { get; init; }
    public required GmailSettings Gmail { get; init; }
    public required IReadOnlyList<string> ParentRecipients { get; init; }
    public required IReadOnlyList<string> StudentRecipients { get; init; }
    public required IReadOnlyList<string> AdminRecipients { get; init; }
    public required Voivodeship Voivodeship { get; init; }

    public AccountCredentials CredentialsFor(AccountKind account) => account == AccountKind.Parent ? Parent : Student;

    public IEnumerable<string> Secrets() => [Parent.Password, Student.Password, Gmail.AppPassword];
}
