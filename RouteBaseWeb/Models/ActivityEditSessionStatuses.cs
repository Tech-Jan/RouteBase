namespace RouteBaseWeb.Models;

/// <summary>
/// The Activity_Edit_Session status vocabulary (D1). Server rows only ever hold
/// <see cref="Active"/> or <see cref="Saved"/> — a discard is a DELETE (FK ON DELETE SET NULL
/// releases the checkout), never a stored status. <see cref="Discarded"/> exists because it IS part
/// of the wire/TS contract: the frozen edit-ops union (<c>src/js/track-edit/edit-ops.ts</c>) is
/// <c>'active' | 'saved' | 'discarded'</c>, and the stamped enum must match it exactly.
/// </summary>
public static class ActivityEditSessionStatuses
{
    public const string Active    = "active";
    public const string Saved     = "saved";
    public const string Discarded = "discarded";
}
