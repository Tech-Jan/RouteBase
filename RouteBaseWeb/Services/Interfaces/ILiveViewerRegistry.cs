namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Who is currently watching which live recording session, by SignalR connection.
///
/// The phone polls this through the append response (<c>AppendPointsResponse.Watched</c>) and slows its
/// upload cadence right down when nobody is watching — an unwatched auto-recording is the common case, and
/// a 5 s POST cadence keeps the LTE modem's tail timer alive for the entire ride. A viewer that is counted
/// but gone would pin the phone at the fast cadence forever, so the disconnect path (not just an explicit
/// leave) must remove the connection.
/// </summary>
public interface ILiveViewerRegistry
{
    /// <summary>Count <paramref name="connectionId"/> as watching <paramref name="sessionId"/>.</summary>
    void Join(string sessionId, string connectionId);

    /// <summary>Stop counting <paramref name="connectionId"/> as watching <paramref name="sessionId"/>.</summary>
    void Leave(string sessionId, string connectionId);

    /// <summary>Remove <paramref name="connectionId"/> from every session it was watching (hub disconnect).</summary>
    void LeaveAll(string connectionId);

    /// <summary>
    /// Record that an anonymous (share-link) viewer polled <paramref name="sessionId"/> just now. Such a
    /// viewer holds no SignalR connection, so this time-stamped heartbeat is how it registers as watching —
    /// keeping the phone at its fast upload cadence while a public link is open, without ever pinning it
    /// there after the viewer leaves.
    /// </summary>
    void TouchPublic(string sessionId);

    /// <summary>True while a SignalR connection is joined to <paramref name="sessionId"/>, OR an anonymous
    /// viewer polled it within the recent window.</summary>
    bool IsWatched(string sessionId);
}
