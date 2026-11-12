namespace LibrusNotifier.Digest;

/// <summary>
/// Rodzaj zdarzenia w zestawieniu. Od rodzaju zależy kolor pola z typem zdarzenia w mailu (patrz <see cref="DigestPalette"/>).
/// </summary>
public enum EventCategory
{
    /// <summary>Sprawdzian, praca klasowa, egzamin.</summary>
    Test,

    Quiz,
    Homework,
    Substitution,
    Cancelled,

    /// <summary>Dzień wolny od zajęć (wpis szkoły w terminarzu lub oznaczenie w planie lekcji).</summary>
    DayOff,

    /// <summary>Wydarzenie szkolne lub klasowe (wycieczka, zebranie, apel itp.).</summary>
    Event,

    Other
}

/// <summary>Skąd pochodzi zdarzenie; przy łączeniu duplikatów pierwszeństwo ma źródło bogatsze w dane.</summary>
public enum EventSource
{
    Schedule,
    Timetable,
    Homework
}

/// <param name="Date">Dzień zdarzenia (dla zadania domowego – termin wykonania).</param>
/// <param name="Label">Napis w polu z typem zdarzenia (np. „Sprawdzian”, „Praca klasowa”).</param>
/// <param name="Lesson">Numer lekcji; <c>null</c> dla zdarzeń na cały dzień lub bez przypisanej lekcji.</param>
/// <param name="Hours">Godziny (np. „08:00–08:45”) albo godzina rozpoczęcia; puste, gdy nieznane.</param>
/// <param name="Subject">Główna linia: przedmiot albo nazwa wydarzenia.</param>
/// <param name="Detail">Druga linia: opis, temat, szczegóły zmiany.</param>
/// <param name="Note">Trzecia, przygaszona linia: nauczyciel.</param>
public sealed record DigestEvent(DateOnly Date, EventCategory Category, string Label, int? Lesson, string Hours, string Subject, string Detail,
    string Note, EventSource Source)
{
    /// <summary>Kolejność w obrębie dnia: najpierw zdarzenia na cały dzień, potem z godziną, potem wg numeru lekcji.</summary>
    internal (int Group, int Position) TimeOrder
    {
        get
        {
            if (Lesson is { } lesson) return (2, lesson);
            if (TryHour(Hours, out var minutes)) return (1, minutes);
            return (0, 0);
        }
    }

    private static bool TryHour(string hours, out int minutes)
    {
        minutes = 0;
        if (hours.Length < 4) return false;
        var end = hours.IndexOfAny(['–', '-', ' ']);
        var start = end < 0 ? hours : hours[..end];
        if (!TimeOnly.TryParseExact(start, ["H:mm", "HH:mm"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var time))
            return false;
        minutes = time.Hour * 60 + time.Minute;
        return true;
    }
}

/// <summary>
/// Pastelowe kolory pól z typem zdarzenia. Tło i tekst są ustawiane razem (ciemny tekst na jasnym tle),
/// więc pole jest czytelne zarówno w jasnym, jak i w ciemnym motywie klienta poczty.
/// </summary>
public static class DigestPalette
{
    public const string TextColor = "#1f1f1f";

    public static string Background(EventCategory category) => category switch
    {
        EventCategory.Test => "#f28b82",         // czerwony, mocniejszy
        EventCategory.Quiz => "#fad0cc",         // czerwony, lżejszy
        EventCategory.Homework => "#d9c4f2",     // fioletowy
        EventCategory.Substitution => "#b9e4c4", // zielony
        EventCategory.Cancelled => "#ffd7a8",    // pomarańczowy
        EventCategory.DayOff => "#bcd9f5",       // niebieski
        EventCategory.Event => "#fff0a8",        // żółty
        _ => "#e2e2e2"                           // szary
    };

    public static bool Bold(EventCategory category) => category == EventCategory.Test;

    /// <summary>Opis koloru do legendy i do zestawienia kolorów w dokumentacji.</summary>
    public static string ColorName(EventCategory category) => category switch
    {
        EventCategory.Test => "czerwony (mocniejszy)",
        EventCategory.Quiz => "czerwony (lżejszy)",
        EventCategory.Homework => "fioletowy",
        EventCategory.Substitution => "zielony",
        EventCategory.Cancelled => "pomarańczowy",
        EventCategory.DayOff => "niebieski",
        EventCategory.Event => "żółty",
        _ => "szary"
    };

    /// <summary>Nazwa rodzaju w legendzie.</summary>
    public static string LegendLabel(EventCategory category) => category switch
    {
        EventCategory.Test => "Sprawdzian",
        EventCategory.Quiz => "Kartkówka",
        EventCategory.Homework => "Zadanie domowe",
        EventCategory.Substitution => "Zastępstwo",
        EventCategory.Cancelled => "Odwołane zajęcia",
        EventCategory.DayOff => "Dzień wolny",
        EventCategory.Event => "Wydarzenie",
        _ => "Inne"
    };
}
