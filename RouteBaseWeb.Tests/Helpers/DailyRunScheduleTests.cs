using RouteBaseWeb.Helpers;
using Xunit;

namespace RouteBaseWeb.Tests.Helpers;

/// <summary>
/// Shared nightly scheduler (B1) — replaced three byte-identical DateTime.Now copies in the
/// hosted services. Pure core tested here; the DateTimeOffset.Now overload is a one-liner.
/// </summary>
public class DailyRunScheduleTests
{
    private static DateTimeOffset At(int hour, int minute, int second = 0) =>
        new(2026, 8, 1, hour, minute, second, TimeSpan.FromHours(2));

    [Fact]
    public void BeforeTheHour_RunsToday()
    {
        var delay = DailyRunSchedule.DelayUntilNextLocalHour(3, At(1, 30));
        Assert.Equal(TimeSpan.FromMinutes(90), delay);
    }

    [Fact]
    public void ExactlyAtTheHour_RollsToTomorrow()
    {
        var delay = DailyRunSchedule.DelayUntilNextLocalHour(3, At(3, 0));
        Assert.Equal(TimeSpan.FromHours(24), delay);
    }

    [Fact]
    public void AfterTheHour_RollsToTomorrow()
    {
        var delay = DailyRunSchedule.DelayUntilNextLocalHour(3, At(4, 30));
        Assert.Equal(TimeSpan.FromHours(22.5), delay);
    }

    [Fact]
    public void MidnightHour_JustBeforeMidnight_RunsWithinTheDay()
    {
        var delay = DailyRunSchedule.DelayUntilNextLocalHour(0, At(23, 59));
        Assert.Equal(TimeSpan.FromMinutes(1), delay);
    }

    [Fact]
    public void MonthBoundary_RollsIntoNextMonth()
    {
        var endOfMonth = new DateTimeOffset(2026, 8, 31, 5, 0, 0, TimeSpan.FromHours(2));
        var delay = DailyRunSchedule.DelayUntilNextLocalHour(2, endOfMonth);
        Assert.Equal(TimeSpan.FromHours(21), delay); // 2026-09-01 02:00 same offset
    }

    [Fact]
    public void DelayIsOffsetIndependent()
    {
        // Same local wall clock at two different offsets → same delay: the schedule is purely
        // wall-clock-relative, the offset only anchors "local".
        var plusTwo = DailyRunSchedule.DelayUntilNextLocalHour(3, At(1, 0));
        var minusFive = DailyRunSchedule.DelayUntilNextLocalHour(
            3, new DateTimeOffset(2026, 8, 1, 1, 0, 0, TimeSpan.FromHours(-5)));
        Assert.Equal(plusTwo, minusFive);
    }
}
