using RouteBaseWeb.Services.Lifecycle;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Lifecycle;

/// <summary>
/// The lifecycle rules, pure. All six ordered state pairs are legal by design — restore must be able to return
/// a ride to whichever state it came from — so the table with teeth is the REASON table: a transition must
/// carry a reason that means something in the state it lands in.
/// </summary>
public class LifecycleTransitionsTests
{
    [Theory]
    [InlineData(LifecycleStates.InWork,   LifecycleStates.Released)]   // approve
    [InlineData(LifecycleStates.InWork,   LifecycleStates.Trashed)]    // reject
    [InlineData(LifecycleStates.Released, LifecycleStates.InWork)]     // park
    [InlineData(LifecycleStates.Released, LifecycleStates.Trashed)]    // delete / merge-consume
    [InlineData(LifecycleStates.Trashed,  LifecycleStates.Released)]   // restore a deleted ride
    [InlineData(LifecycleStates.Trashed,  LifecycleStates.InWork)]     // restore a rejected auto ride
    // "User" is a legal reason in every target state, so a null Validate result isolates the pair-legality
    // rule: any two distinct valid states are a legal transition.
    public void EveryOrderedPairOfDistinctStates_IsALegalTransition(string from, string to) =>
        Assert.Null(LifecycleTransitions.Validate(from, to, LifecycleReasons.User));

    [Theory]
    [InlineData(LifecycleStates.InWork)]
    [InlineData(LifecycleStates.Released)]
    [InlineData(LifecycleStates.Trashed)]
    public void SameToSame_IsNotATransition(string state)
    {
        Assert.Equal("notATransition", LifecycleTransitions.Validate(state, state, LifecycleReasons.User));
    }

    [Theory]
    [InlineData("confirmed", LifecycleStates.Released)]   // the old vocabulary is not a lifecycle state
    [InlineData("pending", LifecycleStates.Released)]
    [InlineData(null, LifecycleStates.Released)]
    public void UnknownFromState_IsRejected(string? from, string to) =>
        Assert.Equal("unknownFromState", LifecycleTransitions.Validate(from, to, LifecycleReasons.User));

    [Fact]
    public void UnknownToState_IsRejected() =>
        Assert.Equal("unknownToState",
            LifecycleTransitions.Validate(LifecycleStates.Released, "deleted", LifecycleReasons.User));

    [Fact]
    public void UnknownReason_IsRejected() =>
        Assert.Equal("unknownReason",
            LifecycleTransitions.Validate(LifecycleStates.Released, LifecycleStates.Trashed, "because"));

    // ---- the table that actually constrains anything ----

    [Theory]
    [InlineData(LifecycleStates.Trashed,  LifecycleReasons.User)]
    [InlineData(LifecycleStates.Trashed,  LifecycleReasons.EditMerge)]
    [InlineData(LifecycleStates.Trashed,  LifecycleReasons.ReviewRejected)]
    [InlineData(LifecycleStates.Trashed,  LifecycleReasons.Abandoned)]
    [InlineData(LifecycleStates.InWork,   LifecycleReasons.AwaitingReview)]
    [InlineData(LifecycleStates.InWork,   LifecycleReasons.Recording)]
    [InlineData(LifecycleStates.InWork,   LifecycleReasons.User)]
    [InlineData(LifecycleStates.Released, LifecycleReasons.User)]
    [InlineData(LifecycleStates.Released, LifecycleReasons.Recording)]
    public void ReasonThatExplainsTheTargetState_IsAllowed(string to, string reason) =>
        Assert.True(LifecycleTransitions.IsReasonAllowed(to, reason));

    [Theory]
    [InlineData(LifecycleStates.Trashed,  LifecycleReasons.Recording)]        // "trashed because it is recording"
    [InlineData(LifecycleStates.Trashed,  LifecycleReasons.AwaitingReview)]   // "trashed because it awaits review"
    [InlineData(LifecycleStates.Released, LifecycleReasons.ReviewRejected)]   // "live because it was rejected"
    [InlineData(LifecycleStates.Released, LifecycleReasons.EditMerge)]        // "live because a merge ate it"
    [InlineData(LifecycleStates.Released, LifecycleReasons.Abandoned)]
    [InlineData(LifecycleStates.InWork,   LifecycleReasons.EditMerge)]
    [InlineData(LifecycleStates.InWork,   LifecycleReasons.ReviewRejected)]
    public void ReasonThatContradictsTheTargetState_IsRejected(string to, string reason) =>
        Assert.False(LifecycleTransitions.IsReasonAllowed(to, reason));

    [Fact]
    public void Validate_SurfacesTheBadReason_OnAnOtherwiseLegalMove()
    {
        // released → trashed is legal; "recording" is not a thing a trashed ride can be.
        Assert.Equal("reasonNotAllowedForState", LifecycleTransitions.Validate(
            LifecycleStates.Released, LifecycleStates.Trashed, LifecycleReasons.Recording));

        Assert.Null(LifecycleTransitions.Validate(
            LifecycleStates.Released, LifecycleStates.Trashed, LifecycleReasons.User));
    }
}
