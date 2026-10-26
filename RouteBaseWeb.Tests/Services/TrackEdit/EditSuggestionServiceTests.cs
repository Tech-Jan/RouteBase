using RouteBaseWeb.Models;
using RouteBaseWeb.Services.TrackEdit;
using Xunit;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Tests.Services.TrackEdit;

/// <summary>
/// Add-track suggestions + search (A4 §3.3): proximity bbox+haversine, same-day, merge/order/cap, the
/// server-side loaded/foreign/non-confirmed exclusions, and name/date search. Real DB + services over the
/// shared in-memory harness. Suggestion candidates are scalar-only Activity rows (no blob); the session's
/// slot-0 track carries the only blob (needed to open the session).
/// </summary>
public class EditSuggestionServiceTests
{
    private const string Me = "me";
    private const string Other = "other";
    private const double Lat = 47.0;
    private const double Lng = 8.0;
    private const string Day = "2025-06-15";

    private static double LatFor(double meters) => Lat + meters / 111_320.0;
    private static DateTime OnDay(int hour = 12) => new(2025, 6, 15, hour, 0, 0, DateTimeKind.Unspecified);

    private static async Task<(TrackEditTestDb Db, string SessionId, EditSuggestionService Svc)> SessionAsync(
        params string[] users)
    {
        var owner = users.Length > 0 ? users[0] : Me;
        var db = await TrackEditTestDb.CreateAsync(users.Length > 0 ? users : new[] { Me });
        await db.AddActivityAsync(new TrackId(1), owner);   // slot-0 track: no GPS/date scalars → never self-suggests
        await db.SeedRampAsync(new TrackId(1), 5);
        var created = await db.ServiceFor(owner).CreateAsync(new CreateSessionRequest([1]));
        return (db, created.Response!.SessionId, db.SuggestionServiceFor(owner));
    }

    [Fact]
    public async Task Proximity_WithinRadius_ReturnedSortedByGap()
    {
        var (db, id, svc) = await SessionAsync();
        await using var _ = db;
        await db.AddSuggestibleAsync(new TrackId(11), Me, startLat: LatFor(800), startLng: Lng);
        await db.AddSuggestibleAsync(new TrackId(10), Me, startLat: LatFor(200), startLng: Lng);
        await db.AddSuggestibleAsync(new TrackId(12), Me, startLat: LatFor(1500), startLng: Lng); // outside 1 km bbox

        var r = (await svc.GetSuggestionsAsync(id, Lat, Lng, null))!;

        Assert.Equal(new[] { 10, 11 }, r.Suggestions.Select(s => s.TrackId)); // nearer first
        Assert.All(r.Suggestions, s => Assert.True(s.NearEnd && s.StartGapMeters <= 1000));
        Assert.True(r.Suggestions[0].StartGapMeters < r.Suggestions[1].StartGapMeters);
    }

    [Fact]
    public async Task Proximity_BboxLongitudeScaling_AtHighLatitude()
    {
        var (db, id, svc) = await SessionAsync();
        await using var _ = db;
        // ~900 m due-east at lat 60°: 1° lng ≈ 55.7 km, so 0.01617° ≈ 900 m — only the cos-scaled dLng finds it.
        await db.AddSuggestibleAsync(new TrackId(20), Me, startLat: 60.0, startLng: 10.0 + 0.01617);

        var r = (await svc.GetSuggestionsAsync(id, 60.0, 10.0, null))!;

        var hit = Assert.Single(r.Suggestions);
        Assert.Equal(20, hit.TrackId);
        Assert.True(hit.StartGapMeters is > 700 and < 1000);
    }

    [Fact]
    public async Task SameDay_LocalDateBoundaries()
    {
        var (db, id, svc) = await SessionAsync();
        await using var _ = db;
        await db.AddSuggestibleAsync(new TrackId(20), Me, startLocal: new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Unspecified));   // in (00:00)
        await db.AddSuggestibleAsync(new TrackId(21), Me, startLocal: new DateTime(2025, 6, 15, 23, 59, 0, DateTimeKind.Unspecified)); // in (23:59)
        await db.AddSuggestibleAsync(new TrackId(22), Me, startLocal: new DateTime(2025, 6, 16, 0, 0, 0, DateTimeKind.Unspecified));   // out (next day)

        var r = (await svc.GetSuggestionsAsync(id, null, null, Day))!;

        Assert.Equal(new[] { 20, 21 }, r.Suggestions.Select(s => s.TrackId).OrderBy(x => x));
        Assert.All(r.Suggestions, s => Assert.True(s.SameDay && !s.NearEnd));
    }

    [Fact]
    public async Task MergedCandidate_CarriesBothReasonFlags()
    {
        var (db, id, svc) = await SessionAsync();
        await using var _ = db;
        await db.AddSuggestibleAsync(new TrackId(30), Me, startLat: LatFor(200), startLng: Lng, startLocal: OnDay());

        var r = (await svc.GetSuggestionsAsync(id, Lat, Lng, Day))!;

        var hit = Assert.Single(r.Suggestions);
        Assert.True(hit.NearEnd);
        Assert.True(hit.SameDay);
        Assert.NotNull(hit.StartGapMeters);
    }

    [Fact]
    public async Task ExcludesSessionLoadedActivities()
    {
        var (db, id, svc) = await SessionAsync();
        await using var _ = db;
        // Activity 2 is loadable (blob) AND near/same-day, but becomes part of the session script → excluded.
        await db.AddActivityAsync(new TrackId(2), Me);
        await db.SeedRampAsync(new TrackId(2), 5);
        var loaded = await db.Context.Activities.FindAsync(new TrackId(2));
        loaded!.StartGpsLat = LatFor(200); loaded.StartGpsLong = Lng; loaded.StartDateTimeLocal = OnDay();
        await db.Context.SaveChangesAsync();
        await db.ServiceFor(Me).AppendOpsAsync(id, new AppendOpsRequest(1, [new LoadActivityOp(2)]));

        var r = (await svc.GetSuggestionsAsync(id, Lat, Lng, Day))!;

        Assert.DoesNotContain(r.Suggestions, s => s.TrackId == 2);
    }

    [Fact]
    public async Task ExcludesForeignAndNonConfirmed()
    {
        var (db, id, svc) = await SessionAsync(Me, Other);
        await using var _ = db;
        await db.AddSuggestibleAsync(new TrackId(40), Other, startLat: LatFor(200), startLng: Lng, startLocal: OnDay()); // foreign
        await db.AddSuggestibleAsync(new TrackId(41), Me, lifecycle: LifecycleStates.InWork, startLat: LatFor(200), startLng: Lng, startLocal: OnDay());
        await db.AddSuggestibleAsync(new TrackId(42), Me, lifecycle: LifecycleStates.Trashed, startLat: LatFor(200), startLng: Lng, startLocal: OnDay());

        var r = (await svc.GetSuggestionsAsync(id, Lat, Lng, Day))!;

        Assert.Empty(r.Suggestions);
    }

    [Fact]
    public async Task NoGpsStart_ExcludedFromProximity_KeptForSameDay()
    {
        var (db, id, svc) = await SessionAsync();
        await using var _ = db;
        await db.AddSuggestibleAsync(new TrackId(50), Me, startLat: null, startLng: null, startLocal: OnDay());

        var both = (await svc.GetSuggestionsAsync(id, Lat, Lng, Day))!;
        var hit = Assert.Single(both.Suggestions);
        Assert.Equal(50, hit.TrackId);
        Assert.True(hit.SameDay);
        Assert.False(hit.NearEnd);
        Assert.Null(hit.StartGapMeters);

        var proximityOnly = (await svc.GetSuggestionsAsync(id, Lat, Lng, null))!;
        Assert.Empty(proximityOnly.Suggestions);
    }

    [Fact]
    public async Task SuggestionCap_Limits20_ProximityFirst()
    {
        var (db, id, svc) = await SessionAsync();
        await using var _ = db;
        for (int i = 0; i < 20; i++) await db.AddSuggestibleAsync(new TrackId(100 + i), Me, startLat: LatFor(100 + i * 10), startLng: Lng);
        for (int i = 0; i < 5; i++) await db.AddSuggestibleAsync(new TrackId(200 + i), Me, startLocal: OnDay()); // same-day only

        var r = (await svc.GetSuggestionsAsync(id, Lat, Lng, Day))!;

        Assert.Equal(20, r.Suggestions.Count);
        Assert.All(r.Suggestions, s => Assert.True(s.NearEnd)); // proximity fills the cap before same-day-only
    }

    [Fact]
    public async Task Search_NameSubstring_CaseInsensitive_FlagsLoaded_WildcardLiteral()
    {
        var (db, id, svc) = await SessionAsync();
        await using var _ = db;
        var slot0 = await db.Context.Activities.FindAsync(new TrackId(1));
        slot0!.TrackName = "Ride One"; await db.Context.SaveChangesAsync(); // loaded + matches "ride"
        await db.AddSuggestibleAsync(new TrackId(40), Me, name: "Evening RIDE");
        await db.AddSuggestibleAsync(new TrackId(41), Me, name: "Swim");
        await db.AddSuggestibleAsync(new TrackId(42), Me, name: "Big%Match");
        await db.AddSuggestibleAsync(new TrackId(43), Me, name: "BigXMatch");

        var byName = (await svc.SearchAsync(id, "ride", null))!;
        Assert.Equal(new[] { 1, 40 }, byName.Suggestions.Select(s => s.TrackId).OrderBy(x => x));
        Assert.True(byName.Suggestions.Single(s => s.TrackId == 1).AlreadyLoaded);
        Assert.False(byName.Suggestions.Single(s => s.TrackId == 40).AlreadyLoaded);

        var literal = (await svc.SearchAsync(id, "big%", null))!; // % escaped → literal, not a wildcard
        Assert.Equal(new[] { 42 }, literal.Suggestions.Select(s => s.TrackId));
    }

    [Fact]
    public async Task Search_DateOnly_FiltersDay_NewestFirst()
    {
        var (db, id, svc) = await SessionAsync();
        await using var _ = db;
        await db.AddSuggestibleAsync(new TrackId(50), Me, name: "Morning", startLocal: OnDay(8));
        await db.AddSuggestibleAsync(new TrackId(51), Me, name: "Evening", startLocal: OnDay(18));
        await db.AddSuggestibleAsync(new TrackId(52), Me, name: "Yesterday", startLocal: new DateTime(2025, 6, 14, 12, 0, 0, DateTimeKind.Unspecified));

        var r = (await svc.SearchAsync(id, null, Day))!;

        Assert.Equal(new[] { 51, 50 }, r.Suggestions.Select(s => s.TrackId)); // newest StartDateTimeUtc first
    }

    [Fact]
    public async Task NullName_FallsBackToActivityId()
    {
        var (db, id, svc) = await SessionAsync();
        await using var _ = db;
        await db.AddSuggestibleAsync(new TrackId(60), Me, name: null, startLat: LatFor(200), startLng: Lng);

        var r = (await svc.GetSuggestionsAsync(id, Lat, Lng, null))!;

        Assert.Equal("Activity 60", Assert.Single(r.Suggestions).Name);
    }
}
