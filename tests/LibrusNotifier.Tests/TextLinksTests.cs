using LibrusNotifier.Mail;

namespace LibrusNotifier.Tests;

public sealed class TextLinksTests
{
    private static string[] Hrefs(string text) => [.. TextLinks.Find(text).Select(l => l.Href)];

    private static string[] Found(string text) => [.. TextLinks.Find(text).Select(l => text.Substring(l.Index, l.Length))];

    [Theory]
    [InlineData("https://liblink.pl/zNYWOlww7S", "https://liblink.pl/zNYWOlww7S")]
    [InlineData("http://example.com", "http://example.com")]
    [InlineData("HTTPS://EXAMPLE.COM/A", "HTTPS://EXAMPLE.COM/A")]
    [InlineData("zobacz https://example.com/a?b=1&c=2#d teraz", "https://example.com/a?b=1&c=2#d")]
    [InlineData("tak:\nhttps://example.com/a\nnastępna linia", "https://example.com/a")]
    public void FindsAddress(string text, string expected) => Assert.Equal([expected], Found(text));

    [Theory]
    [InlineData("Zobacz https://example.com/a.", "https://example.com/a")]
    [InlineData("Zobacz https://example.com/a, a potem", "https://example.com/a")]
    [InlineData("(https://example.com/a)", "https://example.com/a")]
    [InlineData("\"https://example.com/a\"", "https://example.com/a")]
    [InlineData("<https://example.com/a>", "https://example.com/a")]
    [InlineData("Czy to https://example.com/a?", "https://example.com/a")]
    [InlineData("https://example.com/a...", "https://example.com/a")]
    [InlineData("[https://example.com/a]", "https://example.com/a")]
    public void PunctuationAfterAddressIsNotPartOfTheLink(string text, string expected) => Assert.Equal([expected], Found(text));

    [Fact]
    public void ClosingParenthesisBelongingToTheAddressStays() =>
        Assert.Equal(["https://pl.wikipedia.org/wiki/Librus_(system)"], Found("Zobacz (https://pl.wikipedia.org/wiki/Librus_(system))."));

    [Fact]
    public void AddressWithoutSchemeStartingWithWwwGetsHttps() =>
        Assert.Equal(["https://www.szkola.pl/konkurs"], Hrefs("Strona: www.szkola.pl/konkurs."));

    [Fact]
    public void SeveralAddressesAreAllFoundWithTheirPositions()
    {
        const string text = "A https://a.pl i B http://b.pl/x.";

        var links = TextLinks.Find(text);

        Assert.Equal(2, links.Count);
        Assert.Equal(2, links[0].Index);
        Assert.Equal("https://a.pl".Length, links[0].Length);
        Assert.Equal("http://b.pl/x", text.Substring(links[1].Index, links[1].Length));
    }

    [Theory]
    [InlineData("brak adresu")]
    [InlineData("")]
    [InlineData("https://")]
    [InlineData("https://.")]
    [InlineData("www.")]
    [InlineData("xhttps://example.com")]
    [InlineData("ftp://example.com/plik")]
    [InlineData("javascript:alert(1)")]
    [InlineData("jan@www.example.com")]
    [InlineData("na stronie szkoły")]
    public void TextWithoutAWebAddressHasNoLinks(string text) => Assert.Empty(TextLinks.Find(text));
}
