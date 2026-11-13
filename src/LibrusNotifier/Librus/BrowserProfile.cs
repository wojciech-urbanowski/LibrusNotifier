using System.Net.Http.Headers;

namespace LibrusNotifier.Librus;

/// <summary>
/// Spójny zestaw nagłówków jednej przeglądarki, którym aplikacja przedstawia się serwerom: User-Agent, Accept-Language
/// i (dla przeglądarek opartych na Chromium) nagłówki Client Hints. Profil jest losowany raz na start programu
/// (<see cref="BrowserCatalog.Pick"/>) i nie zmienia się do końca działania, tak jak przeglądarka w trakcie sesji.
/// </summary>
/// <param name="Family">Rodzina przeglądarki, np. "Chrome".</param>
/// <param name="Version">Wersja pokazywana w logu i w User-Agencie, np. "157" albo "27.2" (Safari).</param>
/// <param name="Platform">System operacyjny, np. "Windows".</param>
public sealed record BrowserProfile(string Family, string Version, string Platform, string UserAgent, string AcceptLanguage,
    IReadOnlyList<KeyValuePair<string, string>> ClientHints)
{
    /// <summary>Krótki opis do logu, np. "Chrome 157 (Windows)".</summary>
    public string Describe() => $"{Family} {Version} ({Platform})";

    /// <summary>
    /// Ustawia nagłówki profilu i nadpisuje te, które już są. Nadpisanie jest konieczne, bo biblioteka klienta Librusa
    /// dodaje własny User-Agent do nagłówków domyślnych, zanim żądanie trafi do <see cref="LibrusHttpHandler"/>.
    /// </summary>
    public void Apply(HttpRequestHeaders headers)
    {
        headers.UserAgent.Clear();
        headers.UserAgent.ParseAdd(UserAgent);
        Set(headers, "Accept-Language", AcceptLanguage);
        foreach (var (name, value) in ClientHints) Set(headers, name, value);
    }

    private static void Set(HttpRequestHeaders headers, string name, string value)
    {
        headers.Remove(name);
        headers.TryAddWithoutValidation(name, value);
    }
}
