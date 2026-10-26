namespace RouteBaseWeb.Models.Recording;

/// <summary>
/// What <c>RecordingHub.JoinSession</c> told the caller — explicit, not a silent no-op (PI-08). Before
/// this existed, a refused join (session gone, or visibility revoked) looked on the client exactly like
/// "joined fine, the rider just has no GPS fix yet" — the viewer had no way to tell the two apart.
/// TS mirror: <c>JoinSessionResult</c> (live-recording-shared.ts).
/// </summary>
public sealed record JoinSessionResult(bool Ok, string? Reason)
{
    public const string NotFound = "not_found";
    public const string Forbidden = "forbidden";

    public static readonly JoinSessionResult Joined = new(true, null);
}
