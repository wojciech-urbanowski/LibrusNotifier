using System.Globalization;
using LibrusNotifier.Data;
using LibrusNotifier.Librus;
using LibrusNotifier.Processing;

namespace LibrusNotifier.Mail;

/// <summary>Wiersz listy ocen: przedmiot i jego oceny (same wartości, np. „4+”) od najstarszej do najnowszej.</summary>
public sealed record GradeTableRow(string Subject, IReadOnlyList<string> Marks);

/// <summary>
/// Oceny na końcu zestawienia dziennego: wiersz na przedmiot (alfabetycznie, po polsku), a w nim oceny z bieżącego semestru
/// (chronologicznie, od najstarszej). Dobór ocen jak w raporcie pod powiadomieniem o nowej ocenie (<see cref="GradeHistory"/>).
/// </summary>
/// <param name="Semester">Bieżący semestr (1 albo 2).</param>
/// <param name="SchoolYear">Rok, w którym zaczął się bieżący rok szkolny (2026 dla roku szkolnego 2026/27).</param>
public sealed record GradeTable(int Semester, int SchoolYear, IReadOnlyList<GradeTableRow> Rows)
{
    /// <summary>Rok szkolny w zapisie „2026/27”.</summary>
    public string SchoolYearName => string.Create(CultureInfo.InvariantCulture, $"{SchoolYear}/{(SchoolYear + 1) % 100:00}");

    /// <param name="grades">Wszystkie oceny z bazy (typ <see cref="ElementTypes.Grade"/>); inne typy są ignorowane.</param>
    /// <param name="group">Rodzice widzą oceny z obu kont, uczeń tylko te z własnego konta.</param>
    /// <param name="today">Dzień zestawienia; wyznacza bieżący semestr (<see cref="GradeTerm.CurrentAt"/>).</param>
    /// <returns><c>null</c>, gdy nie ma bieżącego semestru (wakacje letnie). Bez ocen w semestrze lista jest pusta (bez wierszy).</returns>
    public static GradeTable? Build(IEnumerable<ElementEntity> grades, IReadOnlyList<BaselineEntity> baselines, RecipientGroup group, DateOnly today)
    {
        if (GradeTerm.CurrentAt(today) is not { Semester: { } semester, SchoolYear: { } schoolYear } term) return null;

        var rows = GradeHistory.ForTerm(term, grades, baselines, group)
            .Select(g => (Element: g, Fields: TrackedElements.Fields(g)))
            .Select(x => (x.Element,
                Subject: ElementKeys.Normalize(x.Fields.GetValueOrDefault("Przedmiot")),
                Mark: x.Fields.GetValueOrDefault("Ocena", "").Trim(),
                Date: TrackedElements.TryGetDate(ElementTypes.Grade, x.Fields, out var date) ? date : (DateOnly?)null))
            .Where(x => x.Subject.Length > 0 && x.Mark.Length > 0)
            // Chronologicznie: po dacie oceny, przy tej samej dacie od najwcześniej wykrytej (odwrotnie niż w raporcie pod powiadomieniem).
            .OrderBy(x => x.Date ?? DateOnly.MinValue)
            .ThenBy(x => x.Element.FirstSeenUtc)
            .ThenBy(x => x.Element.Id)
            .GroupBy(x => x.Subject, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GradeTableRow(g.First().Subject, g.Select(x => x.Mark).ToList()))
            .OrderBy(r => r.Subject, PolishComparer.Instance)
            .ToList();
        return new GradeTable(semester, schoolYear, rows);
    }
}
