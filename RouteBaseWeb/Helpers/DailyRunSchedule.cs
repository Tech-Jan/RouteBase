namespace RouteBaseWeb.Helpers;

/// <summary>
/// Delay until the next daily run at a fixed server-local hour, shared by the nightly hosted
/// services (backup 02:00, maintenance 03:00, tile sweep 04:00). "Off-peak" is a wall-clock
/// concept, so this deliberately reads <see cref="DateTimeOffset.Now"/> — the type says
/// "server-local with a known offset", unlike bare <c>DateTime.Now</c> which the clock gate bans.
/// </summary>
public static class DailyRunSchedule
{
    public static TimeSpan DelayUntilNextLocalHour(int runAtHour) =>
        DelayUntilNextLocalHour(runAtHour, DateTimeOffset.Now);

    // Pure core for tests. A same-day hour that already passed rolls to tomorrow; a DST jump
    // between now and the target shifts the fire time by the jump (Task.Delay counts real time),
    // which all three consumers tolerate — "roughly that quiet hour" is the contract.
    internal static TimeSpan DelayUntilNextLocalHour(int runAtHour, DateTimeOffset now)
    {
        var next = new DateTimeOffset(now.Year, now.Month, now.Day, runAtHour, 0, 0, now.Offset);
        if (next <= now)
            next = next.AddDays(1);
        return next - now;
    }
}
