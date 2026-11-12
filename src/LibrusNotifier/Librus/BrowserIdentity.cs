using System.Net.Http.Headers;

namespace LibrusNotifier.Librus;

/// <summary>
/// Nagłówki, którymi aplikacja przedstawia się serwerom: zwykły Firefox na Windows (wersja z maszyny autora).
/// Wersję warto czasem podnieść, żeby User-Agent nie wyglądał na przestarzały.
/// </summary>
public static class BrowserIdentity
{
    public const string FirefoxVersion = "160.0";

    public const string UserAgent = $"Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:{FirefoxVersion}) Gecko/20100101 Firefox/{FirefoxVersion}";

    public const string AcceptLanguage = "pl,en-US;q=0.7,en;q=0.3";

    /// <summary>Dodaje User-Agent i Accept-Language, o ile żądanie nie ma już własnych.</summary>
    public static void Apply(HttpRequestHeaders headers)
    {
        if (headers.UserAgent.Count == 0) headers.UserAgent.ParseAdd(UserAgent);
        if (!headers.Contains("Accept-Language")) headers.TryAddWithoutValidation("Accept-Language", AcceptLanguage);
    }
}
