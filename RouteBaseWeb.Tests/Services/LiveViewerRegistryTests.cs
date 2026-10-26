using RouteBaseWeb.Services;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Pins the live-viewer registry behind the upload-cadence battery gate: the phone uploads every 5 s only
/// while <see cref="LiveViewerRegistry.IsWatched"/> says someone is on the live view. A viewer counted after
/// it is gone pins the phone at that cadence for the whole ride (battery), and one not counted while present
/// starves the live view (correctness) — so both edges are asserted.
/// </summary>
public class LiveViewerRegistryTests
{
    private const string Session = "sess-1";

    [Fact]
    public void IsWatched_NoViewers_IsFalse()
    {
        var registry = new LiveViewerRegistry(() => DateTime.UtcNow);

        Assert.False(registry.IsWatched(Session));
    }

    [Fact]
    public void Join_MakesTheSessionWatched()
    {
        var registry = new LiveViewerRegistry(() => DateTime.UtcNow);

        registry.Join(Session, "conn-a");

        Assert.True(registry.IsWatched(Session));
    }

    [Fact]
    public void Join_OnlyMarksItsOwnSession()
    {
        var registry = new LiveViewerRegistry(() => DateTime.UtcNow);

        registry.Join(Session, "conn-a");

        Assert.False(registry.IsWatched("other-session"));
    }

    [Fact]
    public void Leave_LastViewer_MakesTheSessionUnwatched()
    {
        var registry = new LiveViewerRegistry(() => DateTime.UtcNow);
        registry.Join(Session, "conn-a");

        registry.Leave(Session, "conn-a");

        Assert.False(registry.IsWatched(Session));
    }

    [Fact]
    public void Leave_OneOfTwoViewers_KeepsTheSessionWatched()
    {
        var registry = new LiveViewerRegistry(() => DateTime.UtcNow);
        registry.Join(Session, "conn-a");
        registry.Join(Session, "conn-b");

        registry.Leave(Session, "conn-a");

        Assert.True(registry.IsWatched(Session));
    }

    [Fact]
    public void Join_SameConnectionTwice_LeavesOnce_IsUnwatched()
    {
        // The set is keyed by connection id: a re-join (page re-subscribe) must not need two leaves.
        var registry = new LiveViewerRegistry(() => DateTime.UtcNow);
        registry.Join(Session, "conn-a");
        registry.Join(Session, "conn-a");

        registry.Leave(Session, "conn-a");

        Assert.False(registry.IsWatched(Session));
    }

    [Fact]
    public void Leave_UnknownSessionOrConnection_IsANoOp()
    {
        var registry = new LiveViewerRegistry(() => DateTime.UtcNow);
        registry.Join(Session, "conn-a");

        registry.Leave("never-joined", "conn-a");
        registry.Leave(Session, "never-joined");

        Assert.True(registry.IsWatched(Session));
    }

    [Fact]
    public void LeaveAll_Disconnect_RemovesTheConnectionFromEverySessionItWatched()
    {
        // The disconnect path is the battery-critical one: a closed tab never leaves a session explicitly,
        // so without this the phone would stay at the fast cadence forever.
        var registry = new LiveViewerRegistry(() => DateTime.UtcNow);
        registry.Join("sess-a", "conn-a");
        registry.Join("sess-b", "conn-a");
        registry.Join("sess-b", "conn-b");

        registry.LeaveAll("conn-a");

        Assert.False(registry.IsWatched("sess-a"));
        Assert.True(registry.IsWatched("sess-b"));   // conn-b is still watching it
    }

    [Fact]
    public void LeaveAll_ThenRejoin_IsWatchedAgain()
    {
        // A pruned session entry must be re-creatable — the reconnect case (SignalR drops and re-joins).
        var registry = new LiveViewerRegistry(() => DateTime.UtcNow);
        registry.Join(Session, "conn-a");
        registry.LeaveAll("conn-a");

        registry.Join(Session, "conn-a");

        Assert.True(registry.IsWatched(Session));
    }

    // ── Anonymous (share-link) viewers: time-stamped heartbeat, not a connection ──

    [Fact]
    public void TouchPublic_MakesTheSessionWatched()
    {
        // A public viewer holds no SignalR connection — a fresh poll must still count as watching, or the
        // phone would sit at its slow idle cadence while a public link is open.
        var registry = new LiveViewerRegistry(() => DateTime.UtcNow);

        registry.TouchPublic(Session);

        Assert.True(registry.IsWatched(Session));
    }

    [Fact]
    public void TouchPublic_OnlyMarksItsOwnSession()
    {
        var registry = new LiveViewerRegistry(() => DateTime.UtcNow);

        registry.TouchPublic(Session);

        Assert.False(registry.IsWatched("other-session"));
    }

    [Fact]
    public void PublicTouch_GoesStale_AfterTheWindow_SoTheGateSelfHeals()
    {
        // Battery edge: a public viewer that closes its tab stops polling, and the stamp must age out so the
        // phone drops back to the idle cadence on its own. Clock is injected so this needs no real wait.
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var registry = new LiveViewerRegistry(() => now);

        registry.TouchPublic(Session);
        Assert.True(registry.IsWatched(Session));

        now = now.AddSeconds(31); // past the ~30 s window
        Assert.False(registry.IsWatched(Session));
    }

    [Fact]
    public void PublicTouch_WithinTheWindow_StaysWatched()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var registry = new LiveViewerRegistry(() => now);

        registry.TouchPublic(Session);
        now = now.AddSeconds(20); // still inside the window
        registry.TouchPublic(Session); // a subsequent poll refreshes the stamp

        now = now.AddSeconds(20);
        Assert.True(registry.IsWatched(Session));
    }

    [Fact]
    public void TouchPublic_SweepsOtherSessionsStaleStamps()
    {
        // The only other prune site (IsWatched) is called from the phone's append path, which stops once a
        // session goes terminal — a later poll of ANY session must sweep the leftovers, or the singleton
        // grows by one stamp per session ever publicly polled (API-05).
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var registry = new LiveViewerRegistry(() => now);
        registry.TouchPublic("finished-session");

        now = now.AddSeconds(31); // its stamp is now stale, and its phone will never call IsWatched again
        registry.TouchPublic(Session);

        Assert.Equal(1, registry.PublicStampCount); // only the fresh stamp remains
    }

    [Fact]
    public void SignalRViewer_KeepsWatched_EvenWithNoRecentPublicTouch()
    {
        // The two sources are OR-ed: a connected hub viewer is watched regardless of the public heartbeat.
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var registry = new LiveViewerRegistry(() => now);

        registry.Join(Session, "conn-a");
        now = now.AddMinutes(5);

        Assert.True(registry.IsWatched(Session));
    }

    [Fact]
    public async Task LeavePrune_RacingAJoin_NeverStrandsTheJoiner()
    {
        // SVC-03 interleave: Leave empties the set, reads IsEmpty, and prunes the set out of the map while
        // a concurrent Join has already written into that same instance AND passed its verify. The pruned
        // set then held a live watcher the registry forgot — phone dropped to idle cadence mid-watch.
        var registry = new LiveViewerRegistry(() => DateTime.UtcNow);

        for (var i = 0; i < 500; i++)
        {
            var leaver = Task.Run(() => { registry.Join(Session, "leaver"); registry.Leave(Session, "leaver"); });
            var joiner = Task.Run(() => registry.Join(Session, "joiner"));
            await Task.WhenAll(leaver, joiner);

            Assert.True(registry.IsWatched(Session), $"round {i}: joined viewer lost to the prune race");
            registry.Leave(Session, "joiner");
        }
    }

    [Fact]
    public async Task ConcurrentJoinsAndLeaves_ConvergeOnTheSurvivingViewer()
    {
        // The prune-on-last-leave race: a Leave that empties the set removes it from the map while a
        // concurrent Join may be writing into that same set. The survivor must still read as watched.
        var registry = new LiveViewerRegistry(() => DateTime.UtcNow);
        var churn = Enumerable.Range(0, 200).Select(i => Task.Run(() =>
        {
            var conn = $"conn-{i}";
            registry.Join(Session, conn);
            registry.Leave(Session, conn);
        }));

        registry.Join(Session, "viewer");
        await Task.WhenAll(churn);

        Assert.True(registry.IsWatched(Session));
    }
}
