namespace LibrusNotifier.Processing;

/// <summary>
/// Porównanie tekstów w polskiej kolejności alfabetycznej (a, ą, b, c, ć, … ł … ń, o, ó … ś … ź, ż), bez rozróżniania wielkości liter.
/// Aplikacja działa z <c>InvariantGlobalization</c> (bez ICU), więc porównanie kulturowe nie zna polskiego alfabetu i ustawiłoby
/// „Ś” czy „Ż” za „Z”. Znaki spoza alfabetu (spacja, cyfry, interpunkcja) są przed literami, według kodu znaku.
/// </summary>
public sealed class PolishComparer : IComparer<string>
{
    private const string Alphabet = "aąbcćdeęfghijklłmnńoópqrsśtuvwxyzźż";

    // Litery dostają klucze większe od każdego znaku spoza alfabetu (kod znaku jest mniejszy niż 0x10000).
    private const int LetterBase = 0x10000;

    public static PolishComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var common = Math.Min(x.Length, y.Length);
        for (var i = 0; i < common; i++)
        {
            var difference = Key(x[i]).CompareTo(Key(y[i]));
            if (difference != 0) return difference;
        }

        var byLength = x.Length.CompareTo(y.Length);
        // Teksty różniące się tylko wielkością liter mają stałą, deterministyczną kolejność.
        return byLength != 0 ? byLength : string.CompareOrdinal(x, y);
    }

    private static int Key(char c)
    {
        var lower = char.ToLowerInvariant(c);
        var index = Alphabet.IndexOf(lower);
        return index >= 0 ? LetterBase + index : lower;
    }
}
