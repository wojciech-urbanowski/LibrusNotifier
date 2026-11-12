namespace LibrusNotifier.Librus;

public enum AccountKind
{
    Parent,
    Student
}

/// <summary>Moduł Librusa, z którego pobierana jest jedna lista.</summary>
public enum ModuleKind
{
    Grades,
    Attendance,
    Messages,
    Announcements,
    Schedule,
    Homework,
    Timetable,
    CompletedLessons
}

/// <summary>Typy elementów zapisywane w bazie (kolumna <c>Type</c>).</summary>
public static class ElementTypes
{
    public const string Grade = "ocena";
    public const string DescriptiveGrade = "ocena_opisowa";
    public const string Attendance = "frekwencja";
    public const string Message = "wiadomosc";
    public const string Announcement = "ogloszenie";
    public const string ScheduleEvent = "terminarz";
    public const string Homework = "zadanie_domowe";
    public const string TimetableChange = "zmiana_planu";
    public const string CompletedLesson = "zrealizowana_lekcja";
}

public static class KindNames
{
    public static IReadOnlyList<ModuleKind> AllModules { get; } = Enum.GetValues<ModuleKind>();

    public static string DisplayName(this AccountKind account) => account == AccountKind.Parent ? "rodzic" : "uczeń";

    public static string DisplayName(this ModuleKind module) => module switch
    {
        ModuleKind.Grades => "oceny",
        ModuleKind.Attendance => "frekwencja",
        ModuleKind.Messages => "wiadomości",
        ModuleKind.Announcements => "ogłoszenia",
        ModuleKind.Schedule => "terminarz",
        ModuleKind.Homework => "zadania domowe",
        ModuleKind.Timetable => "plan lekcji",
        ModuleKind.CompletedLessons => "zrealizowane lekcje",
        _ => module.ToString()
    };
}
