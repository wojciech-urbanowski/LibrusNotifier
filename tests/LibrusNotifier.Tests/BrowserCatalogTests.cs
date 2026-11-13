using LibrusNotifier.Librus;

namespace LibrusNotifier.Tests;

/// <summary>
/// Testy nie wpisują konkretnych numerów wersji (zmieniają się przy każdym odświeżeniu listy w <see cref="BrowserCatalog"/>);
/// sprawdzają spójność profili, wagi losowania i składanie nagłówków Client Hints.
/// </summary>
public sealed class BrowserCatalogTests
{
    [Fact]
    public void PickIsDeterministicForTheSameRandomSeed()
    {
        var random = new Random(11);
        var a = Enumerable.Range(0, 30).Select(_ => BrowserCatalog.Pick(random).UserAgent).ToList();
        random = new Random(11);
        var b = Enumerable.Range(0, 30).Select(_ => BrowserCatalog.Pick(random).UserAgent).ToList();

        Assert.Equal(a, b);
        Assert.True(a.Distinct().Count() > 1);
    }

    [Fact]
    public void ExtremeRandomValuesPickTheFirstAndLastEntryOfEveryLevel()
    {
        var first = BrowserCatalog.Pick(new FixedRandom(0.0));
        var last = BrowserCatalog.Pick(new FixedRandom(0.9999999));

        Assert.Equal(("Chrome", "Windows"), (first.Family, first.Platform));
        Assert.Equal(("Safari", "macOS"), (last.Family, last.Platform));
    }

    [Fact]
    public void EveryFamilyIsDrawnAndChromeIsTheMostCommon()
    {
        var random = new Random(1);
        var counts = Enumerable.Range(0, 4000).Select(_ => BrowserCatalog.Pick(random).Family)
            .GroupBy(f => f).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(["Chrome", "Edge", "Firefox", "Safari"], counts.Keys.Order());
        Assert.Equal("Chrome", counts.MaxBy(c => c.Value).Key);
    }

    [Fact]
    public void NewestVersionOfAFamilyIsDrawnMoreOftenThanTheOldest()
    {
        var random = new Random(2);
        var chrome = Enumerable.Range(0, 6000).Select(_ => BrowserCatalog.Pick(random)).Where(p => p.Family == "Chrome")
            .GroupBy(p => int.Parse(p.Version)).ToDictionary(g => g.Key, g => g.Count());

        Assert.True(chrome[chrome.Keys.Max()] > chrome[chrome.Keys.Min()]);
    }

    [Fact]
    public void EveryProfileIsAWellFormedDesktopHeaderSet()
    {
        var all = BrowserCatalog.All().ToList();

        Assert.Equal(all.Count, all.Select(p => p.UserAgent).Distinct().Count());
        foreach (var profile in all)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://school.test/");
            profile.Apply(request.Headers);

            Assert.Equal(profile.UserAgent, request.Headers.UserAgent.ToString());
            Assert.DoesNotContain("Mobile", profile.UserAgent);
            Assert.StartsWith("Mozilla/5.0 (", profile.UserAgent);
            Assert.Contains(profile.Platform switch { "Windows" => "Windows NT 10.0", "macOS" => "Macintosh", _ => "X11; Linux" }, profile.UserAgent);
            switch (profile.Family)
            {
                case "Firefox":
                    Assert.Contains($"rv:{profile.Version}.0) Gecko/20100101 Firefox/{profile.Version}.0", profile.UserAgent);
                    Assert.Empty(profile.ClientHints);
                    break;
                case "Safari":
                    Assert.EndsWith($"Version/{profile.Version} Safari/605.1.15", profile.UserAgent);
                    Assert.Empty(profile.ClientHints);
                    break;
                default:
                    Assert.Contains($"Chrome/{profile.Version}.0.0.0 Safari/537.36", profile.UserAgent);
                    Assert.Equal(profile.Family == "Edge", profile.UserAgent.EndsWith($" Edg/{profile.Version}.0.0.0"));
                    var brand = profile.Family == "Edge" ? "Microsoft Edge" : "Google Chrome";
                    Assert.Equal(BrowserCatalog.ChromiumBrands(int.Parse(profile.Version), brand), request.Headers.GetValues("sec-ch-ua").Single());
                    Assert.Equal("?0", request.Headers.GetValues("sec-ch-ua-mobile").Single());
                    Assert.Equal($"\"{profile.Platform}\"", request.Headers.GetValues("sec-ch-ua-platform").Single());
                    break;
            }
        }
    }

    [Fact]
    public void ApplyReplacesExistingHeadersInsteadOfAddingToThem()
    {
        var profile = TestBrowser.Profile;
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://school.test/");
        request.Headers.UserAgent.ParseAdd("Biblioteka/1.0");
        request.Headers.TryAddWithoutValidation("Accept-Language", "de");
        request.Headers.TryAddWithoutValidation("sec-ch-ua", "stare");

        profile.Apply(request.Headers);
        profile.Apply(request.Headers);

        Assert.Equal(profile.UserAgent, request.Headers.UserAgent.ToString());
        Assert.Equal(profile.AcceptLanguage, string.Join(",", request.Headers.GetValues("Accept-Language")).Replace(" ", ""));
        Assert.Single(request.Headers.GetValues("sec-ch-ua"));
    }

    [Theory]
    [InlineData(120, "Google Chrome", "\"Not_A Brand\";v=\"8\", \"Chromium\";v=\"120\", \"Google Chrome\";v=\"120\"")]
    [InlineData(124, "Google Chrome", "\"Chromium\";v=\"124\", \"Google Chrome\";v=\"124\", \"Not-A.Brand\";v=\"99\"")]
    [InlineData(131, "Google Chrome", "\"Google Chrome\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"")]
    [InlineData(131, "Microsoft Edge", "\"Microsoft Edge\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"")]
    public void ChromiumBrandsMatchHeadersSentByRealBrowsers(int major, string brand, string expected) =>
        Assert.Equal(expected, BrowserCatalog.ChromiumBrands(major, brand));

    [Fact]
    public void ListIsStaleOnlyAfterTheStaleLimit()
    {
        Assert.False(BrowserCatalog.IsStale(BrowserCatalog.VerifiedOn));
        Assert.False(BrowserCatalog.IsStale(BrowserCatalog.VerifiedOn.AddDays(BrowserCatalog.StaleAfterDays)));
        Assert.True(BrowserCatalog.IsStale(BrowserCatalog.VerifiedOn.AddDays(BrowserCatalog.StaleAfterDays + 1)));
    }
}
