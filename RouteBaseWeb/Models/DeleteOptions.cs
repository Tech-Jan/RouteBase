namespace RouteBaseWeb.Models;

/// <summary>
/// Granular selection of which data groups to delete.
/// Used by DatabaseResetService.ResetDatabaseAsync(DeleteOptions).
/// </summary>
public class DeleteOptions
{
    /// <summary>All Activity rows — also forces GPS, GPSinSegment, Photos deletion.</summary>
    public bool Activities { get; set; }

    /// <summary>
    /// GPS rows (only meaningful when Activities is false). Also drops the GPSinSegment efforts
    /// derived from those points — they reference GPS_IDs that would otherwise dangle.
    /// </summary>
    public bool Gps { get; set; }

    /// <summary>Photos rows (+ their filesystem rendition files).</summary>
    public bool Photos { get; set; }

    /// <summary>Multi_Day_Tour rows (Activity.multi_day_ID gets SET NULL).</summary>
    public bool MultiDayTours { get; set; }

    /// <summary>GPSinSegment rows (segment match results only, keeps segments and activities).</summary>
    public bool SegmentMatches { get; set; }

    /// <summary>Segment + Segment_GPS rows — also forces GPSinSegment deletion.</summary>
    public bool Segments { get; set; }

    /// <summary>AppCredentials row with Type=StravaToken.</summary>
    public bool StravaToken { get; set; }

    /// <summary>AppCredentials row with Type=StravaCredentials (Client ID + Secret).</summary>
    public bool StravaApiKey { get; set; }

    /// <summary>AppCredentials row with Type=MapTilerCredentials.</summary>
    public bool MapTilerApiKey { get; set; }

    /// <summary>ProfileSettings rows.</summary>
    public bool ProfileSettings { get; set; }
}
