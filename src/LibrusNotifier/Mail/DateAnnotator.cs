using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using LibrusNotifier.Calendar;
using LibrusNotifier.Timing;

namespace LibrusNotifier.Mail;

/// <summary>
/// Dopisuje do dat w treści maila opis względem dzisiaj, np. <c>2026-11-13 (piątek, jutro)</c>. Rozpoznaje daty ISO
/// (<c>rrrr-mm-dd</c>, opcjonalnie z godziną – opis jest wtedy za godziną). W wersji HTML data wraz z opisem jest pogrubiona,
/// gdy dotyczy dnia dzisiejszego, jutrzejszego albo – gdy dziś jest wolne (weekend, święto, ferie) – pierwszego dnia szkolnego po wolnym.
/// Daty czysto techniczne (np. kiedy nadano wiadomość) dostają tylko nazwę dnia tygodnia i nie są pogrubiane (<see cref="DateMode.WeekdayOnly"/>).
/// </summary>
public sealed partial class DateAnnotator
{
    /// <summary>Jak opisywać daty w tekście.</summary>
    public enum DateMode
    {
        /// <summary>Dzień tygodnia i odległość od dzisiaj („piątek, jutro”); daty „na teraz” są pogrubione w HTML-u.</summary>
        Relative,

        /// <summary>Tylko dzień tygodnia („czwartek”), bez pogrubienia: dla dat czysto technicznych, których bliskość nic nie znaczy.</summary>
        WeekdayOnly
    }

    private static readonly string[] DayNames = ["niedziela", "poniedziałek", "wtorek", "środa", "czwartek", "piątek", "sobota"];

    /// <summary>Tyle dni do przodu szukany jest pierwszy dzień szkolny po wolnym (dłuższa przerwa niż wakacje się nie zdarza).</summary>
    private const int NextSchoolDaySearchLimit = 120;

    private readonly DateOnly _today;
    private readonly DateOnly? _firstSchoolDayAfterFree;

    /// <param name="calendar">Kalendarz szkolny; bez niego dniami wolnymi są tylko weekendy i święta państwowe.</param>
    public DateAnnotator(Clock clock, SchoolCalendar? calendar = null)
    {
        _today = clock.Today;
        calendar ??= new SchoolCalendar([]);
        if (calendar.GetDayType(_today) == DayType.School) return;
        for (var i = 1; i <= NextSchoolDaySearchLimit; i++)
        {
            var day = _today.AddDays(i);
            if (calendar.GetDayType(day) != DayType.School) continue;
            _firstSchoolDayAfterFree = day;
            return;
        }
    }

    [GeneratedRegex(@"(?<!\d)\d{4}-\d{2}-\d{2}(?:[ T]\d{1,2}:\d{2}(?::\d{2})?)?(?!\d)")]
    private static partial Regex DatePattern();

    /// <summary>Tekst z dopisanymi opisami dat, bez formatowania.</summary>
    public string Plain(string text, DateMode mode = DateMode.Relative) => string.Concat(Split(text, mode).Select(s => s.Text));

    /// <summary>
    /// Jak <see cref="Plain"/>, ale gdy wynik przekracza <paramref name="maxLength"/>, skraca (wielokropkiem) najwcześniejszy fragment
    /// tekstu między datami, tak by daty i ich opisy zostały w całości. Gdy się nie da, zwraca pełny tekst (skróci go wołający).
    /// </summary>
    public string PlainFitting(string text, int maxLength)
    {
        const int MinKept = 8;
        var segments = Split(text, DateMode.Relative).ToList();
        var overflow = segments.Sum(s => s.Text.Length) - maxLength;
        if (overflow > 0)
        {
            for (var i = 0; i < segments.Count; i++)
            {
                if (segments[i].IsDate) continue;
                var value = segments[i].Text;
                var tailLength = value.Length - value.TrimEnd(' ', ',', ';', ':', '–', '-').Length;
                var body = value[..^tailLength];
                var kept = body.Length - overflow - 1;
                if (kept < MinKept) continue;
                segments[i] = segments[i] with { Text = body[..kept] + "…" + value[^tailLength..] };
                break;
            }
        }
        return string.Concat(segments.Select(s => s.Text));
    }

    /// <summary>Tekst zakodowany do HTML-a (znaki nowej linii jako <c>&lt;br&gt;</c>), z pogrubionymi datami „na teraz”.</summary>
    public string Html(string text, DateMode mode = DateMode.Relative)
    {
        var html = new StringBuilder();
        foreach (var segment in Split(text.Replace("\r\n", "\n").Replace('\r', '\n'), mode))
        {
            var encoded = WebUtility.HtmlEncode(segment.Text);
            html.Append(segment.Emphasize ? $"<strong>{encoded}</strong>" : encoded);
        }
        return html.ToString().Replace("\n", "<br>\n");
    }

    /// <summary>Opis daty względem dzisiaj, np. „piątek, jutro” albo „środa, za 6 dni”.</summary>
    public string Describe(DateOnly date)
    {
        var days = date.DayNumber - _today.DayNumber;
        var relative = days switch
        {
            0 => "dziś",
            1 => "jutro",
            2 => "pojutrze",
            -1 => "wczoraj",
            -2 => "przedwczoraj",
            > 2 => $"za {days} dni",
            _ => $"{-days} dni temu"
        };
        return $"{DayNames[(int)date.DayOfWeek]}, {relative}";
    }

    public bool IsEmphasized(DateOnly date) => date == _today || date == _today.AddDays(1) || date == _firstSchoolDayAfterFree;

    private readonly record struct Segment(string Text, bool IsDate, bool Emphasize);

    private IEnumerable<Segment> Split(string text, DateMode mode)
    {
        var last = 0;
        foreach (Match match in DatePattern().Matches(text))
        {
            if (!DateOnly.TryParseExact(match.Value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            if (match.Index > last) yield return new Segment(text[last..match.Index], false, false);
            yield return mode == DateMode.WeekdayOnly
                ? new Segment($"{match.Value} ({DayNames[(int)date.DayOfWeek]})", true, false)
                : new Segment($"{match.Value} ({Describe(date)})", true, IsEmphasized(date));
            last = match.Index + match.Length;
        }
        if (last < text.Length) yield return new Segment(text[last..], false, false);
    }
}
