namespace RouteBaseWeb.Tests;

/// <summary>
/// Deterministic <see cref="TimeProvider"/> for the K2 clock seam — hand-rolled instead of
/// Microsoft.Extensions.TimeProvider.Testing because NuGet additions are Docker-locked
/// (lockfiles). Only what the fake-time suites need: a settable, advanceable UtcNow.
/// Timer-driven code wanting instant callbacks uses StravaApiClientTests' RecordingTimeProvider.
/// </summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _utcNow;

    public TestClock(DateTimeOffset start) => _utcNow = start;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan by) => _utcNow += by;

    public void SetUtcNow(DateTimeOffset to) => _utcNow = to;
}
