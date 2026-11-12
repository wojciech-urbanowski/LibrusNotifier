using System.Globalization;
using LibrusNotifier.Data;
using LibrusNotifier.Librus;
using LibrusNotifier.Processing;

namespace LibrusNotifier.Mail;

/// <summary>Wiersz raportu ocen z przedmiotu.</summary>
/// <param name="Date">Data oceny (<c>rrrr-mm-dd</c>, gdy da się ją odczytać, inaczej tekst z Librusa).</param>
/// <param name="Counts">Czy ocena jest liczona do średniej.</param>
/// <param name="IsCurrent">Ocena, której dotyczy powiadomienie.</param>
public sealed record GradeReportRow(string Date, string Mark, string Category, string Weight, bool Counts, bool IsCurrent);

/// <summary>
/// Raport pod powiadomieniem o nowej ocenie: oceny z tego samego przedmiotu i semestru, od najnowszej do najstarszej
/// (po dacie oceny, przy tej samej dacie – od ostatnio wykrytej).
/// </summary>
/// <param name="Semester">Semestr nowej oceny (1 albo 2); <c>null</c>, gdy go nie ma w danych oceny.</param>
public sealed record GradeReport(string Subject, int? Semester, IReadOnlyList<GradeReportRow> Rows)
{
    /// <param name="current">Ocena, której dotyczy powiadomienie; jest w raporcie zawsze, także gdy brak jej na liście <paramref name="grades"/>.</param>
    /// <param name="grades">Oceny z tego przedmiotu i okresu (wybór: <see cref="GradeHistory.ForSubject"/>).</param>
    /// <returns><c>null</c>, gdy ocena nie ma przedmiotu.</returns>
    public static GradeReport? Build(ElementEntity current, IEnumerable<ElementEntity> grades)
    {
        var currentFields = TrackedElements.Fields(current);
        var subject = currentFields.GetValueOrDefault("Przedmiot", "");
        if (subject.Length == 0) return null;

        var all = grades.Where(g => g.Key != current.Key).Append(current);
        var rows = all
            .Select(g => (Element: g, Fields: TrackedElements.Fields(g)))
            .Select(x => (x.Element, x.Fields, Date: TrackedElements.TryGetDate(ElementTypes.Grade, x.Fields, out var date) ? date : (DateOnly?)null))
            .OrderByDescending(x => x.Date ?? DateOnly.MinValue)
            .ThenByDescending(x => x.Element.FirstSeenUtc)
            .ThenByDescending(x => x.Element.Id)
            .Select(x => new GradeReportRow(
                x.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? x.Fields.GetValueOrDefault("Data", ""),
                x.Fields.GetValueOrDefault("Ocena", ""),
                x.Fields.GetValueOrDefault("Kategoria", ""),
                x.Fields.GetValueOrDefault("Waga", ""),
                x.Fields.GetValueOrDefault("Liczona do średniej", "tak") != "nie",
                x.Element.Key == current.Key))
            .ToList();
        return new GradeReport(subject, GradeTerm.Of(currentFields).Semester, rows);
    }
}
