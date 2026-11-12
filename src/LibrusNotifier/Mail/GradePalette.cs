namespace LibrusNotifier.Mail;

/// <summary>
/// Pastelowe kolory pola z oceną. Tło i tekst są ustawiane razem (ciemny tekst na jasnym tle), więc pole jest czytelne
/// zarówno w jasnym, jak i w ciemnym motywie klienta poczty (tak samo jak kolory rodzajów wydarzeń w zestawieniach dziennych).
/// Ocena z plusem lub minusem (np. „4+”, „3-”) ma kolor swojej cyfry; osobne kolory mają tylko same znaki „+” i „-”.
/// Inne oznaczenia (np. „nb”, „np”, „zw”) i oceny opisowe są szare. Puste pole nie ma koloru.
/// </summary>
public static class GradePalette
{
    public const string TextColor = "#1f1f1f";

    /// <summary>Szary dla oznaczeń spoza skali i ocen opisowych (ten sam co „Inne” w zestawieniach dziennych).</summary>
    public const string OtherBackground = "#e2e2e2";

    /// <summary>Klasy kolorów w kolejności od najlepszej do najgorszej oceny.</summary>
    public static IReadOnlyList<string> Classes { get; } = ["6", "5", "+", "4", "3", "2", "-", "1"];

    /// <summary>
    /// Kolor tła oceny. Oznaczenie spoza skali (np. „nb”) dostaje szary, a <c>null</c> jest tylko dla pustego pola.
    /// </summary>
    public static string? Background(string? mark) => Classify(mark) switch
    {
        "6" => "#57cf7f", // zielony, intensywny
        "5" => "#8fe0a1", // zielony
        "+" => "#8fdcc9", // zielony w odcieniu miętowym, jak piątka
        "4" => "#cdeccf", // zielony, słaby
        "3" => "#fff0a8", // żółty
        "2" => "#ffc78a", // pomarańczowy
        "-" => "#f9a986", // między pomarańczowym a czerwonym
        "1" => "#f28b82", // czerwony
        _ => string.IsNullOrWhiteSpace(mark) ? null : OtherBackground
    };

    /// <summary>Opis koloru do dokumentacji i testów.</summary>
    public static string ColorName(string @class) => @class switch
    {
        "6" => "zielony (intensywny)",
        "5" => "zielony",
        "+" => "zielony (miętowy)",
        "4" => "zielony (słaby)",
        "3" => "żółty",
        "2" => "pomarańczowy",
        "-" => "łososiowy (między pomarańczowym a czerwonym)",
        "1" => "czerwony",
        _ => ""
    };

    /// <summary>
    /// Styl komórki z oceną (tło, ciemny tekst, pogrubienie) albo pusty tekst dla pustego pola.
    /// Ocena opisowa (<paramref name="descriptive"/>) jest zawsze szara, bez względu na treść.
    /// </summary>
    public static string CellStyle(string? mark, bool descriptive = false) =>
        (descriptive ? (string.IsNullOrWhiteSpace(mark) ? null : OtherBackground) : Background(mark)) is { } background
            ? $"background:{background};color:{TextColor};font-weight:600;"
            : "";

    /// <summary>Klasa koloru: „1”–„6”, „+” lub „-”; <c>null</c> dla pozostałych oznaczeń.</summary>
    public static string? Classify(string? mark)
    {
        var text = mark?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length == 1 && IsPlus(text[0])) return "+";
        if (text.Length == 1 && IsMinus(text[0])) return "-";
        if (text[0] is < '1' or > '6') return null;
        return text.Length == 1 || (text.Length == 2 && (IsPlus(text[1]) || IsMinus(text[1]))) ? text[..1] : null;
    }

    private static bool IsPlus(char c) => c == '+';

    // Librus zapisuje minus zwykłym myślnikiem, ale tolerujemy też minus matematyczny i półpauzę.
    private static bool IsMinus(char c) => c is '-' or '−' or '–' or '‐';
}
