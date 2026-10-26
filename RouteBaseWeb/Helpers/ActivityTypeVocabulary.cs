namespace RouteBaseWeb.Helpers;

/// <summary>
/// Canonical picker vocabulary for <c>Activity.ActivityType</c> / <c>Segment.ActivityType</c>.
/// Deliberately separate from <see cref="ActivityTypeHelper"/>: that class classifies arbitrary
/// free-text (imports, Strava, device files) into coarse groups; this class enumerates the closed
/// sets of values the app's own &lt;select&gt; pickers offer. The field itself stays an open
/// <c>string</c> (import paths pass through unknown values verbatim) — this is not an enum.
/// </summary>
public static class ActivityTypeVocabulary
{
    public const string Run = "Run";
    public const string Ride = "Ride";
    public const string Walk = "Walk";
    public const string Hike = "Hike";
    public const string AlpineSki = "AlpineSki";
    public const string NordicSki = "NordicSki";
    public const string Other = "Other";
    public const string EBikeRide = "EBikeRide";
    public const string VirtualRide = "VirtualRide";
    public const string VirtualRun = "VirtualRun";
    public const string Swim = "Swim";
    public const string Workout = "Workout";
    public const string WeightTraining = "WeightTraining";
    public const string Yoga = "Yoga";
    public const string Skiing = "Skiing";
    public const string Snowboard = "Snowboard";
    public const string Kayaking = "Kayaking";
    public const string Rowing = "Rowing";

    /// <summary>
    /// Live-recording / pending-review picker set. Order is the cross-repo contract with
    /// GpsRecorder's <c>res/values/strings.xml</c> <c>activity_types</c> string-array (AU-09: "do
    /// not localize or reorder — the app sends the label, not the position"). Pinned by
    /// <c>ActivityTypeVocabularyTests.Recordable_MatchesAndroidStringsXml</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> Recordable =
        [Run, Ride, Walk, Hike, AlpineSki, NordicSki, Other];

    /// <summary>
    /// Activity-edit picker set (Details.cshtml). Curated, NOT exhaustive — DC-06 keeps an
    /// out-of-list stored value selectable, so this list is not a closed constraint on the field.
    /// </summary>
    public static readonly IReadOnlyList<string> EditableKnown =
        [Run, Ride, Walk, Hike, VirtualRide, VirtualRun, Swim, Workout, WeightTraining, Yoga, Skiing, Snowboard, Kayaking, Rowing, EBikeRide];

    /// <summary>Segment-create route-picker filter set (Segments/Create.cshtml).</summary>
    public static readonly IReadOnlyList<string> SegmentFilter =
        [Run, Ride, Walk, Hike, EBikeRide];

    private static readonly IReadOnlyDictionary<string, string> DisplayLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [AlpineSki] = "Alpine Ski",
        [NordicSki] = "Nordic Ski",
    };

    /// <summary>Friendly multi-word label for a canonical value; defaults to the value itself.</summary>
    public static string DisplayLabel(string value) =>
        DisplayLabels.TryGetValue(value, out var label) ? label : value;
}
