using RouteBaseWeb.Models.Recording;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace RouteBaseWeb.Hubs;

/// <summary>
/// SignalR hub for live GPS recording sessions.
/// Viewers join a session group to receive real-time GPS point updates.
/// Fully implemented in Phase 2 (Step 2.1).
/// </summary>
[Authorize]
public class RecordingHub : Hub
{
    private readonly Services.Interfaces.IRecordingService _recordingService;
    private readonly Services.Interfaces.IVisibilityService _visibilityService;
    private readonly Services.Interfaces.ILiveViewerRegistry _viewers;

    public RecordingHub(
        Services.Interfaces.IRecordingService recordingService,
        Services.Interfaces.IVisibilityService visibilityService,
        Services.Interfaces.ILiveViewerRegistry viewers)
    {
        _recordingService = recordingService;
        _visibilityService = visibilityService;
        _viewers = viewers;
    }

    /// <summary>
    /// The SignalR group carrying one recording session's live traffic. The single construction site —
    /// the name used to be assembled from a string fragment in seven places across two files, where a
    /// typo in one would have been a silently dead broadcast that no grep could find (LV-09).
    /// </summary>
    public static string GroupFor(string sessionId) => $"recording-{sessionId}";

    /// <summary>
    /// Join the group for a recording session to receive live GPS updates. Returns an explicit result
    /// (PI-08): a refused join used to just return, which the client could not tell apart from "joined
    /// fine, the rider has no GPS fix yet" — the viewer sat there silently instead of being told why.
    /// </summary>
    public async Task<JoinSessionResult> JoinSession(string sessionId)
    {
        var session = await _recordingService.GetSessionAsync(sessionId);
        if (session is null) return new JoinSessionResult(false, JoinSessionResult.NotFound);

        if (!await _visibilityService.CanViewItemAsync(session.UserId, session.Visibility))
            return new JoinSessionResult(false, JoinSessionResult.Forbidden);

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(sessionId));
        // Registered after the visibility check, never before: an unauthorised viewer must not count as
        // watching — that would hold the phone at the fast upload cadence for a session it cannot even see.
        _viewers.Join(sessionId, Context.ConnectionId);

        // CONC-P2: this method suspended at the awaits above, so the connection may have dropped while we
        // ran. If it did, OnDisconnectedAsync's LeaveAll already fired and found nothing registered (we
        // hadn't joined yet), so the Join above just added a DEAD connection id that no disconnect will
        // ever clear — the session would read as watched forever, pinning the recording phone at the fast
        // cadence (battery). Re-check the abort after joining and undo our own registration; the result is
        // moot (the client is gone), but the registry must not keep a dead viewer.
        if (Context.ConnectionAborted.IsCancellationRequested)
        {
            _viewers.Leave(sessionId, Context.ConnectionId);
            return new JoinSessionResult(false, JoinSessionResult.NotFound);
        }
        return JoinSessionResult.Joined;
    }

    /// <summary>
    /// A viewer never leaves a session explicitly — a closed tab or dropped connection just goes away, so
    /// this disconnect hook is the only path that unregisters it. SignalR forgets the group membership on
    /// its own, but the viewer registry is ours: a connection left behind in it would report the session as
    /// watched forever, pinning the recording phone at the 5 s upload cadence for the rest of the ride.
    /// (There was an unused <c>LeaveSession</c> RPC here; no client ever invoked it and this hook already
    /// does the exact cleanup, so it was removed — DC29-R2.)
    /// </summary>
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _viewers.LeaveAll(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
