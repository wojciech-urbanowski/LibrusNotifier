using LibrusNotifier.Data;
using LibrusNotifier.Librus;

namespace LibrusNotifier.Processing;

/// <summary>
/// Dobór ocen do raportu pod powiadomieniem o nowej ocenie (<see cref="Mail.GradeReport"/>): oceny z tego samego przedmiotu
/// i z tego samego okresu (<see cref="GradeTerm"/>: semestr i rok szkolny), które są teraz w Librusie. Wszystko z bazy,
/// bez żądań do Librusa.
/// </summary>
public static class GradeHistory
{
    /// <summary>
    /// Oceny z przedmiotu <paramref name="element"/> dla grupy odbiorców: rodzice widzą oceny z obu kont, uczeń tylko te z własnego konta
    /// (jak przy powiadomieniach). Lista ocen w Librusie obejmuje oba semestry, więc raport nowej oceny z drugiego semestru nie zawiera
    /// ocen z pierwszego (i odwrotnie); nie zawiera też ocen z poprzedniego roku szkolnego, które zostały w bazie. Pomijane są oceny,
    /// które zniknęły z listy w Librusie (np. po poprawieniu oceny w dzienniku – zostają w bazie, ale nie są już aktualne).
    /// Sama oceniana ocena jest w wyniku zawsze.
    /// </summary>
    /// <param name="grades">Wszystkie oceny z bazy (typ <see cref="ElementTypes.Grade"/>); inne typy są ignorowane.</param>
    public static IReadOnlyList<ElementEntity> ForSubject(ElementEntity element, IEnumerable<ElementEntity> grades,
        IReadOnlyList<BaselineEntity> baselines, RecipientGroup group)
    {
        var currentFields = TrackedElements.Fields(element);
        var subject = SubjectOf(currentFields);
        if (subject.Length == 0) return [];
        var term = GradeTerm.Of(currentFields);
        var result = grades
            .Where(g => g.Type == ElementTypes.Grade && g.Key != element.Key)
            .Where(g => group == RecipientGroup.Parents || g.VisibleToStudent)
            .Select(g => (Element: g, Fields: TrackedElements.Fields(g)))
            .Where(x => string.Equals(SubjectOf(x.Fields), subject, StringComparison.OrdinalIgnoreCase))
            .Where(x => term.Matches(GradeTerm.Of(x.Fields)))
            .Select(x => x.Element)
            .Where(g => !TrackedElements.IsGone(g, baselines, ModuleKind.Grades))
            .ToList();
        result.Add(element);
        return result;
    }

    private static string SubjectOf(IReadOnlyDictionary<string, string> fields) =>
        ElementKeys.Normalize(fields.GetValueOrDefault("Przedmiot"));
}
