using System.Globalization;
using LibrusNotifier.Data;
using LibrusNotifier.Librus;

namespace LibrusNotifier.Processing;

/// <summary>
/// Okres, do którego należy ocena: semestr (pole „Semestr” z Librusa, 1 albo 2) i rok szkolny (z daty oceny; rok szkolny zaczyna się
/// 1 września, więc ocena z 2026-09-02 i z 2027-01-15 należą do roku 2026/27). Lista ocen w Librusie obejmuje oba semestry, a w bazie
/// zostają też oceny z poprzednich lat, więc raport ocen z przedmiotu (<see cref="GradeHistory"/>) obejmuje tylko oceny z okresu nowej oceny.
/// Składnik, którego nie da się odczytać, jest nieznany (<c>null</c>) i nie wyklucza żadnej oceny.
/// </summary>
public readonly record struct GradeTerm(int? Semester, int? SchoolYear)
{
    public static GradeTerm Of(ElementEntity grade) => Of(TrackedElements.Fields(grade));

    public static GradeTerm Of(IReadOnlyDictionary<string, string> fields)
    {
        int? semester = null;
        if (fields.TryGetValue("Semestr", out var text)
            && int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && (number == 1 || number == 2))
            semester = number;

        int? schoolYear = TrackedElements.TryGetDate(ElementTypes.Grade, fields, out var date) ? SchoolYearOf(date) : null;
        return new GradeTerm(semester, schoolYear);
    }

    /// <summary>Rok, w którym rok szkolny się zaczął (wrzesień–grudzień: rok daty, styczeń–sierpień: rok wcześniejszy).</summary>
    public static int SchoolYearOf(DateOnly date) => date.Month >= 9 ? date.Year : date.Year - 1;

    /// <summary>
    /// Bieżący okres wg daty: semestr 1 trwa od września do stycznia, semestr 2 od lutego do czerwca (jak w <see cref="SchoolYearOf"/>
    /// rok szkolny zaczyna się 1 września). Lipiec i sierpień to wakacje: nie ma wtedy bieżącego semestru (<c>null</c>).
    /// Termin zakończenia semestru ustala szkoła (najpóźniej w styczniu), więc granica jest przybliżona.
    /// </summary>
    public static GradeTerm? CurrentAt(DateOnly today) => today.Month switch
    {
        >= 9 or 1 => new GradeTerm(1, SchoolYearOf(today)),
        >= 2 and <= 6 => new GradeTerm(2, SchoolYearOf(today)),
        _ => null
    };

    /// <summary>Oceny są z tego samego okresu, o ile żaden ze znanych składników się nie różni.</summary>
    public bool Matches(GradeTerm other) => Agree(Semester, other.Semester) && Agree(SchoolYear, other.SchoolYear);

    private static bool Agree(int? first, int? second) => first is null || second is null || first == second;
}
