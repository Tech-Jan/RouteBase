using RouteBaseWeb.Services.Races;
using Xunit;
using RouteBaseWeb.Models;

namespace RouteBaseWeb.Tests.Services.Races;

/// <summary>
/// The per-leg → elapsed conversion. Every case here is a real shape from Ski to Sea 2025 (508 teams).
///
/// The rule under test: **the leg sum is the finish time.** The results file's published total is not an
/// authority — it is the wall clock, compressed by the organisers' forced starts — and it is consulted
/// only where the legs cannot answer.
///
/// Checkpoint ids 1..3 stand for three legs; the start is implicit and never appears.
/// </summary>
public class RaceLegAccumulatorTests
{
    private static RaceLeg Leg(int id, double? seconds) => new(new CheckpointId(id), seconds);

    private static CheckpointId Cp(int id) => new(id);

    [Fact]
    public void CompleteLegs_AccumulateToElapsedFromStart()
    {
        var result = RaceLegAccumulator.Accumulate(
            [Leg(1, 100), Leg(2, 200), Leg(3, 300)], officialFinishSeconds: 600);

        Assert.Equal([(Cp(1), 100.0), (Cp(2), 300.0), (Cp(3), 600.0)], result.Splits);
        Assert.False(result.OverlapsOfficialTotal);
    }

    /// <summary>
    /// The 36 cutoff teams. A leg racer missed the cutoff, the next was started anyway, and the two legs
    /// ran concurrently — so the published total is SHORTER than the racers' efforts actually took (by up
    /// to 2.3 h in the real file). The legs are the truth of what each athlete did, so they win: the team
    /// keeps every split and finishes LATER than the published result. Nothing is discarded.
    /// </summary>
    [Fact]
    public void LegsSummingPastThePublishedTotal_KeepEverySplit_AndFinishOnTheLegSum()
    {
        var result = RaceLegAccumulator.Accumulate(
            [Leg(1, 100), Leg(2, 200), Leg(3, 300)], officialFinishSeconds: 400);

        Assert.Equal([(Cp(1), 100.0), (Cp(2), 300.0), (Cp(3), 600.0)], result.Splits);   // 600, not the published 400
        Assert.True(result.OverlapsOfficialTotal);                            // reported, never acted on
    }

    [Fact]
    public void PublishedTotal_IsNeverSubstitutedForTheLegSum()
    {
        // Sub-tolerance drift (rounding in the results file). Even here the leg sum stands — one rule,
        // not a rule with an exception nobody can predict.
        var result = RaceLegAccumulator.Accumulate(
            [Leg(1, 100), Leg(2, 200), Leg(3, 300)], officialFinishSeconds: 599);

        Assert.Equal(600, result.Splits.Single(s => s.CheckpointId == Cp(3)).ElapsedSeconds);
        Assert.False(result.OverlapsOfficialTotal);   // within tolerance — nothing to report
    }

    [Fact]
    public void NoPublishedTotalAtAll_TheLegsStillProduceTheFinish()
    {
        // A relay export with only leg times is a complete file, not a broken one.
        var result = RaceLegAccumulator.Accumulate(
            [Leg(1, 100), Leg(2, 200), Leg(3, 300)], officialFinishSeconds: null);

        Assert.Equal([(Cp(1), 100.0), (Cp(2), 300.0), (Cp(3), 600.0)], result.Splits);
        Assert.False(result.OverlapsOfficialTotal);
    }

    /// <summary>
    /// ONE hole against a known total is one equation: the missing leg IS total - sum(the rest). Solve it
    /// and the rider rejoins the normal path with a full set of legs.
    /// </summary>
    [Fact]
    public void OneHole_IsSolvedFromThePublishedTotal()
    {
        var result = RaceLegAccumulator.Accumulate(
            [Leg(1, null), Leg(2, 200), Leg(3, 300)], officialFinishSeconds: 600);

        Assert.Equal([(Cp(1), 100.0), (Cp(2), 300.0), (Cp(3), 600.0)], result.Splits);   // leg 1 = 600 - 500
    }

    /// <summary>
    /// TWO holes are two unknowns and one equation — the legs cannot produce a finish at all, so there is
    /// no leg-sum clock to be had. The rider falls back onto the published (compressed) clock, which is
    /// the only anchor left. Mixing clocks is not ideal; vanishing from the field is worse.
    /// </summary>
    [Fact]
    public void ConsecutiveHoles_FallBackToThePublishedClock()
    {
        var result = RaceLegAccumulator.Accumulate(
            [Leg(1, null), Leg(2, null), Leg(3, 300)], officialFinishSeconds: 600);

        Assert.Equal([(Cp(2), 300.0), (Cp(3), 600.0)], result.Splits);   // cp1 unrecoverable — and not guessed
    }

    [Fact]
    public void HolesAndNoPublishedTotal_ForwardPrefixOnly_AndHeIsHeldAtHisLastCheckpoint()
    {
        var result = RaceLegAccumulator.Accumulate(
            [Leg(1, 100), Leg(2, 200), Leg(3, null)], officialFinishSeconds: null);

        Assert.Equal([(Cp(1), 100.0), (Cp(2), 300.0)], result.Splits);
    }

    /// <summary>
    /// A hole the total cannot solve (it would need a negative leg), so the rider is on the published
    /// clock — where a forward prefix running past the total is arithmetic, not a fact.
    /// </summary>
    [Fact]
    public void OnThePublishedClock_APrefixPastTheTotal_IsDiscarded()
    {
        var result = RaceLegAccumulator.Accumulate(
            [Leg(1, 5000), Leg(2, null), Leg(3, 300)], officialFinishSeconds: 600);

        Assert.DoesNotContain(result.Splits, s => s.CheckpointId == Cp(1));   // 5000 s > the 600 s total
        Assert.Equal(600, result.Splits.Single(s => s.CheckpointId == Cp(3)).ElapsedSeconds);
    }

    /// <summary>
    /// The Ski to Sea teleport bug, at its source. Two holes send the rider onto the published clock, and
    /// the backward pass subtracts the trailing legs from the total — but a cutoff team's legs sum PAST
    /// that total, so the subtraction lands on or before the gun. A checkpoint reached at t &lt;= 0 is not a
    /// fact, and it is a dangerous fiction: the replay reads it as an anchor and materialises the rider AT
    /// that checkpoint at the start of the race, then crawls him to the line.
    ///
    /// Eleven real teams did exactly this (bibs 37, 56, 64, 85, 271, 305, 361, 368, 421, 455, 458) — each
    /// landing on the last checkpoint before the Finish with an elapsed of exactly 0.
    /// </summary>
    [Fact]
    public void OnThePublishedClock_ABackwardPassBelowTheGun_IsDiscarded()
    {
        // Legs 2 and 3 alone (900 s) already exceed the 800 s published total, so chaining back from it
        // puts checkpoint 1 at -100 s and checkpoint 2 at exactly 0.
        var result = RaceLegAccumulator.Accumulate(
            [Leg(1, null), Leg(2, 100), Leg(3, 800)], officialFinishSeconds: 800);

        Assert.All(result.Splits, s => Assert.True(s.ElapsedSeconds > 0, $"split at {s.ElapsedSeconds}s"));
        Assert.DoesNotContain(result.Splits, s => s.CheckpointId == Cp(2));   // the 0 s anchor that teleported him
        Assert.Equal(800, result.Splits.Single().ElapsedSeconds);         // only the finish survives
    }

    [Fact]
    public void NoLegsAtAll_YieldsNothing() =>
        Assert.Empty(RaceLegAccumulator.Accumulate([], officialFinishSeconds: 600).Splits);

    /// <summary>
    /// A rider cannot reach a LATER checkpoint at an EARLIER time.
    ///
    /// The two-hole fallback runs a forward prefix (anchored at a true zero) and a backward chain
    /// (anchored on the official total). On a cutoff team — legs summing PAST the compressed official
    /// total, the 36-team case this class exists for — the two passes meet and CROSS, and nothing compared
    /// them: `e &lt;= 0` and `e &gt; cap` are the only guards, and neither rejects a split that merely goes
    /// backwards.
    ///
    /// Below: forward gives CP1=5000, CP2=8000. Backward from 9000 gives CP4=6000, CP5=7000, CP6=8000. So
    /// CP2 is at 8000 s and CP4 — two checkpoints FURTHER along the course — is at 6000 s.
    ///
    /// It shows up twice. The results table computes each leg as `end - start`, so it renders a leg of
    /// MINUS 2000 s and sorts it as the fastest in the field. And in the replay, anchorsFor drops the
    /// backwards splits, leaving two anchors at the same time — a zero-second span across four legs, which
    /// the pacing model resolves by TELEPORTING the rider across half the course.
    /// </summary>
    [Fact]
    public void WhenTheTwoClocksCross_SplitsNeverGoBackwards()
    {
        var result = RaceLegAccumulator.Accumulate(
            [
                Leg(1, 5000), Leg(2, 3000),      // forward prefix → 5000, 8000
                Leg(3, null), Leg(4, null),      // two holes → the fallback path
                Leg(5, 1000), Leg(6, 1000), Leg(7, 1000),
            ],
            officialFinishSeconds: 9000);        // the legs sum to 11 000 — a compressed, forced-start total

        var times = result.Splits.Select(s => s.ElapsedSeconds).ToList();

        Assert.Equal(times.OrderBy(t => t), times);   // strictly non-decreasing, in checkpoint order
        Assert.All(times, t => Assert.True(t > 0));
        Assert.DoesNotContain(result.Splits, s => s.ElapsedSeconds > 9000);
    }
}
