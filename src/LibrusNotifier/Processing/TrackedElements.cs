using System.Globalization;
using LibrusNotifier.Data;
using LibrusNotifier.Librus;

namespace LibrusNotifier.Processing;

/// <summary>
/// Elementy z datą w przyszłości (terminarz, zmiany planu, zadania domowe): wspólne reguły dla zestawień dziennych i wykrywania
/// zmian terminu oraz usunięć (<see cref="ChangeDetector"/>).
/// </summary>
public static class TrackedElements
{
    /// <summary>Tyle dni wstecz sięga lista zadań domowych (pobierana od „dzisiaj − 7” do „dzisiaj”).</summary>
    public const int HomeworkListDays = 7;

    public static IReadOnlyList<string> Types { get; } = [ElementTypes.ScheduleEvent, ElementTypes.TimetableChange, ElementTypes.Homework];

    public static bool IsTracked(string type) => Types.Contains(type);

    public static ModuleKind? ModuleOf(string type) => type switch
    {
        ElementTypes.ScheduleEvent => ModuleKind.Schedule,
        ElementTypes.TimetableChange => ModuleKind.Timetable,
        ElementTypes.Homework => ModuleKind.Homework,
        _ => null
    };

    /// <summary>Etykieta pola z datą zdarzenia (dla zadania domowego – termin wykonania).</summary>
    public static string DateLabel(string type) => type == ElementTypes.Homework ? "Termin wykonania" : "Data";

    public static bool TryGetDate(string type, IReadOnlyDictionary<string, string> fields, out DateOnly date)
    {
        date = default;
        if (!fields.TryGetValue(DateLabel(type), out var text)) return false;
        text = text.Trim();
        return DateOnly.TryParseExact(text.Length >= 10 ? text[..10] : text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    public static IReadOnlyDictionary<string, string> Fields(ElementEntity element) =>
        FieldJson.Deserialize(element.DataJson)
            .GroupBy(f => f.Label, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);

    /// <summary>
    /// Element nie jest na liście: żadne z kont, na których był widoczny, nie wymienia go w swoim ostatnim pobraniu listy
    /// (<see cref="ElementEntity.LastSeenUtc"/> jest starsze niż najstarsze z tych pobrań). Wymaga więc, by każde takie konto
    /// pobrało listę od ostatniego zobaczenia elementu: konto, które nie pobiera list (np. błąd logowania), wstrzymuje wniosek,
    /// a lista widoczna tylko na jednym koncie nie powoduje fałszywego alarmu na drugim.
    /// </summary>
    public static bool IsGone(ElementEntity element, IReadOnlyList<BaselineEntity> baselines)
    {
        var module = ModuleOf(element.Type);
        if (module is null) return false;
        var fetches = baselines.Where(b => b.Module == module && element.IsVisibleTo(b.Account)).Select(b => b.LastFetchUtc).ToList();
        return fetches.Count > 0 && element.LastSeenUtc < fetches.Min();
    }

    /// <summary>
    /// Czy lista na pewno obejmuje ten element, gdyby nadal istniał w Librusie. Terminarz (bieżący i następny miesiąc) i plan lekcji
    /// (bieżący tydzień) obejmują każdy element z dzisiejszą lub późniejszą datą, który kiedykolwiek się na nich pojawił. Lista zadań
    /// domowych sięga tylko 7 dni wstecz, więc obejmuje zadania udostępnione w tym czasie.
    /// </summary>
    public static bool ListCovers(string type, IReadOnlyDictionary<string, string> fields, DateOnly today)
    {
        if (type != ElementTypes.Homework) return true;
        if (!fields.TryGetValue("Data udostępnienia", out var text) || text.Length < 10) return false;
        return DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var assigned)
            && assigned >= today.AddDays(-HomeworkListDays);
    }

    /// <summary>
    /// Klucz do łączenia zniknięcia z nowym wpisem w „zmianę terminu”: ta sama treść, inna data. Dla terminarza przedmiot, tytuł i opis
    /// (lekcja i godzina mogą się zmienić razem z datą); dla zadania domowego wszystko poza terminem wykonania. <c>null</c>, gdy łączenie
    /// nie ma sensu: zmiany planu, zastępstwa i odwołania z numerem lekcji w treści oraz wpisy szkoły na cały dzień (np. święta).
    /// </summary>
    public static string? PairKey(string type, IReadOnlyDictionary<string, string> fields)
    {
        string F(string label) => ElementKeys.Normalize(fields.GetValueOrDefault(label)).ToLowerInvariant();
        switch (type)
        {
            case ElementTypes.ScheduleEvent:
                var title = F("Tytuł");
                var subject = F("Przedmiot");
                if (title.Length == 0 || title == subject) return null;
                if (title.Contains("na lekcji nr", StringComparison.Ordinal) || subject.StartsWith("odwołane", StringComparison.Ordinal)
                    || title.StartsWith("zastępstwo", StringComparison.Ordinal)) return null;
                return string.Join('\u001f', type, subject, title, F("Opis"));
            case ElementTypes.Homework:
                return string.Join('\u001f', type, F("Przedmiot"), F("Temat"), F("Kategoria"), F("Nauczyciel"), F("Data udostępnienia"));
            default:
                return null;
        }
    }
}
