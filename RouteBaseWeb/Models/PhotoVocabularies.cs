namespace RouteBaseWeb.Models;

/// <summary>How a photo landed on its activity: single clear match vs user pick (D2).</summary>
public static class PhotoAssignments
{
    public const string Auto   = "auto";
    public const string Manual = "manual";
}

/// <summary>
/// How <see cref="Photo.Lat"/>/<see cref="Photo.Lng"/> were derived (D2). PhotoMatchService
/// produces exif/interpolated/none; manual placement writes manual. On the wire
/// (PhotoDto/PhotoMarkerDto.locationSource, stamped) — drives the placement/unpin UI.
/// </summary>
public static class PhotoLocationSources
{
    public const string Exif         = "exif";
    public const string Interpolated = "interpolated";
    public const string Manual       = "manual";
    public const string None         = "none";
}

/// <summary>Where the photo's rendition files live. Only local FS today (D2).</summary>
public static class PhotoStorages
{
    public const string Local = "local";
}
