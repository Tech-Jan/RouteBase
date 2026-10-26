namespace RouteBaseWeb.Services.Races;

/// <summary>Where a checkpoint sits in the order — the three cases have different legal ranges.</summary>
public enum CheckpointRole
{
    /// <summary>The lowest chainage: the start line. Free to sit anywhere before the first checkpoint.</summary>
    Start,
    Intermediate,
    /// <summary>The highest chainage: the finish line. Free to sit anywhere after the last checkpoint.</summary>
    Finish,
}

/// <summary>The legal chainage range for one checkpoint, inclusive at both ends.</summary>
public readonly record struct ChainageBounds(double Lo, double Hi)
{
    public bool Contains(double chainage) => chainage >= Lo && chainage <= Hi;
}

/// <summary>
/// Where a checkpoint may legally sit. Pure — no EF, no entities — so the rule that is easy to get wrong
/// is unit-testable, and so the API layer and the CRUD cannot drift apart about it.
///
/// The model: **the grid is the ride, the race is a window on it.** A recorded activity usually holds more
/// than the race — the roll-out to the line, the cooldown past it, a wrong turn at the end — so Start and
/// Finish are placed BY THE USER anywhere in <c>[0, gridTotal]</c>, not pinned to the ride's ends. They stay
/// the chainage extremes (moving one past its neighbour would silently reassign the role), which is the
/// invariant every other rule in this slice is stated against.
///
/// Chainage is metres along <see cref="RaceCourseGrid"/> and nothing else. It is never
/// <c>Activity.TotalDistance</c> — that is the source's own scalar (Strava's, on an import) and runs ~0.5%
/// short of the raw geometry, which is the mismatch that once left a 70 km course's last 337 m undrawn.
/// </summary>
public static class RaceCheckpointBounds
{
    /// <summary>
    /// How close two checkpoints may get. Two at the SAME chainage give the pacing model a zero-length
    /// interval to normalize — a divide-by-zero — and this is the server-side authority for the rule; the
    /// client's matching constant in <c>race-clock.ts</c> (snapCheckpoint) only makes the gesture feel right.
    /// </summary>
    public const double MinGapMeters = 50;

    /// <summary>
    /// The range <paramref name="role"/> may move within, given every OTHER checkpoint's chainage and the
    /// course grid's length. An empty <paramref name="otherChainages"/> means the whole grid.
    /// </summary>
    public static ChainageBounds For(CheckpointRole role, IReadOnlyList<double> otherChainages, double gridTotalMeters)
    {
        if (otherChainages.Count == 0) return new ChainageBounds(0, gridTotalMeters);

        return role switch
        {
            CheckpointRole.Start => new ChainageBounds(0, otherChainages.Min() - MinGapMeters),
            CheckpointRole.Finish => new ChainageBounds(otherChainages.Max() + MinGapMeters, gridTotalMeters),
            // An intermediate may cross its neighbours — a drag past one reorders, and seq follows (see
            // RenumberAndSaveAsync). Only the endpoints bound it; the gap check below keeps it off the others.
            _ => new ChainageBounds(otherChainages.Min() + MinGapMeters, otherChainages.Max() - MinGapMeters),
        };
    }

    /// <summary>
    /// May this checkpoint move to <paramref name="proposed"/>? In range, finite, and not on top of another.
    ///
    /// Finiteness is tested FIRST and on its own: every comparison with NaN is false, so a NaN satisfies no
    /// bound and would fall through a range check into the row — where it sorts as the MINIMUM (.NET orders
    /// NaN below every number), takes Start's place in the seq renumbering, and finally kills the whole
    /// replay response, because System.Text.Json cannot write one.
    /// </summary>
    public static bool IsLegal(
        double proposed, CheckpointRole role, IReadOnlyList<double> otherChainages, double gridTotalMeters)
    {
        if (!double.IsFinite(proposed)) return false;
        if (!For(role, otherChainages, gridTotalMeters).Contains(proposed)) return false;
        return otherChainages.All(other => Math.Abs(other - proposed) >= MinGapMeters);
    }
}
