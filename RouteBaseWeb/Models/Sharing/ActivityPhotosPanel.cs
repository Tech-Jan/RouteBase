namespace RouteBaseWeb.Models.Sharing;

/// <summary>
/// Model for the Details photos panel. Was a bare <c>int</c> track id; it grew a capability flag because
/// the "Add photos" link targets an owner-only import route and had no gate at all — a non-owner (and now
/// a share viewer) was being offered an action that would fail.
/// </summary>
public sealed record ActivityPhotosPanel(int TrackId, bool CanAdd);
