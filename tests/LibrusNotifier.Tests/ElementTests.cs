using LibrusApixNet;
using LibrusNotifier.Librus;
using LibrusNotifier.Processing;

namespace LibrusNotifier.Tests;

public sealed class ElementTests
{
    [Fact]
    public void KeyDependsOnTypeAndNormalizedContent()
    {
        var a = new ObservedItem("ocena", ["Matematyka", "5"], []);
        var b = new ObservedItem("ocena", [" Matematyka ", "5 "], []);
        var c = new ObservedItem("frekwencja", ["Matematyka", "5"], []);
        var keys = ElementKeys.Assign([a]).Concat(ElementKeys.Assign([b])).Concat(ElementKeys.Assign([c])).Select(k => k.Key).ToList();
        Assert.Equal(keys[0], keys[1]);
        Assert.NotEqual(keys[0], keys[2]);
        Assert.Equal(64, keys[0].Length);
    }

    [Fact]
    public void IdenticalItemsInOneListGetDistinctStableKeys()
    {
        var plus = new ObservedItem("ocena", ["WF", "+", "2026-11-10"], []);
        var first = ElementKeys.Assign([plus, plus]).Select(k => k.Key).ToList();
        var second = ElementKeys.Assign([plus, plus, plus]).Select(k => k.Key).ToList();
        Assert.NotEqual(first[0], first[1]);
        Assert.Equal(first, second.Take(2));
        Assert.DoesNotContain(second[2], first);
    }

    [Fact]
    public void GradeKeyIgnoresSemesterAndAccountSpecificLink()
    {
        Grade Make(int semester, string href) => new("Matematyka", "5", true, "2026-11-10", href, "Ocena: 5\nPrzedmiot: Matematyka\nKomentarz: brawo", semester, "Sprawdzian", "Anna", 3);
        GradesResult Result(Grade grade) => new([new() { ["Matematyka"] = [grade] }, []], [], [[], []]);
        var parent = LibrusLists.FromGrades(Result(Make(1, "/oceny/1")));
        var student = LibrusLists.FromGrades(Result(Make(2, "/oceny/999")));
        Assert.Equal(ElementKeys.Assign(parent)[0].Key, ElementKeys.Assign(student)[0].Key);
        var fields = parent[0].Fields.ToDictionary(f => f.Label, f => f.Value);
        Assert.Equal("5", fields["Ocena"]);
        Assert.Equal("Sprawdzian", fields["Kategoria"]);
        Assert.Equal("brawo", fields["Komentarz"]);
        Assert.False(fields.ContainsKey("Przedmiot: Matematyka"));
    }

    [Fact]
    public void AttendanceSkipsPresence()
    {
        var items = LibrusLists.FromAttendance([
            [
                new Attendance("nb", "1", 0, "2026-11-10", "Nieobecność", "Anna", 2, false, "Ułamki", "Matematyka"),
                new Attendance("ob", "2", 0, "2026-11-10", "Obecność", "Anna", 3, false, "Ułamki", "Matematyka")
            ]
        ]);
        Assert.Single(items);
        Assert.Equal(ElementTypes.Attendance, items[0].Type);
    }

    [Fact]
    public void TimetableReportsOnlyChangedLessons()
    {
        Period Lesson(int number, Dictionary<string, PeriodChange?> info) =>
            new("Matematyka", "Anna Nowak s. 12", "2026-11-12", "08:00", "08:45", "Thursday", info, number, null, null);
        var items = LibrusLists.FromTimetable([
            [
                Lesson(1, []),
                Lesson(2, new() { ["zastępstwo"] = new PeriodChange("Jan Kowalski", "", "", "2026-11-11 15:00") }),
                Lesson(3, new() { ["odwołane"] = null })
            ]
        ]);
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal(ElementTypes.TimetableChange, i.Type));
        Assert.Contains(items[0].Fields, f => f is { Label: "Nauczyciel (zmiana)", Value: "Jan Kowalski" });
    }

    [Fact]
    public void ScheduleItemsUseFullDateAndSkipUnknownPlaceholders()
    {
        var items = LibrusLists.FromSchedule(2026, 11, new Dictionary<int, List<Event>>
        {
            [20] = [new Event("Sprawdzian", "Matematyka", new() { ["Nauczyciel"] = "Anna", ["Opis"] = "Ułamki" }, "20", 2, "unknown", "x/1")],
            [31] = [new Event("Błędny dzień", "", [], "31", null, "unknown", "")]
        });
        var item = Assert.Single(items);
        Assert.Contains(item.Fields, f => f is { Label: "Data", Value: "2026-11-20" });
        Assert.DoesNotContain(item.Fields, f => f.Label == "Godzina");
    }

    [Fact]
    public void MondayOfWeek()
    {
        Assert.Equal(new DateOnly(2026, 11, 9), LibrusLists.MondayOf(new DateOnly(2026, 11, 12)));
        Assert.Equal(new DateOnly(2026, 11, 9), LibrusLists.MondayOf(new DateOnly(2026, 11, 15)));
        Assert.Equal(new DateOnly(2026, 11, 9), LibrusLists.MondayOf(new DateOnly(2026, 11, 9)));
    }

    [Fact]
    public void DetailsOnlyForMessagesAndHomeworkWithLink()
    {
        Assert.True(LibrusLists.HasDetails(Items.Message("Anna", "Temat", "42")));
        Assert.False(LibrusLists.HasDetails(Items.Message("Anna", "Temat", "")));
        Assert.False(LibrusLists.HasDetails(Items.Announcement("Ogłoszenie")));
        Assert.False(LibrusLists.HasDetails(Items.Grade("Matematyka", "5")));
    }

    [Fact]
    public void FieldJsonRoundTripKeepsOrderAndPolishCharacters()
    {
        var json = FieldJson.Serialize([new Field("Treść", "Zażółć gęślą jaźń"), new Field("Autor", "Ąna")]);
        Assert.Contains("Zażółć", json);
        Assert.Equal(["Treść", "Autor"], FieldJson.Deserialize(json).Select(f => f.Label));
        Assert.Empty(FieldJson.Deserialize("nie json"));
    }
}
