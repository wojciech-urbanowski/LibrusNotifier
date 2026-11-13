using System.Text.RegularExpressions;

namespace LibrusNotifier.Mail;

/// <summary>
/// Rozpoznaje adresy WWW w treści z Librusa (<c>https://...</c>, <c>http://...</c> i <c>www....</c>), żeby w mailu HTML były klikalne.
/// Librus przysyła treść jako zwykły tekst, więc adres kończy się na pierwszej spacji; znaki interpunkcji, które autor dopisał
/// zaraz za adresem (kropka, przecinek, zamykający nawias), nie należą do linku. Linkami są wyłącznie adresy http i https.
/// </summary>
internal static partial class TextLinks
{
    /// <summary>Adres w tekście: miejsce, długość i docelowy adres (z <c>https://</c> dla zapisu zaczynającego się od <c>www.</c>).</summary>
    internal readonly record struct Link(int Index, int Length, string Href);

    private const string WwwPrefix = "www.";

    // Znaki, których autor tekstu nie ma na myśli jako końca adresu, gdy stoją na jego końcu: „zobacz https://a.pl/b.”, „(https://a.pl/b)”.
    private const string TrailingPunctuation = ".,;:!?'\"…";

    // Przed adresem nie może stać litera ani cyfra (np. „xhttps://”) ani „@” (adres e-mail typu a@www.b.pl); nawiasy ostre „<https://...>” kończą adres.
    [GeneratedRegex(@"(?<![\p{L}\p{N}@/.])(?:https?://|www\.)[^\s<>""]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    public static IReadOnlyList<Link> Find(string text)
    {
        var links = new List<Link>();
        foreach (Match match in UrlPattern().Matches(text))
        {
            var length = TrimmedLength(match.Value);
            var url = match.Value[..length];
            var isWww = url.StartsWith(WwwPrefix, StringComparison.OrdinalIgnoreCase);
            var prefixLength = isWww ? WwwPrefix.Length : url.IndexOf("//", StringComparison.Ordinal) + 2;
            if (length <= prefixLength) continue; // samo „https://” albo „www.” po obcięciu interpunkcji to nie adres
            links.Add(new Link(match.Index, length, isWww ? "https://" + url : url));
        }
        return links;
    }

    /// <summary>Długość adresu bez końcowej interpunkcji; zamykający nawias zostaje, gdy w adresie jest jego otwierający (np. adresy z Wikipedii).</summary>
    private static int TrimmedLength(string url)
    {
        var end = url.Length;
        while (end > 0)
        {
            var last = url[end - 1];
            if (TrailingPunctuation.Contains(last))
            {
                end--;
                continue;
            }
            var open = last switch { ')' => '(', ']' => '[', '}' => '{', _ => '\0' };
            if (open != '\0' && url[..end].Count(c => c == last) > url[..end].Count(c => c == open))
            {
                end--;
                continue;
            }
            break;
        }
        return end;
    }
}
