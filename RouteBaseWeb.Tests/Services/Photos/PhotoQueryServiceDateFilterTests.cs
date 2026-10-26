using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Photos;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Photos;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Photos;

/// <summary>
/// B2: the grid date filter selects by the photo's OWN local calendar day (CapturedUtc + TzOffset),
/// both bounds inclusive — not by the UTC column, which shifts near-midnight photos into the wrong
/// day for any non-zero offset. Runs against real SQLite so the per-row AddHours translation is
/// proven, not assumed.
/// </summary>
public class PhotoQueryServiceDateFilterTests : IAsyncLifetime
{
    private const string Me = "me";

    private RouteBaseDbContext _context = null!;
    private PhotoQueryService _svc = null!;

    public async Task InitializeAsync()
    {
        _context = new RouteBaseDbContext(new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:photodatefilter_{Guid.NewGuid():N}?mode=memory&cache=shared").Options);
        await _context.Database.OpenConnectionAsync();
        await _context.Database.EnsureCreatedAsync();
        await new DbSchemaManager(_context).EnsurePhotosTableAsync();

        _context.Users.Add(new AppUser { UserId = Me, DisplayName = Me, Email = "me@t.com", IsAdmin = false });
        _context.Activities.Add(new Activity { TrackId = new TrackId(1), UserId = Me, ActivityType = "Ride" });
        await _context.SaveChangesAsync();

        // The no-trackId path never touches the visibility service or the image store.
        _svc = new PhotoQueryService(_context, null!, new TestCurrentUserService(Me), null!);
    }

    public async Task DisposeAsync()
    {
        await _context.Database.CloseConnectionAsync();
        await _context.DisposeAsync();
    }

    private async Task<int> AddPhotoAsync(DateTime? capturedUtc, double? tzOffsetHours)
    {
        var photo = new Photo
        {
            TrackId = new TrackId(1), UserId = Me, CapturedUtc = capturedUtc, TzOffset = tzOffsetHours,
            ImportedUtc = DateTime.UtcNow,
        };
        _context.Photos.Add(photo);
        await _context.SaveChangesAsync();
        return photo.PhotoId.Value;
    }

    private async Task<List<int>> ListIdsAsync(DateOnly? from, DateOnly? to)
    {
        var page = await _svc.ListAsync(
            trackId: null, from, to, "captured", 1, 100, PhotoFieldScope.OwnerWhenOwned);
        return page.Items.Select(i => i.PhotoId).ToList();
    }

    private static DateOnly Day(int d) => new(2026, 6, d);

    [Fact]
    public async Task PlusOffset_PhotoAfterUtcMidnightRollover_MatchesItsLocalDayNotItsUtcDay()
    {
        // 22:30Z June 15 at +2 → local 00:30 June 16. A human took this photo "on the 16th".
        var id = await AddPhotoAsync(new DateTime(2026, 6, 15, 22, 30, 0, DateTimeKind.Utc), +2);

        Assert.Contains(id, await ListIdsAsync(Day(16), Day(16)));
        Assert.DoesNotContain(id, await ListIdsAsync(Day(15), Day(15))); // its UTC day
    }

    [Fact]
    public async Task MinusOffset_PhotoBeforeUtcMidnightRollover_MatchesItsLocalDayNotItsUtcDay()
    {
        // 04:30Z June 16 at −5 → local 23:30 June 15.
        var id = await AddPhotoAsync(new DateTime(2026, 6, 16, 4, 30, 0, DateTimeKind.Utc), -5);

        Assert.Contains(id, await ListIdsAsync(Day(15), Day(15)));
        Assert.DoesNotContain(id, await ListIdsAsync(Day(16), Day(16))); // its UTC day
    }

    [Fact]
    public async Task NullOffset_BehavesAsUtc()
    {
        var id = await AddPhotoAsync(new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc), null);

        Assert.Contains(id, await ListIdsAsync(Day(15), Day(15)));
        Assert.DoesNotContain(id, await ListIdsAsync(Day(16), Day(16)));
    }

    [Fact]
    public async Task ToBound_IsInclusiveOfTheWholeLocalDay()
    {
        // Local 23:30 June 15 (−5): the old UTC `<= to` compare needed a client-side T23:59:59 hack
        // and STILL missed this one (04:30Z June 16 > June 15 23:59:59Z).
        var id = await AddPhotoAsync(new DateTime(2026, 6, 16, 4, 30, 0, DateTimeKind.Utc), -5);

        Assert.Contains(id, await ListIdsAsync(Day(1), Day(15)));
    }

    [Fact]
    public async Task FractionalOffset_Translates()
    {
        // Nepal +5.75: 18:30Z June 15 → local 00:15 June 16.
        var id = await AddPhotoAsync(new DateTime(2026, 6, 15, 18, 30, 0, DateTimeKind.Utc), +5.75);

        Assert.Contains(id, await ListIdsAsync(Day(16), Day(16)));
        Assert.DoesNotContain(id, await ListIdsAsync(Day(15), Day(15)));
    }

    [Fact]
    public async Task NullCapturedUtc_NeverMatchesAnActiveFilter_ButListsUnfiltered()
    {
        var id = await AddPhotoAsync(null, null);

        Assert.Contains(id, await ListIdsAsync(null, null));
        Assert.DoesNotContain(id, await ListIdsAsync(Day(1), Day(30)));
        Assert.DoesNotContain(id, await ListIdsAsync(Day(1), null));
        Assert.DoesNotContain(id, await ListIdsAsync(null, Day(30)));
    }
}
