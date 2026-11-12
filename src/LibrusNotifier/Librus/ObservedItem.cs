using System.Security.Cryptography;
using System.Text;

namespace LibrusNotifier.Librus;

/// <summary>Pole elementu: etykieta (po polsku, używana w treści maila) i wartość.</summary>
public sealed record Field(string Label, string Value);

/// <summary>
/// Element odczytany z listy Librusa.
/// </summary>
/// <param name="Type">Typ elementu (<see cref="ElementTypes"/>).</param>
/// <param name="KeyParts">Pola kluczowe: treść niezależna od konta i identyfikatorów Librusa, bez numeru semestru.</param>
/// <param name="Fields">Dane elementu zapisywane w JSON i używane w mailu.</param>
/// <param name="DetailRef">Odnośnik do szczegółów (zależny od konta, nie trafia do klucza ani bazy).</param>
public sealed record ObservedItem(string Type, IReadOnlyList<string> KeyParts, IReadOnlyList<Field> Fields, string? DetailRef = null);

public sealed record KeyedItem(ObservedItem Item, string Key);

/// <summary>Wyliczanie kluczy elementów: skrót SHA-256 z typu i znormalizowanych pól kluczowych.</summary>
public static class ElementKeys
{
    private const char Separator = '\u001f';

    /// <summary>
    /// Wylicza klucze elementów jednej listy. Elementy o identycznej treści (np. dwa plusy z tego samego dnia)
    /// dostają kolejne numery wystąpienia, więc każdy ma osobny klucz, taki sam na obu kontach.
    /// </summary>
    public static IReadOnlyList<KeyedItem> Assign(IEnumerable<ObservedItem> items)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<KeyedItem>();
        foreach (var item in items)
        {
            var baseText = item.Type + Separator + string.Join(Separator, item.KeyParts.Select(Normalize));
            var occurrence = occurrences[baseText] = occurrences.GetValueOrDefault(baseText) + 1;
            var text = occurrence == 1 ? baseText : baseText + Separator + "#" + occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture);
            result.Add(new KeyedItem(item, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))));
        }
        return result;
    }

    /// <summary>Usuwa różnice w białych znakach (w tym spacje nierozdzielające), które nie zmieniają treści.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || c == ' ')
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            builder.Append(c);
        }
        return builder.ToString();
    }
}
