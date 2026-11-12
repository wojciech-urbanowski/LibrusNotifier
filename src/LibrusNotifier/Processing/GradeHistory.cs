using LibrusNotifier.Data;
using LibrusNotifier.Librus;

namespace LibrusNotifier.Processing;

/// <summary>
/// Dobór ocen do raportu pod powiadomieniem o nowej ocenie (<see cref="Mail.GradeReport"/>): oceny z tego samego przedmiotu,
/// które są teraz w Librusie. Wszystko z bazy, bez żądań do Librusa.
/// </summary>
public static class GradeHistory
{
    /// <summary>
    /// Oceny z przedmiotu <paramref name="element"/> dla grupy odbiorców: rodzice widzą oceny z obu kont, uczeń tylko te z własnego konta
    /// (jak przy powiadomieniach). Pomijane są oceny, które zniknęły z listy w Librusie (np. po poprawieniu oceny w dzienniku –
    /// zostają w bazie, ale nie są już aktualne). Sama oceniana ocena jest w wyniku zawsze.
    /// </summary>
    /// <param name="grades">Wszystkie oceny z bazy (typ <see cref="ElementTypes.Grade"/>); inne typy są ignorowane.</param>
    public static IReadOnlyList<ElementEntity> ForSubject(ElementEntity element, IEnumerable<ElementEntity> grades,
        IReadOnlyList<BaselineEntity> baselines, RecipientGroup group)
    {
        var subject = SubjectOf(element);
        if (subject.Length == 0) return [];
        var result = grades
            .Where(g => g.Type == ElementTypes.Grade && g.Key != element.Key)
            .Where(g => string.Equals(SubjectOf(g), subject, StringComparison.OrdinalIgnoreCase))
            .Where(g => group == RecipientGroup.Parents || g.VisibleToStudent)
            .Where(g => !TrackedElements.IsGone(g, baselines, ModuleKind.Grades))
            .ToList();
        result.Add(element);
        return result;
    }

    private static string SubjectOf(ElementEntity element) =>
        ElementKeys.Normalize(TrackedElements.Fields(element).GetValueOrDefault("Przedmiot"));
}
