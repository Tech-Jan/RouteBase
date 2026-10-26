using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Tracks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Tracks;

/// <summary>
/// TE-31 unit tests for <see cref="ContentTrackReader"/> against real in-memory SQLite. A keep-alive
/// connection holds the shared-cache DB open across the reader's own producer-scoped connections.
///
/// Track_Blob + Recording_Staging come from the production DbSchemaManager Ensure* methods; the reader reads
/// staging via the injected <see cref="RecordingStagingStore"/> (TE-32).
/// </summary>
public class ContentTrackReaderTests : IAsyncLifetime
{
    private const int BlobTrack = 1;
    private const int StagingTrack = 2;
    private const int BothTrack = 3;
    private const int EmptyTrack = 4;
    private static readonly DateTime BaseTime = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly double BaseEpoch = (BaseTime - DateTime.UnixEpoch).TotalSeconds;

    private ServiceProvider _provider = null!;
    private SqliteConnection _keepAlive = null!;
    private ContentTrackReader _reader = null!;
    private TrackReaderCache _cache = null!;

    public async Task InitializeAsync()
    {
        var connStr = $"Data Source=file:blobreader_{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connStr);
        await _keepAlive.OpenAsync();

        var services = new ServiceCollection().AddSingleton(TimeProvider.System);
        services.AddDbContext<RouteBaseDbContext>(o => o.UseSqlite(connStr));
        services.AddScoped<ITrackObjectStore, TrackObjectStore>();
        services.AddScoped<TrackContentStore>();
        services.AddScoped<RecordingStagingStore>();
        _provider = services.BuildServiceProvider();

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RouteBaseDbContext>();
            await db.Database.EnsureCreatedAsync();
            var schema = new DbSchemaManager(db);
            await schema.EnsureTrackContentTablesAsync();
            await schema.EnsureRecordingStagingTableAsync();
            await SeedAsync(db, scope.ServiceProvider.GetRequiredService<TrackContentStore>());
        }

        _cache = new TrackReaderCache(64L * 1024 * 1024);
        _reader = new ContentTrackReader(_cache, new SingleFlight(), _provider.GetRequiredService<IServiceScopeFactory>());
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
        await _provider.DisposeAsync();
    }

    private static async Task SeedAsync(RouteBaseDbContext db, TrackContentStore content)
    {
        db.Users.Add(new AppUser
        {
            UserId = "u", DisplayName = "u", Email = "u@test.com", IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });
        foreach (var id in new[] { BlobTrack, StagingTrack, BothTrack, EmptyTrack })
            db.Activities.Add(new Activity { TrackId = new TrackId(id), UserId = "u" });
        await db.SaveChangesAsync();

        // BlobTrack + the blob half of BothTrack: encode a 3-point track and store it.
        await PutContentAsync(content, new TrackId(BlobTrack), 3, 0);
        await PutContentAsync(content, new TrackId(BothTrack), 2, 0);   // 2 finalized points

        // StagingTrack: staging-only. BothTrack: staging tail appended after its 2 blob points.
        await InsertStagingAsync(db, new TrackId(StagingTrack), startSeq: 0, count: 3, startEpoch: BaseEpoch);
        await InsertStagingAsync(db, new TrackId(BothTrack), startSeq: 0, count: 2, startEpoch: BaseEpoch + 20);
    }

    private static async Task PutContentAsync(TrackContentStore content, TrackId trackId, int n, double startEpoch)
    {
        var time = new double[n];
        var lat = new double[n];
        var lng = new double[n];
        var altitude = new double[n];
        for (int i = 0; i < n; i++)
        {
            time[i] = BaseEpoch + startEpoch + i;
            lat[i] = 10.0 + i * 0.001;
            lng[i] = 20.0 + i * 0.001;
            altitude[i] = 100 + i;
        }
        var track = DecodedTrackExtensions.FromDoubleChannels(
            time, lat, lng, altitude, null, null, null, null, null, null);
        var blob = TrackBlobCodec.Encode(track);
        await content.AppendVersionAsync(trackId, blob, n);
    }

    private static async Task InsertStagingAsync(
        RouteBaseDbContext db, TrackId trackId, int startSeq, int count, double startEpoch)
    {
        for (int i = 0; i < count; i++)
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Recording_Staging (track_ID, seq, lat, lng, altitude, speed, distance, ts_utc) " +
                "VALUES ({0},{1},{2},{3},{4},{5},{6},{7})",
                trackId.Value, startSeq + i, 30.0 + i * 0.001, 40.0 + i * 0.001,
                200.0 + i, 3.5, i * 10.0, (long)(startEpoch + i));
        }
    }

    [Fact]
    public async Task BlobBackedTrack_DecodesToCodecOutput()
    {
        var t = await _reader.GetTrackAsync(new TrackId(BlobTrack));

        Assert.NotNull(t);
        Assert.Equal(3, t!.Count);
        Assert.Equal(TrackBlobCodec.RoundTripValue(10.0, TrackBlobCodec.LatScale), t.Lat[0]);
        Assert.Equal(TrackBlobCodec.RoundTripValue(100.0, TrackBlobCodec.AltitudeScale), t.Altitude[0]);
        Assert.True(t.HeartRate.IsNull);
    }

    [Fact]
    public async Task BlobBacked_SecondCall_ReturnsCachedInstance()
    {
        var first = await _reader.GetTrackAsync(new TrackId(BlobTrack));
        var second = await _reader.GetTrackAsync(new TrackId(BlobTrack));
        Assert.Same(first, second);
    }

    [Fact]
    public async Task StagingOnlyTrack_IsReadable()
    {
        var t = await _reader.GetTrackAsync(new TrackId(StagingTrack));

        Assert.NotNull(t);
        Assert.Equal(3, t!.Count);
        Assert.Equal(TrackBlobCodec.RoundTripValue(30.0, TrackBlobCodec.LatScale), t.Lat[0]);
        Assert.False(t.Altitude.IsNull);
        Assert.Equal(TrackBlobCodec.RoundTripValue(200.0, TrackBlobCodec.AltitudeScale), t.Altitude[0]);
        // Staging carries no sensor channels.
        Assert.True(t.HeartRate.IsNull);
        Assert.True(t.Cadence.IsNull);
    }

    [Fact]
    public async Task BlobPlusStaging_ConcatenatedInOrder()
    {
        var t = await _reader.GetTrackAsync(new TrackId(BothTrack));

        Assert.NotNull(t);
        Assert.Equal(4, t!.Count);                         // 2 blob + 2 staging (ordinal ramp over the total)
        // First two from the blob, last two from staging.
        Assert.Equal(TrackBlobCodec.RoundTripValue(10.0, TrackBlobCodec.LatScale), t.Lat[0]);
        Assert.Equal(TrackBlobCodec.RoundTripValue(30.0, TrackBlobCodec.LatScale), t.Lat[2]);
        Assert.Equal(TrackBlobCodec.RoundTripValue(30.001, TrackBlobCodec.LatScale), t.Lat[3], 3);
        // Altitude present across both halves.
        Assert.False(t.Altitude.IsNull);
        Assert.Equal(TrackBlobCodec.RoundTripValue(200.0, TrackBlobCodec.AltitudeScale), t.Altitude[2]);
    }

    [Fact]
    public async Task StagingBackedResult_IsNotCached()
    {
        var first = await _reader.GetTrackAsync(new TrackId(BothTrack));
        var second = await _reader.GetTrackAsync(new TrackId(BothTrack));
        Assert.NotSame(first, second);   // recomputed every call (grows on append)
        Assert.Equal(0, _cache.CurrentBytes);
    }

    [Fact]
    public async Task NoBlobNoStaging_ReturnsNull()
    {
        Assert.Null(await _reader.GetTrackAsync(new TrackId(EmptyTrack)));
        Assert.Null(await _reader.GetTrackAsync(new TrackId(999)));
    }

    /// <summary>
    /// CL-03 pin. A finalized track's decode is cached under its object sha — but a session that starts
    /// recording ON it must still be seen: the staged tail is checked on EVERY read, and a cached head can
    /// never be handed back alone once one exists. Guards the fast path added for the non-recording case.
    /// </summary>
    [Fact]
    public async Task StagingAppendedAfterACachedRead_IsStillConcatenated()
    {
        var finalized = await _reader.GetTrackAsync(new TrackId(BlobTrack));
        Assert.Equal(3, finalized!.Count);                 // head only, and now cached
        Assert.True(_cache.CurrentBytes > 0);

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RouteBaseDbContext>();
            await InsertStagingAsync(db, new TrackId(BlobTrack), startSeq: 0, count: 2, startEpoch: BaseEpoch + 100);
        }

        var live = await _reader.GetTrackAsync(new TrackId(BlobTrack));

        Assert.Equal(5, live!.Count);                      // 3 head + 2 staged, not the cached 3
        Assert.Equal(TrackBlobCodec.RoundTripValue(30.0, TrackBlobCodec.LatScale), live.Lat[3]);
    }
}
