using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.GridFilter;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Step 6: <see cref="ActivityExportService"/> streams a ZIP of per-activity GPX. Verifies user-scoping,
/// trackId filtering, in-zip name-collision suffixing, and that the output stream stays open (leaveOpen).
/// </summary>
public class ActivityExportServiceTests
{
    private const string Me = "me";
    private const string Other = "other";

    [Fact]
    public async Task WriteAllAsZip_AllForUser_ExcludesOtherUsers_AndSuffixesCollisions()
    {
        var query = new FakeQuery
        {
            Activities =
            {
                new Activity { TrackId = new TrackId(1), UserId = Me,    TrackName = "Ride" },
                new Activity { TrackId = new TrackId(2), UserId = Me,    TrackName = "Ride" }, // same name → collision
                new Activity { TrackId = new TrackId(3), UserId = Other, TrackName = "Secret" },
            }
        };
        var svc = new ActivityExportService(query, new FakeGpxExport(query.Activities), NullLogger<ActivityExportService>.Instance);

        using var output = new MemoryStream();
        var manifest = await svc.WriteAllAsZipAsync(output, Me, trackIds: null, CancellationToken.None);

        Assert.Equal(new[] { 1, 2 }, manifest.IncludedIds.OrderBy(id => id));
        Assert.Empty(manifest.Skipped);
        Assert.True(output.CanWrite, "output must stay open (leaveOpen) for the HTTP response");

        var names = EntryNames(output);
        Assert.Equal(
            new[] { "Ride.gpx", "Ride_1.gpx", "manifest.json" },
            names.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("Secret.gpx", names, StringComparer.Ordinal);
    }

    /// <summary>
    /// API-10: a requested id the caller doesn't own is SKIPPED (with a reason recorded in the manifest),
    /// not silently dropped into a mysteriously-smaller ZIP and not a hard 403 for the whole request.
    /// </summary>
    [Fact]
    public async Task WriteAllAsZip_TrackIdFilter_SkipsUnownedIds_AndRecordsWhy()
    {
        var query = new FakeQuery
        {
            Activities =
            {
                new Activity { TrackId = new TrackId(1), UserId = Me, TrackName = "A" },
                new Activity { TrackId = new TrackId(2), UserId = Me, TrackName = "B" },
                new Activity { TrackId = new TrackId(3), UserId = Other, TrackName = "C" },
            }
        };
        var svc = new ActivityExportService(query, new FakeGpxExport(query.Activities), NullLogger<ActivityExportService>.Instance);

        using var output = new MemoryStream();
        // Ask for 2 (owned) and 3 (not owned) — only 2 should be written; 3 shows up as skipped.
        var manifest = await svc.WriteAllAsZipAsync(output, Me, new[] { 2, 3 }, CancellationToken.None);

        Assert.Equal(new[] { 2 }, manifest.IncludedIds);
        var skip = Assert.Single(manifest.Skipped);
        Assert.Equal(3, skip.TrackId);
        Assert.Equal(BulkExportSkip.NotOwned, skip.Reason);
        Assert.Equal(new[] { "B.gpx", "manifest.json" }, EntryNames(output).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Every id foreign: still a 200 with an (almost-)empty ZIP, but the manifest says exactly
    /// why instead of leaving the caller to guess (API-10's reported "empty ZIP" symptom).</summary>
    [Fact]
    public async Task WriteAllAsZip_AllRequestedIdsUnowned_IncludesNone_ButManifestExplainsWhy()
    {
        var query = new FakeQuery
        {
            Activities = { new Activity { TrackId = new TrackId(9), UserId = Other, TrackName = "NotYours" } }
        };
        var svc = new ActivityExportService(query, new FakeGpxExport(query.Activities), NullLogger<ActivityExportService>.Instance);

        using var output = new MemoryStream();
        var manifest = await svc.WriteAllAsZipAsync(output, Me, new[] { 9 }, CancellationToken.None);

        Assert.Empty(manifest.IncludedIds);
        var skip = Assert.Single(manifest.Skipped);
        Assert.Equal(9, skip.TrackId);
        Assert.Equal(BulkExportSkip.NotOwned, skip.Reason);
        Assert.Equal(new[] { "manifest.json" }, EntryNames(output));
    }

    /// <summary>Pins the manifest.json wire shape — a client that only kept the ZIP still needs to be able
    /// to parse it, in the same camelCase convention as every other JSON contract in the app.</summary>
    [Fact]
    public async Task ManifestJsonEntry_IsCamelCase_AndMatchesTheReturnedManifest()
    {
        var query = new FakeQuery
        {
            Activities =
            {
                new Activity { TrackId = new TrackId(1), UserId = Me, TrackName = "A" },
                new Activity { TrackId = new TrackId(2), UserId = Other, TrackName = "NotYours" },
            }
        };
        var svc = new ActivityExportService(query, new FakeGpxExport(query.Activities), NullLogger<ActivityExportService>.Instance);

        using var output = new MemoryStream();
        var manifest = await svc.WriteAllAsZipAsync(output, Me, new[] { 1, 2 }, CancellationToken.None);

        using var read = new MemoryStream(output.ToArray());
        using var archive = new ZipArchive(read, ZipArchiveMode.Read);
        using var manifestStream = archive.GetEntry("manifest.json")!.Open();
        using var doc = await JsonDocument.ParseAsync(manifestStream);

        Assert.Equal(manifest.IncludedIds, doc.RootElement.GetProperty("includedIds").EnumerateArray().Select(e => e.GetInt32()));
        var skipped = doc.RootElement.GetProperty("skipped").EnumerateArray().ToList();
        Assert.Single(skipped);
        Assert.Equal(2, skipped[0].GetProperty("trackId").GetInt32());
        Assert.Equal(BulkExportSkip.NotOwned, skipped[0].GetProperty("reason").GetString());
    }

    /// <summary>
    /// Each entry is dated at the RIDE, not at the export — otherwise a decade of activities extracts as a
    /// decade of files all stamped with the day the ZIP was built. Wall-clock local (the date the
    /// activities table shows), because a zip timestamp is a zone-less MS-DOS field read back as local.
    /// </summary>
    [Fact]
    public async Task WriteAllAsZip_StampsEachEntryWithTheRidesLocalStart()
    {
        var start = new DateTime(2024, 5, 12, 7, 42, 18, DateTimeKind.Unspecified);
        var query = new FakeQuery
        {
            Activities =
            {
                new Activity { TrackId = new TrackId(1), UserId = Me, TrackName = "Dated", StartDateTimeLocal = start },
            }
        };
        var svc = new ActivityExportService(query, new FakeGpxExport(query.Activities), NullLogger<ActivityExportService>.Instance);

        using var output = new MemoryStream();
        await svc.WriteAllAsZipAsync(output, Me, trackIds: null, CancellationToken.None);

        using var read = new MemoryStream(output.ToArray());
        using var archive = new ZipArchive(read, ZipArchiveMode.Read);
        // Zip stores seconds at 2 s resolution, so :18 round-trips but an odd second would not.
        Assert.Equal(start, archive.GetEntry("Dated.gpx")!.LastWriteTime.DateTime);
    }

    /// <summary>
    /// The zip epoch is 1980..2107 and the LastWriteTime setter THROWS outside it. An activity with no
    /// start (1 of the live library's) or an absurd one must keep the default stamp, not kill the export.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("1970-01-01")]
    public async Task WriteAllAsZip_UndatableStart_KeepsTheDefaultStamp_AndStillExports(string? startDate)
    {
        var query = new FakeQuery
        {
            Activities =
            {
                new Activity
                {
                    TrackId = new TrackId(1), UserId = Me, TrackName = "Undatable",
                    StartDateTimeLocal = startDate is null ? null : DateTime.Parse(startDate, CultureInfo.InvariantCulture),
                },
            }
        };
        var svc = new ActivityExportService(query, new FakeGpxExport(query.Activities), NullLogger<ActivityExportService>.Instance);

        using var output = new MemoryStream();
        var manifest = await svc.WriteAllAsZipAsync(output, Me, trackIds: null, CancellationToken.None);

        Assert.Equal(new[] { 1 }, manifest.IncludedIds);
        using var read = new MemoryStream(output.ToArray());
        using var archive = new ZipArchive(read, ZipArchiveMode.Read);
        Assert.True(archive.GetEntry("Undatable.gpx")!.LastWriteTime.Year >= 1980);
    }

    private static string[] EntryNames(MemoryStream zipStream)
    {
        var bytes = zipStream.ToArray();
        using var read = new MemoryStream(bytes);
        using var archive = new ZipArchive(read, ZipArchiveMode.Read);
        return archive.Entries.Select(e => e.Name).ToArray();
    }

    private sealed class FakeQuery : IActivityQueryService
    {
        public List<Activity> Activities { get; } = new();
        public Task<List<Activity>> GetAllActivitiesAsync() => Task.FromResult(Activities);

        // Unused by the service under test.
        public Task<Activity?> GetActivityMetadataByIdAsync(int id) => throw new NotSupportedException();
        public Task<Activity?> GetOwnedActivityMetadataByIdAsync(int id) => throw new NotSupportedException();
        public Task<bool> IsActivityOwnedAsync(TrackId trackId) => throw new NotSupportedException();
        public Task<bool> IsActivityVisibleAsync(TrackId trackId) => throw new NotSupportedException();
        public Task<Dictionary<int, ActivityMapSlice>> GetActivitySlicesByIdsAsync(List<int> trackIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<ActivityEndpoint>> GetActivityEndpointsAsync(List<int> trackIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<string>> GetActivityTypesAsync() => throw new NotSupportedException();
        public Task<List<int>> GetTrackIdsContainingPointAsync(double lat, double lng, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> GetVisibleActivityCountAsync() => throw new NotSupportedException();
        public Task<Dictionary<TrackId, int>> GetPhotoCountsAsync(IReadOnlyCollection<TrackId> trackIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(List<Activity> Items, int TotalCount)> GetPagedActivitiesAsync(
            string? activityType, DateTime? startDate, DateTime? endDate, string sortColumn, string sortDirection,
            int page, int pageSize, string? ownerUserId = null, IReadOnlyList<string>? activityTypes = null,
            GridFilterCollection? filters = null, IReadOnlyList<string>? ownerUserIds = null, bool ownerMatchNone = false, IReadOnlyCollection<TrackId>? restrictIds = null, int? knownCount = null)
            => throw new NotSupportedException();
        public Task<List<TrackId>> GetFilteredActivityIdsAsync(
            string? activityType, DateTime? startDate, DateTime? endDate, string? ownerUserId = null,
            IReadOnlyList<string>? activityTypes = null, GridFilterCollection? filters = null,
            IReadOnlyList<string>? ownerUserIds = null, bool ownerMatchNone = false, IReadOnlyCollection<TrackId>? restrictIds = null)
            => throw new NotSupportedException();
        public Task<List<Activity>> GetActivitiesWithGpsCoordinatesAsync(int limit = 100) => throw new NotSupportedException();
        public Task<ActivityStatistics> GetStatisticsAsync(
            string? activityType = null, DateTime? startDate = null, DateTime? endDate = null, string? ownerUserId = null)
            => throw new NotSupportedException();
    }

    private sealed class FakeGpxExport(List<Activity> activities) : IGpxExportService
    {
        public Task<(Stream? Stream, string FileName)?> ExportAsync(TrackId trackId, CancellationToken ct = default)
        {
            // Name mirrors the real exporter (activity TrackName → .gpx); content is a tiny placeholder.
            var name = activities.First(a => a.TrackId == trackId).TrackName + ".gpx";
            var stream = new MemoryStream(Encoding.UTF8.GetBytes($"<gpx id=\"{trackId}\"/>"));
            return Task.FromResult<(Stream?, string)?>((stream, name));
        }
    }
}
