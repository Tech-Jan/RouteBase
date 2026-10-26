using RouteBaseWeb.Services.TrackEdit;
using Xunit;

namespace RouteBaseWeb.Tests.Helpers;

/// <summary>
/// An <see cref="ITrackDeriveQueue"/> that records what was enqueued instead of queueing it. The real
/// <see cref="RouteBaseWeb.Services.OperationQueue"/> exposes only counts and the next type, never the payload,
/// so "the recording's track id was queued for a match-only derive" is not assertable through it.
/// </summary>
public sealed class CapturingDeriveQueue : ITrackDeriveQueue
{
    public sealed record Call(IReadOnlyList<int> TrackIds, string UserId, DeriveReason Reason, bool FullDerive);

    public List<Call> Calls { get; } = new();

    /// <summary>The single match-only call, asserting exactly one happened.</summary>
    public Call SingleMatchOnly() => Assert.Single(Calls, c => !c.FullDerive);

    public void EnqueueMatchOnly(IReadOnlyList<int> trackIds, string userId, DeriveReason reason)
    {
        if (trackIds.Count == 0) return;   // mirrors the real seam: an empty set is not a call
        Calls.Add(new Call(trackIds, userId, reason, FullDerive: false));
    }

    public void EnqueueFullDerive(IReadOnlyList<int> trackIds, string userId, DeriveReason reason)
    {
        if (trackIds.Count == 0) return;
        Calls.Add(new Call(trackIds, userId, reason, FullDerive: true));
    }
}
