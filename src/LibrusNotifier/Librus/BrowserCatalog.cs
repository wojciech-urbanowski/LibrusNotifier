using System.Globalization;

namespace LibrusNotifier.Librus;

/// <summary>
/// Pula przeglądarek, z której przy starcie programu losowany jest <see cref="BrowserProfile"/>: rodzina (z wagą
/// odpowiadającą orientacyjnej popularności), system operacyjny i wersja. Pula zawiera tylko świeże wersje, bo przeglądarki
/// aktualizują się same i stara wersja wyróżniałaby się bardziej niż jakakolwiek stała.
/// <para>
/// UTRZYMANIE: wersje trzeba odświeżać. Przy każdej zmianie w programie sprawdź <see cref="VerifiedOn"/>; jeśli minęło
/// więcej niż <see cref="RefreshAfterDays"/> dni, uruchom <c>scripts/odswiez-wersje-przegladarek.sh</c>, popraw listy wersji
/// w <see cref="Families"/> i ustaw <see cref="VerifiedOn"/> na dzisiaj. Gdy minie <see cref="StaleAfterDays"/> dni,
/// start programu zapisuje ostrzeżenie w logu (trafia też do raportu dla administratora).
/// </para>
/// <para>
/// Zasady doboru: Chrome i Edge to ostatnie 3-4 wersje major (wersja Edge = wersja Chromium), Firefox to 3 ostatnie wersje
/// major plus bieżące ESR, Safari to wersje dostępne na macOS. Tylko wersje desktopowe: Librus mógłby dla przeglądarki
/// mobilnej podać inny układ strony niż ten, który zna parser. W User-Agencie Chromium i Safari wersja jest "zamrożona"
/// (np. Chrome/157.0.0.0, macOS zawsze 10_15_7), więc wymiarem wersji jest sam numer major (Safari: major.minor).
/// </para>
/// </summary>
public static class BrowserCatalog
{
    /// <summary>Dzień, w którym listy wersji porównano z oficjalnymi źródłami (Mozilla, Google, Microsoft, Apple).</summary>
    public static readonly DateOnly VerifiedOn = new(2026, 11, 13);

    /// <summary>Po tylu dniach od weryfikacji lista powinna zostać odświeżona przy najbliższej pracy nad programem.</summary>
    public const int RefreshAfterDays = 60;

    /// <summary>Po tylu dniach od weryfikacji start programu ostrzega w logu, że lista jest przestarzała.</summary>
    public const int StaleAfterDays = 120;

    private enum Platform { Windows, MacOs, Linux }

    private readonly record struct Weighted<T>(T Value, int Weight);

    private sealed record Family(string Name, int Weight, Weighted<Platform>[] Platforms, Weighted<string>[] Versions,
        Func<Platform, string, BrowserProfile> Build);

    // Stan na VerifiedOn: Chrome stable 157, Edge stable 157, Firefox 160 (ESR 153), Safari 27.2 (macOS 27.2, 26 i Sequoia)
    // oraz 26.6 (Sonoma). Wagi: najnowsza wersja najczęstsza, starsze coraz rzadsze.
    private static readonly Family[] Families =
    [
        new("Chrome", 55,
            [new(Platform.Windows, 70), new(Platform.MacOs, 18), new(Platform.Linux, 12)],
            [new("157", 4), new("156", 3), new("155", 2), new("154", 1)],
            (platform, version) => Chromium("Chrome", version, platform, "Google Chrome", "")),
        new("Edge", 15,
            [new(Platform.Windows, 100)],
            [new("157", 4), new("156", 3), new("155", 2)],
            (platform, version) => Chromium("Edge", version, platform, "Microsoft Edge", $" Edg/{version}.0.0.0")),
        new("Firefox", 20,
            [new(Platform.Windows, 65), new(Platform.MacOs, 15), new(Platform.Linux, 20)],
            [new("160", 4), new("159", 3), new("158", 2), new("153", 1)], // 153 to bieżące ESR
            Firefox),
        new("Safari", 10,
            [new(Platform.MacOs, 100)],
            [new("27.2", 4), new("27.0", 2), new("26.6", 1)],
            Safari)
    ];

    /// <summary>Losuje profil: rodzina, potem system, potem wersja (każdy krok z wagami).</summary>
    public static BrowserProfile Pick(Random random)
    {
        var family = PickWeighted(Families, f => f.Weight, random);
        var platform = PickWeighted(family.Platforms, p => p.Weight, random).Value;
        var version = PickWeighted(family.Versions, v => v.Weight, random).Value;
        return family.Build(platform, version);
    }

    /// <summary>Wszystkie możliwe profile (do testów i diagnostyki).</summary>
    public static IEnumerable<BrowserProfile> All() =>
        Families.SelectMany(f => f.Platforms.SelectMany(p => f.Versions.Select(v => f.Build(p.Value, v.Value))));

    /// <summary>Czy lista wersji jest na tyle stara, że start programu ma o tym ostrzec.</summary>
    public static bool IsStale(DateOnly today) => today.DayNumber - VerifiedOn.DayNumber > StaleAfterDays;

    /// <summary>
    /// Wartość nagłówka <c>sec-ch-ua</c> tak, jak składa ją Chromium: trzy marki (zmyślona "Not?A_Brand", "Chromium" i marka
    /// przeglądarki), których znaki, wersja zmyślonej marki i kolejność wynikają z numeru major.
    /// </summary>
    public static string ChromiumBrands(int major, string brand)
    {
        string[] separators = [" ", "(", ":", "-", ".", "/", ")", ";", "=", "?", "_"];
        string[] greaseVersions = ["8", "99", "24"];
        int[][] orders = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        var v = major.ToString(CultureInfo.InvariantCulture);
        var order = orders[major % orders.Length];
        var brands = new string[3];
        brands[order[0]] = $"\"Not{separators[major % separators.Length]}A{separators[(major + 1) % separators.Length]}Brand\";v=\"{greaseVersions[major % greaseVersions.Length]}\"";
        brands[order[1]] = $"\"Chromium\";v=\"{v}\"";
        brands[order[2]] = $"\"{brand}\";v=\"{v}\"";
        return string.Join(", ", brands);
    }

    private static T PickWeighted<T>(IReadOnlyList<T> items, Func<T, int> weight, Random random)
    {
        var roll = random.NextDouble() * items.Sum(weight);
        foreach (var item in items)
        {
            if (roll < weight(item)) return item;
            roll -= weight(item);
        }
        return items[^1];
    }

    private static string PlatformName(Platform platform) => platform switch
    {
        Platform.Windows => "Windows",
        Platform.MacOs => "macOS",
        _ => "Linux"
    };

    private static BrowserProfile Firefox(Platform platform, string version)
    {
        var system = platform switch
        {
            Platform.Windows => "Windows NT 10.0; Win64; x64",
            Platform.MacOs => "Macintosh; Intel Mac OS X 10.15",
            _ => "X11; Linux x86_64"
        };
        return new BrowserProfile("Firefox", version, PlatformName(platform),
            $"Mozilla/5.0 ({system}; rv:{version}.0) Gecko/20100101 Firefox/{version}.0", "pl,en-US;q=0.7,en;q=0.3", []);
    }

    private static BrowserProfile Chromium(string family, string version, Platform platform, string brand, string userAgentSuffix)
    {
        var system = platform switch
        {
            Platform.Windows => "Windows NT 10.0; Win64; x64",
            Platform.MacOs => "Macintosh; Intel Mac OS X 10_15_7",
            _ => "X11; Linux x86_64"
        };
        var platformName = PlatformName(platform);
        return new BrowserProfile(family, version, platformName,
            $"Mozilla/5.0 ({system}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{version}.0.0.0 Safari/537.36{userAgentSuffix}",
            "pl-PL,pl;q=0.9,en-US;q=0.8,en;q=0.7",
            [
                new("sec-ch-ua", ChromiumBrands(int.Parse(version, CultureInfo.InvariantCulture), brand)),
                new("sec-ch-ua-mobile", "?0"),
                new("sec-ch-ua-platform", $"\"{platformName}\"")
            ]);
    }

    private static BrowserProfile Safari(Platform platform, string version) =>
        new("Safari", version, PlatformName(platform),
            $"Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/{version} Safari/605.1.15",
            "pl-PL,pl;q=0.9", []);
}
