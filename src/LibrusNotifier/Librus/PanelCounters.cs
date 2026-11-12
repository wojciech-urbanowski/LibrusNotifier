using LibrusApixNet;

namespace LibrusNotifier.Librus;

/// <summary>Liczniki panelu powiadomień (nowości od poprzedniego logowania danego konta).</summary>
public sealed class PanelCounters
{
    private static readonly Dictionary<string, ModuleKind> ByDestination = new(StringComparer.Ordinal)
    {
        ["/przegladaj_oceny/uczen"] = ModuleKind.Grades,
        ["/przegladaj_nb/uczen"] = ModuleKind.Attendance,
        ["/wiadomosci3"] = ModuleKind.Messages, // aktualny adres wiadomości w menu panelu
        ["/wiadomosci"] = ModuleKind.Messages,  // starszy układ
        ["/ogloszenia"] = ModuleKind.Announcements,
        ["/moje_zadania"] = ModuleKind.Homework,
        ["/terminarz"] = ModuleKind.Schedule
    };

    public PanelCounters(IReadOnlyDictionary<ModuleKind, int> counts) => Counts = counts;

    public IReadOnlyDictionary<ModuleKind, int> Counts { get; }

    public IEnumerable<ModuleKind> NonZero => Counts.Where(c => c.Value != 0).Select(c => c.Key);

    public static PanelCounters Empty { get; } = new(new Dictionary<ModuleKind, int>());

    public static PanelCounters From(IEnumerable<NotificationAmount> amounts)
    {
        var counts = new Dictionary<ModuleKind, int>();
        foreach (var amount in amounts)
            if (ByDestination.TryGetValue(amount.Destination, out var module))
                counts[module] = counts.GetValueOrDefault(module) + amount.Amount;
        return new PanelCounters(counts);
    }

    public static PanelCounters Of(params (ModuleKind Module, int Count)[] counts) =>
        new(counts.ToDictionary(c => c.Module, c => c.Count));

    public override string ToString() => Counts.Count == 0
        ? "brak liczników"
        : string.Join(", ", Counts.OrderBy(c => c.Key).Select(c => $"{c.Key.DisplayName()}={c.Value}"));
}
