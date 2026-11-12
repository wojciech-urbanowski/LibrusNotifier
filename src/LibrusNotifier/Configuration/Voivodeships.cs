using System.Text;

namespace LibrusNotifier.Configuration;

/// <summary>Województwa i ich kody w OpenHolidays API (kody API różnią się od ISO 3166-2 dla części województw).</summary>
public static class Voivodeships
{
    public static readonly IReadOnlyList<Voivodeship> All =
    [
        new("dolnośląskie", "PL-DS"),
        new("kujawsko-pomorskie", "PL-KP"),
        new("lubelskie", "PL-LB"),
        new("lubuskie", "PL-LU"),
        new("łódzkie", "PL-LD"),
        new("małopolskie", "PL-MA"),
        new("mazowieckie", "PL-MZ"),
        new("opolskie", "PL-OP"),
        new("podkarpackie", "PL-PD"),
        new("podlaskie", "PL-PK"),
        new("pomorskie", "PL-PM"),
        new("śląskie", "PL-SK"),
        new("świętokrzyskie", "PL-SL"),
        new("warmińsko-mazurskie", "PL-WN"),
        new("wielkopolskie", "PL-WP"),
        new("zachodniopomorskie", "PL-ZP")
    ];

    /// <summary>Szuka województwa po nazwie, bez rozróżniania wielkości liter i z polskimi znakami lub bez nich.</summary>
    public static Voivodeship? Find(string name)
    {
        var normalized = Normalize(name);
        return All.FirstOrDefault(v => Normalize(v.Name) == normalized);
    }

    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Trim().ToLowerInvariant())
        {
            builder.Append(c switch
            {
                'ą' => 'a', 'ć' => 'c', 'ę' => 'e', 'ł' => 'l', 'ń' => 'n', 'ó' => 'o', 'ś' => 's', 'ź' => 'z', 'ż' => 'z',
                _ => c
            });
        }
        return builder.ToString();
    }
}
