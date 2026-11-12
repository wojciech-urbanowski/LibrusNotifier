using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;

namespace LibrusNotifier.Configuration;

/// <summary>Błąd konfiguracji. Komunikat wskazuje plik oraz sekcję lub wiersz i nigdy nie zawiera wartości haseł.</summary>
public sealed class ConfigException(string message) : Exception(message);

/// <summary>
/// Parser pliku <c>librus_config.txt</c> (UTF-8, sekcje INI). Puste linie i linie zaczynające się od # lub ; są pomijane.
/// Para klucz–wartość jest dzielona na pierwszym '='; wartości są dosłowne (bez zmiennych i sekwencji ucieczki),
/// spacje na brzegach klucza i wartości są usuwane. W sekcjach z listą adresów wiersz to cały adres.
/// </summary>
public static partial class ConfigParser
{
    public static readonly TimeSpan DefaultIdleLimit = TimeSpan.FromMinutes(9);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private enum SectionKind
    {
        Values,
        List
    }

    private sealed record SectionSpec(string Name, SectionKind Kind, string[] Keys, bool Required);

    private static readonly SectionSpec[] Specs =
    [
        new("rodzic", SectionKind.Values, ["login", "haslo"], true),
        new("uczen", SectionKind.Values, ["login", "haslo"], true),
        new("sesja", SectionKind.Values, ["limit_bezczynnosci_minuty"], false),
        new("gmail", SectionKind.Values, ["email", "app_password"], true),
        new("odbiorcy_rodzice", SectionKind.List, [], true),
        new("odbiorcy_uczen", SectionKind.List, [], true),
        new("odbiorcy_log", SectionKind.List, [], true),
        new("kalendarz", SectionKind.Values, ["wojewodztwo"], true)
    ];

    private sealed class Section(SectionSpec spec, int headerLine)
    {
        public SectionSpec Spec { get; } = spec;
        public int HeaderLine { get; } = headerLine;
        public Dictionary<string, (string Value, int Line)> Values { get; } = new(StringComparer.Ordinal);
        public List<string> Addresses { get; } = [];
    }

    /// <exception cref="ConfigException">Brak pliku, błąd odczytu lub niepoprawna zawartość.</exception>
    public static AppConfig Load(string path)
    {
        if (!File.Exists(path)) throw new ConfigException($"Błąd konfiguracji ({path}): brak pliku konfiguracji.");
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException($"Błąd konfiguracji ({path}): nie można odczytać pliku: {e.Message}");
        }
        string text;
        try
        {
            var preamble = Encoding.UTF8.Preamble;
            var offset = bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
            text = StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            throw new ConfigException($"Błąd konfiguracji ({path}): plik nie jest zapisany w kodowaniu UTF-8.");
        }
        return Parse(text, path);
    }

    /// <exception cref="ConfigException">Niepoprawna zawartość.</exception>
    public static AppConfig Parse(string text, string path)
    {
        var sections = new Dictionary<string, Section>(StringComparer.Ordinal);
        Section? current = null;
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var number = index + 1;
            var line = lines[index].TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;

            if (line[0] == '[')
            {
                if (line[^1] != ']') throw Error(path, null, number, "niepoprawny nagłówek sekcji (brak znaku ']')");
                var name = line[1..^1].Trim().ToLowerInvariant();
                var spec = Specs.FirstOrDefault(s => s.Name == name)
                    ?? throw Error(path, null, number, IsSafeName(name) ? $"nieznana sekcja [{name}]" : "nieznana sekcja");
                if (sections.ContainsKey(name)) throw Error(path, name, number, "sekcja występuje w pliku więcej niż raz");
                current = new Section(spec, number);
                sections[name] = current;
                continue;
            }

            if (current is null) throw Error(path, null, number, "wpis przed pierwszym nagłówkiem sekcji");

            if (current.Spec.Kind == SectionKind.List)
            {
                if (!IsValidAddress(line)) throw Error(path, current.Spec.Name, number, "niepoprawny adres e-mail");
                if (!current.Addresses.Contains(line, StringComparer.OrdinalIgnoreCase)) current.Addresses.Add(line);
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0) throw Error(path, current.Spec.Name, number, "oczekiwano wpisu w postaci klucz=wartość");
            var key = line[..separator].Trim().ToLowerInvariant();
            var value = line[(separator + 1)..].Trim();
            if (key.Length == 0) throw Error(path, current.Spec.Name, number, "brak nazwy klucza przed znakiem '='");
            if (!current.Spec.Keys.Contains(key))
                throw Error(path, current.Spec.Name, number, IsSafeName(key) ? $"nieznany klucz '{key}'" : "nieznany klucz");
            if (current.Values.ContainsKey(key)) throw Error(path, current.Spec.Name, number, $"klucz '{key}' występuje więcej niż raz");
            current.Values[key] = (value, number);
        }

        foreach (var spec in Specs.Where(s => s.Required))
            if (!sections.ContainsKey(spec.Name))
                throw Error(path, null, null, $"brak wymaganej sekcji [{spec.Name}]");

        string Required(string sectionName, string key)
        {
            var section = sections[sectionName];
            if (!section.Values.TryGetValue(key, out var entry))
                throw Error(path, sectionName, section.HeaderLine, $"brak wymaganego klucza '{key}'");
            if (entry.Value.Length == 0) throw Error(path, sectionName, entry.Line, $"pusta wartość klucza '{key}'");
            return entry.Value;
        }

        IReadOnlyList<string> Addresses(string sectionName, bool atLeastOne)
        {
            var section = sections[sectionName];
            if (atLeastOne && section.Addresses.Count == 0)
                throw Error(path, sectionName, section.HeaderLine, "lista musi zawierać co najmniej jeden adres");
            return section.Addresses.ToArray();
        }

        var parent = new AccountCredentials(Required("rodzic", "login"), Required("rodzic", "haslo"));
        var student = new AccountCredentials(Required("uczen", "login"), Required("uczen", "haslo"));

        var gmailEmail = Required("gmail", "email");
        if (!IsValidAddress(gmailEmail)) throw Error(path, "gmail", sections["gmail"].Values["email"].Line, "niepoprawny adres e-mail w kluczu 'email'");
        var gmail = new GmailSettings(gmailEmail, Required("gmail", "app_password"));

        var voivodeshipName = Required("kalendarz", "wojewodztwo");
        var voivodeship = Voivodeships.Find(voivodeshipName)
            ?? throw Error(path, "kalendarz", sections["kalendarz"].Values["wojewodztwo"].Line,
                "nieznane województwo (przykłady poprawnych wartości: dolnoslaskie, mazowieckie, slaskie)");

        var idleLimit = DefaultIdleLimit;
        if (sections.ContainsKey("sesja"))
        {
            var raw = Required("sesja", "limit_bezczynnosci_minuty");
            idleLimit = ParseMinutes(raw) ?? throw Error(path, "sesja", sections["sesja"].Values["limit_bezczynnosci_minuty"].Line,
                "'limit_bezczynnosci_minuty' musi być dodatnią liczbą minut (ułamek z kropką, np. 9 lub 7.5) w zakresie TimeSpan");
        }

        return new AppConfig
        {
            Parent = parent,
            Student = student,
            IdleLimit = idleLimit,
            Gmail = gmail,
            ParentRecipients = Addresses("odbiorcy_rodzice", atLeastOne: true),
            StudentRecipients = Addresses("odbiorcy_uczen", atLeastOne: false),
            AdminRecipients = Addresses("odbiorcy_log", atLeastOne: true),
            Voivodeship = voivodeship
        };
    }

    /// <summary>Adres w postaci <c>lokalna@domena.tld</c>, bez nazwy wyświetlanej i białych znaków.</summary>
    public static bool IsValidAddress(string text)
    {
        if (text.Length is 0 or > 254 || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return false;
        if (!MailAddress.TryCreate(text, out var address) || address.Address != text || address.DisplayName.Length != 0) return false;
        if (!MimeKit.MailboxAddress.TryParse(text, out var mailbox) || mailbox.Address != text) return false;
        var at = text.LastIndexOf('@');
        var domain = text[(at + 1)..];
        return at > 0 && domain.Contains('.') && domain[0] != '.' && domain[^1] != '.';
    }

    private static TimeSpan? ParseMinutes(string text)
    {
        if (!MinutesPattern().IsMatch(text)) return null;
        if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var minutes) ||
            !double.IsFinite(minutes) || minutes <= 0)
            return null;
        try
        {
            var span = TimeSpan.FromMinutes(minutes);
            return span > TimeSpan.Zero ? span : null;
        }
        catch (Exception e) when (e is OverflowException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsSafeName(string name) => SafeNamePattern().IsMatch(name);

    private static ConfigException Error(string path, string? section, int? line, string detail)
    {
        var location = new List<string> { path };
        if (section is not null) location.Add($"sekcja [{section}]");
        if (line is not null) location.Add($"wiersz {line}");
        return new ConfigException($"Błąd konfiguracji ({string.Join(", ", location)}): {detail}.");
    }

    [GeneratedRegex(@"^[0-9]+(\.[0-9]+)?$")]
    private static partial Regex MinutesPattern();

    [GeneratedRegex("^[a-z0-9_]{1,40}$")]
    private static partial Regex SafeNamePattern();
}
