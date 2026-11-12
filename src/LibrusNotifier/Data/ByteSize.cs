using System.Globalization;

namespace LibrusNotifier.Data;

public static class ByteSize
{
    /// <summary>Rozmiar w czytelnej jednostce (B, KB, MB), zawsze z kropką dziesiętną, niezależnie od ustawień systemu.</summary>
    public static string Format(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.##} MB")
    };
}
