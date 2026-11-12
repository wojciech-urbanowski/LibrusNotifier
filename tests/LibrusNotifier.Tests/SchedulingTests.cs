using LibrusNotifier.Calendar;
using LibrusNotifier.Scheduling;

namespace LibrusNotifier.Tests;

public sealed class SchedulingTests
{
    private static readonly DateOnly Day = new(2026, 11, 12);

    private static DateTime At(int hour, int minute = 0) => Day.ToDateTime(new TimeOnly(hour, minute));

    private static PollingScheduler Scheduler(IReadOnlyList<PollingWindow> windows, double random = 0.5) => new(Day, windows, new FixedRandom(random));

    [Fact]
    public void PlanDependsOnDayType()
    {
        Assert.Same(PollingPlan.School, PollingPlan.ForDay(new DayInfo(Day, DayType.School, false, null)));
        Assert.Same(PollingPlan.Free, PollingPlan.ForDay(new DayInfo(Day, DayType.Free, false, null)));
        Assert.Same(PollingPlan.BeforeSchool, PollingPlan.ForDay(new DayInfo(Day, DayType.Free, true, null)));
        Assert.Same(PollingPlan.Holiday, PollingPlan.ForDay(new DayInfo(Day, DayType.Holiday, false, null)));
        Assert.Same(PollingPlan.BeforeSchool, PollingPlan.ForDay(new DayInfo(Day, DayType.Holiday, true, null)));
    }

    [Fact]
    public void WindowDescriptionHasSpacesAroundRangeHyphen()
    {
        var window = new PollingWindow("Szkolny", TimeSpan.FromHours(6), TimeSpan.FromHours(16), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(4), JitterMode.Symmetric);
        Assert.Equal("Szkolny 06:00 - 16:00, co 15 min ±4 min", window.ToString());
    }

    [Fact]
    public void SchoolDayCoversWholeDayWithExpectedIntervals()
    {
        var scheduler = Scheduler(PollingPlan.School);
        Assert.Equal(TimeSpan.FromHours(1), scheduler.WindowAt(At(3))!.Interval);
        Assert.Equal(TimeSpan.FromMinutes(15), scheduler.WindowAt(At(6))!.Interval);
        Assert.Equal(TimeSpan.FromMinutes(25), scheduler.WindowAt(At(16))!.Interval);
        Assert.Equal(TimeSpan.FromHours(1), scheduler.WindowAt(At(23, 59))!.Interval);
    }

    [Fact]
    public void FirstPingIsImmediateInsideWindow() => Assert.Equal(At(9, 13), Scheduler(PollingPlan.Free).FirstPing(At(9, 13)));

    [Fact]
    public void FirstPingWaitsForNextWindowOfTheDay() => Assert.Equal(At(8), Scheduler(PollingPlan.Free).FirstPing(At(0, 5)));

    [Fact]
    public void FirstPingIsNullAfterLastWindow() => Assert.Null(Scheduler(PollingPlan.Free).FirstPing(At(22, 0)));

    [Theory]
    [InlineData(0.0, 11)]
    [InlineData(0.5, 15)]
    [InlineData(0.999999, 19)]
    public void SymmetricJitterStaysWithinBounds(double random, int expectedMinutes)
    {
        var next = Scheduler(PollingPlan.School, random).NextPing(At(9), At(9, 1))!.Value;
        Assert.Equal(expectedMinutes, (int)Math.Round((next - At(9)).TotalMinutes));
    }

    [Theory]
    [InlineData(0.0, 360)]
    [InlineData(0.999999, 420)]
    public void HolidayJitterIsPositiveOnly(double random, int expectedMinutes)
    {
        var next = Scheduler(PollingPlan.Holiday, random).NextPing(At(1), At(1, 1))!.Value;
        Assert.Equal(expectedMinutes, (int)Math.Round((next - At(1)).TotalMinutes));
    }

    [Fact]
    public void EnteringNewWindowStartsAtItsBeginning()
    {
        Assert.Equal(At(16), Scheduler(PollingPlan.School).NextPing(At(15, 50), At(15, 51)));
        Assert.Equal(At(6), Scheduler(PollingPlan.School).NextPing(At(5, 30), At(5, 31)));
        Assert.Equal(At(12), Scheduler(PollingPlan.BeforeSchool).NextPing(At(11), At(11, 1)));
    }

    [Fact]
    public void NoPingAfterLastWindowOrAfterMidnight()
    {
        Assert.Null(Scheduler(PollingPlan.Free).NextPing(At(20), At(20, 1)));
        Assert.Null(Scheduler(PollingPlan.School).NextPing(At(23, 30), At(23, 31)));
        Assert.Null(Scheduler(PollingPlan.BeforeSchool).NextPing(At(22, 50), At(22, 51)));
    }

    [Fact]
    public void LongCyclePingsAgainImmediatelyIfStillInWindow() =>
        Assert.Equal(At(10, 30), Scheduler(PollingPlan.School).NextPing(At(10), At(10, 30)));

    [Fact]
    public void BeforeSchoolDayHasDenserEvening()
    {
        var scheduler = Scheduler(PollingPlan.BeforeSchool);
        Assert.Null(scheduler.WindowAt(At(7, 59)));
        Assert.Equal(TimeSpan.FromHours(3), scheduler.WindowAt(At(8))!.Interval);
        Assert.Equal(TimeSpan.FromHours(1), scheduler.WindowAt(At(12))!.Interval);
        Assert.Equal(TimeSpan.FromMinutes(25), scheduler.WindowAt(At(20))!.Interval);
        Assert.Null(scheduler.WindowAt(At(23)));
    }
}

public sealed class SchoolCalendarTests
{
    private static readonly SchoolCalendar Calendar = new([new SchoolHolidayPeriod(new DateOnly(2027, 1, 18), new DateOnly(2027, 1, 31), "Ferie zimowe")]);

    [Fact]
    public void PublicHolidayOnWeekdayIsFree()
    {
        var day = Calendar.GetDay(new DateOnly(2026, 11, 11));
        Assert.Equal(DayType.Free, day.Type);
        Assert.True(day.BeforeSchool); // czwartek 12.11 jest szkolny
        Assert.Equal(DayType.School, Calendar.GetDayType(new DateOnly(2026, 11, 12)));
        Assert.False(Calendar.GetDay(new DateOnly(2026, 11, 10)).BeforeSchool); // dzień szkolny nigdy nie jest „przed szkołą”
    }

    [Fact]
    public void ChristmasEveIsPublicHolidaySince2025() => Assert.Equal(DayType.Free, Calendar.GetDayType(new DateOnly(2026, 12, 24)));

    [Fact]
    public void SundayBeforeSchoolDayIsMarked()
    {
        Assert.False(Calendar.GetDay(new DateOnly(2026, 11, 14)).BeforeSchool);
        Assert.True(Calendar.GetDay(new DateOnly(2026, 11, 15)).BeforeSchool);
        Assert.False(Calendar.GetDay(new DateOnly(2026, 11, 13)).BeforeSchool);
    }

    [Fact]
    public void SchoolHolidaysHavePriorityAndLastDayIsBeforeSchool()
    {
        Assert.Equal(DayType.Holiday, Calendar.GetDayType(new DateOnly(2027, 1, 18)));
        Assert.Equal(DayType.Holiday, Calendar.GetDayType(new DateOnly(2027, 1, 23)));
        Assert.Equal(DayType.School, Calendar.GetDayType(new DateOnly(2027, 1, 15)));
        Assert.False(Calendar.GetDay(new DateOnly(2027, 1, 17)).BeforeSchool);
        var last = Calendar.GetDay(new DateOnly(2027, 1, 31));
        Assert.Equal(DayType.Holiday, last.Type);
        Assert.True(last.BeforeSchool);
        Assert.Contains("Ferie zimowe", last.DisplayName);
    }

    [Fact]
    public void WithoutHolidayDataOnlyWeekendsAndPublicHolidaysCount() =>
        Assert.Equal(DayType.School, new SchoolCalendar([]).GetDayType(new DateOnly(2027, 1, 20)));
}
