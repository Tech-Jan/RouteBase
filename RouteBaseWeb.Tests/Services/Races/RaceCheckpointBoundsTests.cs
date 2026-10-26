using RouteBaseWeb.Services.Races;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Races;

/// <summary>
/// Where a checkpoint may legally sit. Pure, so these run without EF, a track store or a race.
///
/// The rule these encode is the model this slice was rebuilt around: **the grid is the ride, the race is a
/// window on it.** A recorded activity usually holds more than the race, so both lines are placed by the
/// user anywhere in <c>[0, gridTotal]</c> — while staying the chainage extremes, which is what every other
/// invariant here is stated against.
/// </summary>
public class RaceCheckpointBoundsTests
{
    private const double Grid = 38_910;
    private const double Gap = RaceCheckpointBounds.MinGapMeters;

    [Fact]
    public void Finish_Reaches_TheEndOfTheRide()
    {
        var bounds = RaceCheckpointBounds.For(CheckpointRole.Finish, [0, 21_000], Grid);

        Assert.Equal(21_000 + Gap, bounds.Lo);
        Assert.Equal(Grid, bounds.Hi);
    }

    [Fact]
    public void Start_SitsAnywhereBeforeTheFirstCheckpoint()
    {
        var bounds = RaceCheckpointBounds.For(CheckpointRole.Start, [21_000, Grid], Grid);

        Assert.Equal(0, bounds.Lo);
        Assert.Equal(21_000 - Gap, bounds.Hi);
    }

    [Fact]
    public void Intermediate_IsHeldStrictlyInsideTheTwoLines()
    {
        var bounds = RaceCheckpointBounds.For(CheckpointRole.Intermediate, [1_200, 38_510], Grid);

        Assert.Equal(1_200 + Gap, bounds.Lo);
        Assert.Equal(38_510 - Gap, bounds.Hi);
    }

    [Fact]
    public void WithNothingElseOnTheCourse_TheWholeGridIsLegal()
    {
        var bounds = RaceCheckpointBounds.For(CheckpointRole.Finish, [], Grid);

        Assert.Equal(0, bounds.Lo);
        Assert.Equal(Grid, bounds.Hi);
    }

    [Theory]
    [InlineData(20_000, true)]   // clear
    [InlineData(1_200, false)]   // on top of the start line
    [InlineData(21_020, false)]  // inside another checkpoint's gap
    [InlineData(600, false)]     // in the roll-out — ride, not race
    [InlineData(38_800, false)]  // in the cooldown
    [InlineData(-10, false)]
    public void IsLegal_Intermediate(double proposed, bool expected) =>
        Assert.Equal(
            expected,
            RaceCheckpointBounds.IsLegal(proposed, CheckpointRole.Intermediate, [1_200, 21_000, 38_510], Grid));

    [Fact]
    public void IsLegal_TheFinishMayEnterTheCooldown_WhereAnIntermediateMayNot()
    {
        double[] others = [1_200, 21_000];

        Assert.True(RaceCheckpointBounds.IsLegal(38_800, CheckpointRole.Finish, others, Grid));
        Assert.False(RaceCheckpointBounds.IsLegal(Grid + 1, CheckpointRole.Finish, others, Grid));
    }

    /// <summary>
    /// NaN satisfies no comparison, so a range check alone cannot reject it. Unrejected it sorts as the
    /// MINIMUM (.NET orders NaN below every number), takes Start's place in the seq renumbering, and then
    /// kills the whole replay response — System.Text.Json cannot write one.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void IsLegal_RejectsNonFinite(double proposed) =>
        Assert.False(RaceCheckpointBounds.IsLegal(proposed, CheckpointRole.Intermediate, [0, Grid], Grid));
}
