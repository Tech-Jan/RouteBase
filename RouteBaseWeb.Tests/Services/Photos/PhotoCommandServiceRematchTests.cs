using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Photos;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Photos;

/// <summary>
/// Whole-track photo re-match seam (A2 §5.5): re-resolves every one of the caller's photos on a track whose
/// geometry changed — track-derived locations move, manual/exif stay, TrackPositionM recomputes for all — and
/// never touches another user's photos.
/// </summary>
public class PhotoCommandServiceRematchTests : IAsyncLifetime
{
    private const string Me = "me";
    private const string Other = "other";
    private const int Track = 50;

    private RouteBaseDbContext _context = null!;
    private PhotoCommandService _svc = null!;
    private readonly DateTime _t1 = new(2025, 1, 1, 8, 0, 1, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        _context = new RouteBaseDbContext(new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:photorematch_{Guid.NewGuid():N}?mode=memory&cache=shared").Options);
        await _context.Database.OpenConnectionAsync();
        await _context.Database.EnsureCreatedAsync();
        var schema = new DbSchemaManager(_context);
        await schema.EnsureTrackContentTablesAsync();
        await schema.EnsurePhotosTableAsync();

        foreach (var u in new[] { Me, Other })
            _context.Users.Add(new AppUser { UserId = u, DisplayName = u, Email = $"{u}@t.com", IsAdmin = false });
        _context.Activities.Add(new Activity { TrackId = new TrackId(Track), UserId = Me, ActivityType = "Ride" });
        await _context.SaveChangesAsync();

        var t0 = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        await TrackSeed.SeedTrackAsync(_context, new TrackId(Track), new[]
        {
            (47.000, 11.0, t0), (47.001, 11.0, t0.AddSeconds(1)), (47.002, 11.0, t0.AddSeconds(2)),
        });

        _svc = new PhotoCommandService(
            _context, new DirectTrackReader(_context), null!, new TestCurrentUserService(Me));
    }

    public async Task DisposeAsync()
    {
        await _context.Database.CloseConnectionAsync();
        await _context.DisposeAsync();
    }

    private async Task<int> AddPhotoAsync(string owner, string locationSource, double lat, double lng)
    {
        var photo = new Photo
        {
            TrackId = new TrackId(Track), UserId = owner, LocationSource = locationSource, Assignment = "manual",
            CapturedUtc = _t1, Lat = lat, Lng = lng, ImportedUtc = DateTime.UtcNow,
        };
        _context.Photos.Add(photo);
        await _context.SaveChangesAsync();
        return photo.PhotoId.Value;
    }

    [Fact]
    public async Task Rematch_TrackDerivedRecomputed_ManualExifKept()
    {
        int interp = await AddPhotoAsync(Me, "interpolated", lat: 0, lng: 0);
        int manual = await AddPhotoAsync(Me, "manual", lat: 10, lng: 10);

        int updated = await _svc.RematchTrackPhotosAsync(new TrackId(Track));

        Assert.Equal(2, updated);
        var a = await _context.Photos.AsNoTracking().FirstAsync(p => p.PhotoId == new PhotoId(interp));
        var b = await _context.Photos.AsNoTracking().FirstAsync(p => p.PhotoId == new PhotoId(manual));
        Assert.Equal(47.001, a.Lat!.Value, 4);         // moved onto the track at its capture time
        Assert.NotNull(a.TrackPositionM);
        Assert.Equal(10, b.Lat!.Value);                // manual location kept
        Assert.Equal(10, b.Lng!.Value);
        Assert.NotNull(b.TrackPositionM);              // but position still recomputed
    }

    [Fact]
    public async Task Rematch_OnlyOwnPhotosOnTrack()
    {
        int foreign = await AddPhotoAsync(Other, "interpolated", lat: 0, lng: 0);

        int updated = await _svc.RematchTrackPhotosAsync(new TrackId(Track));

        Assert.Equal(0, updated);
        var c = await _context.Photos.AsNoTracking().FirstAsync(p => p.PhotoId == new PhotoId(foreign));
        Assert.Equal(0, c.Lat!.Value);                 // foreign photo untouched
        Assert.Null(c.TrackPositionM);
    }
}
