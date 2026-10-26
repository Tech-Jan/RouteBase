using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Races;

/// <summary>One leg's duration, and the checkpoint it ENDS at. Null when the file has no time for it.</summary>
public readonly record struct RaceLeg(CheckpointId CheckpointId, double? Seconds);

/// <summary>
/// What the accumulator made of one rider's legs. <see cref="OverlapsOfficialTotal"/> is reported, not
/// acted on — see the class docs for why the leg times win.
/// </summary>
public sealed record RaceLegAccumulation(
    IReadOnlyList<(CheckpointId CheckpointId, double ElapsedSeconds)> Splits,
    bool OverlapsOfficialTotal);

/// <summary>
/// Turns LEG DURATIONS into elapsed-from-start times.
///
/// Relay and stage results publish per-leg times ("21:09.3, 20:55.2, 37:12.7 …"), but
/// <see cref="Models.RaceSplitTime.ElapsedSeconds"/> means seconds from the race start. So the legs are
/// chained: checkpoint k is the sum of legs 1..k. **The leg sum is the finish time.**
///
/// It will not always match the results file's own total, and that is expected. On Ski to Sea 2025
/// (508 teams) 36 teams' legs sum to MORE than their official
/// finish — by up to 2.3 h — and never to less. That is the race's cutoff rule: when a leg racer misses
/// the cutoff the next racer is started anyway, so two legs run concurrently and the official clock is
/// COMPRESSED by the overlap.
///
/// Two different clocks, then:
///
///   official   = wall-clock, squeezed by the organisers' forced starts
///   leg sum    = what the team would have taken had every racer waited for the handoff
///
/// The replay uses the LEG SUM. It is each athlete's real effort chained end to end, which is what a
/// replay is for; the official total is an artefact of race logistics. Those 36 teams simply finish
/// later here than the published results say — slower, and truer. Nothing is discarded to protect a
/// number that was compressed for organisational reasons.
///
/// The official total is therefore not an authority. It is used only where the legs cannot answer:
/// as the anchor that places a rider whose legs have holes in them.
///
/// Pure. No DB, no CSV — the rule is testable on numbers alone.
/// </summary>
public static class RaceLegAccumulator
{
    /// <summary>Beyond this, the legs and the official total tell different stories. Seconds. Reported only.</summary>
    public const double DefaultToleranceSeconds = 60;

    /// <summary>
    /// Elapsed-from-start for each checkpoint we can place.
    ///
    /// <paramref name="legs"/> is every checkpoint AFTER the start, in seq order — the start is implicit
    /// at t=0 and never carries a leg. <paramref name="officialFinishSeconds"/> is the results file's own
    /// total; optional, and only consulted when a leg is missing.
    /// </summary>
    public static RaceLegAccumulation Accumulate(
        IReadOnlyList<RaceLeg> legs,
        double? officialFinishSeconds,
        double toleranceSeconds = DefaultToleranceSeconds)
    {
        if (legs.Count == 0) return new RaceLegAccumulation([], false);

        var seconds = legs.Select(l => l.Seconds).ToArray();

        // One hole against a known total is one equation: the missing leg IS total - sum(the rest).
        // Solve it, and the rider rejoins the clean path below with a full set of legs.
        var holes = seconds.Count(s => s is null);
        if (holes == 1 && officialFinishSeconds is double totalForHole)
        {
            var missing = totalForHole - seconds.Sum(s => s ?? 0);
            if (missing > 0) seconds[Array.FindIndex(seconds, s => s is null)] = missing;
        }

        return seconds.All(s => s is not null)
            ? ChainLegs(legs, seconds.OfType<double>().ToArray(), officialFinishSeconds, toleranceSeconds)
            : AnchorOnOfficialTotal(legs, seconds, officialFinishSeconds);
    }

    /// <summary>
    /// The normal path: every leg known, so elapsed is just the running sum and the finish is the total
    /// of the legs. The official number is compared but NEVER substituted — see the class docs.
    /// </summary>
    private static RaceLegAccumulation ChainLegs(
        IReadOnlyList<RaceLeg> legs, double[] seconds, double? official, double tolerance)
    {
        var splits = new List<(CheckpointId, double)>(legs.Count);
        double running = 0;
        for (var i = 0; i < legs.Count; i++)
        {
            running += seconds[i];
            splits.Add((legs[i].CheckpointId, running));
        }

        var overlaps = official is double t && Math.Abs(running - t) > tolerance;
        return new RaceLegAccumulation(splits, overlaps);
    }

    /// <summary>
    /// The fallback: TWO or more holes, so the legs cannot produce a finish time at all and there is no
    /// leg-sum clock to be had. The official total is then the only anchor available, so this rider is
    /// placed on the official (compressed) clock instead — forward from the start, backward from the
    /// total, and whatever sits between two consecutive holes is left unplaced rather than invented.
    ///
    /// Mixing clocks is not ideal. Having no clock is worse: without this, a team missing its first two
    /// legs has no forward prefix and would vanish from the field entirely.
    /// </summary>
    private static RaceLegAccumulation AnchorOnOfficialTotal(
        IReadOnlyList<RaceLeg> legs, double?[] seconds, double? official)
    {
        var elapsed = new double?[legs.Count];

        double running = 0;
        for (var i = 0; i < legs.Count; i++)
        {
            if (seconds[i] is not double s) break;
            running += s;
            elapsed[i] = running;
        }

        if (official is double total)
        {
            elapsed[^1] = total;
            var back = total;
            for (var i = legs.Count - 1; i > 0; i--)
            {
                if (seconds[i] is not double s) break;
                back -= s;
                elapsed[i - 1] ??= back;   // forward wins where both know: it is anchored at a true zero
            }
        }

        var splits = new List<(CheckpointId, double)>(legs.Count);
        // The last time we have actually PLACED. The gun is the floor, and it only ever moves forward.
        var placed = 0.0;
        for (var i = 0; i < legs.Count; i++)
        {
            if (elapsed[i] is not double e) continue;
            // On the official clock the total is the ceiling: a forward prefix that runs past it is
            // arithmetic, not a fact.
            if (official is double cap && i < legs.Count - 1 && e > cap) continue;
            // ...and zero is the floor. The backward pass subtracts the trailing legs from the total, so a
            // team whose legs sum PAST that total (a forced start ran two legs at once — the whole reason
            // the two clocks disagree) lands on or before the gun. A checkpoint reached at t<=0 is the same
            // kind of arithmetic artefact as one past the total, and it is worse: the replay reads it as a
            // real anchor and teleports the rider to that checkpoint at the start of the race.
            if (e <= 0) continue;
            // ...and TIME IS THE FLOOR TOO. The forward prefix is anchored at a true zero and the backward
            // chain at the compressed official total, so on exactly those cutoff teams the two passes MEET
            // AND CROSS: the backward values land BEFORE where the forward pass already got to, and a rider
            // reaches a later checkpoint at an earlier time. `e <= 0` and `e > cap` cannot see it — nothing
            // compared the two passes to each other.
            //
            // It surfaces twice: the results table shows the leg as `end - start` and renders a NEGATIVE
            // duration (sorting as the fastest leg in the field), and the replay, having dropped the
            // backwards splits, is left with two anchors at the same time — a zero-second span that the
            // pacing model resolves by teleporting the rider across half the course.
            //
            // Forward wins, because it alone is anchored at a time we know. A contradicting backward value
            // is left UNPLACED rather than invented — which is what this method already does with a gap.
            if (e <= placed) continue;
            splits.Add((legs[i].CheckpointId, e));
            placed = e;
        }
        return new RaceLegAccumulation(splits, false);
    }
}
