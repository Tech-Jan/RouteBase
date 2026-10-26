using RouteBaseWeb.Helpers.Grid;

namespace RouteBaseWeb.Models.GridFilter;

/// <summary>
/// The activities-grid column registry: the single declaration of which <see cref="Activity"/> columns are
/// filterable and/or sortable, the data type of each (which derives the filter control), and any display→storage
/// unit scale. Both the filter predicates and the sort order are dispatched from here, so adding a column means
/// adding one entry — no bespoke per-column plumbing, and unknown keys can never reach a property.
///
/// Units: distance is entered in km (×1000 → metres), speed in km/h (÷3.6 → m/s), durations in minutes
/// (×60 → seconds); heart rate/cadence/power/ascent/descent are entered in their stored unit.
/// </summary>
public sealed class ActivityGridColumns : GridColumnRegistry<Activity>
{
    /// <summary>Minutes→seconds scale for the duration columns (stored as seconds, filtered in minutes).</summary>
    private const double MinutesToSeconds = 60.0;

    /// <summary>km/h→m/s scale for the speed columns (stored as m/s, filtered in km/h).</summary>
    private const double KmhToMs = 1.0 / 3.6;

    /// <summary>km→m scale for the distance column (stored as metres, filtered in km).</summary>
    private const double KmToM = 1000.0;

    public static readonly ActivityGridColumns Instance = new();

    private ActivityGridColumns() : base(BuildColumns(), defaultSortKey: "date") { }

    private static IEnumerable<GridColumn<Activity>> BuildColumns() => new GridColumn<Activity>[]
    {
        new DateColumn<Activity>("date", a => a.StartDateTimeLocal),
        new TextColumn<Activity>("name", a => a.TrackName),
        new ComboColumn<Activity>("type", a => a.ActivityType),
        new MultiSelectColumn<Activity>("visibility", a => a.Visibility),
        new TextColumn<Activity>("owner", a => a.UserId, filterable: false), // sort only

        new NumberColumn<Activity>("distance", a => a.TotalDistance, scale: KmToM),
        new NumberColumn<Activity>("ascent", a => a.TotalAscent),
        new NumberColumn<Activity>("descent", a => a.TotalDescent),
        new NumberColumn<Activity>("time", a => a.TimeTotal, scale: MinutesToSeconds),
        new NumberColumn<Activity>("movingtime", a => a.TimeMoving, scale: MinutesToSeconds),
        new NumberColumn<Activity>("avgspeed", a => a.SpeedAvg, scale: KmhToMs),
        new NumberColumn<Activity>("maxspeed", a => a.SpeedMax, scale: KmhToMs),
        new NumberColumn<Activity>("avghr", a => a.HeartRateAvg),
        new NumberColumn<Activity>("maxhr", a => a.HeartRateMax),
        new NumberColumn<Activity>("avgcadence", a => a.CadenceAvg),
        new NumberColumn<Activity>("avgpower", a => a.PowerAvg),

        // Photos feature seam: count of assigned photos, over the Activity.Photos navigation. Sort/filter
        // become an indexed COUNT subquery (no denormalised counter). The raw-SQL twin (for the paint/adopt
        // filter + segment matching) is the same COUNT, qualified with the candidate query's `a` alias, so
        // photos filters identically in both engines. Display value is batched separately.
        new CountColumn<Activity>("photos", a => a.Photos.Count,
            "(SELECT COUNT(*) FROM Photos ph WHERE ph.track_id = a.track_ID)"),
    };
}
