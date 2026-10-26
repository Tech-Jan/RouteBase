using RouteBaseWeb.Models;
using RouteBaseWeb.Pages.Activities;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Pages;

/// <summary>
/// PR-08: the Trash page is a read. It renders the entities through the shared <c>_ActivityCard</c> partial and
/// never writes them, so it must not take a change-tracker snapshot of every card — the cost grows with the
/// trash. The rendered content is pinned alongside, because that is what must NOT change.
/// </summary>
public class TrashModelTests : IAsyncLifetime
{
    private const string Me = "me";

    private RouteBaseDbContext _context = null!;
    private SqliteConnection _conn = null!;
    private TrashModel _page = null!;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        await _conn.OpenAsync();
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        _context.Users.Add(new AppUser
        {
            UserId = Me, DisplayName = Me, Email = "me@test.com", IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });
        _context.Activities.Add(new Activity
        {
            TrackId = new TrackId(1), UserId = Me, TrackName = "Binned", ActivityType = "Ride", TotalDistance = 10_000,
            Lifecycle = LifecycleStates.Trashed, LifecyclePrev = LifecycleStates.Released,
            LifecycleReason = LifecycleReasons.User, LifecycleAt = DateTime.UtcNow,
        });
        _context.Activities.Add(new Activity
        {
            TrackId = new TrackId(2), UserId = Me, TrackName = "Still riding", ActivityType = "Ride",
            Lifecycle = LifecycleStates.Released,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        _page = new TrashModel(new TrashQueryService(_context, new TestVisibilityService(Me)));
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _conn.DisposeAsync();
    }

    [Fact]
    public async Task OnGetAsync_DoesNotTrackTheRenderedEntities()
    {
        await _page.OnGetAsync(CancellationToken.None);

        Assert.Empty(_context.ChangeTracker.Entries<Activity>());
    }

    /// <summary>The behaviour pin: what the page hands the view is unchanged by not tracking it.</summary>
    [Fact]
    public async Task OnGetAsync_StillRendersTheTrashedRides_AndOnlyThose()
    {
        await _page.OnGetAsync(CancellationToken.None);

        var card = Assert.Single(_page.Activities);
        Assert.Equal(new TrackId(1), card.TrackId);
        Assert.Equal("Binned", card.TrackName);

        var summary = Assert.Single(_page.Trashed).Value;
        Assert.Equal(LifecycleReasons.User, summary.Reason);
        Assert.Equal(LifecycleStates.Released, summary.RestoresTo);
        Assert.Single(_page.GridBootstrap.Rows);
    }
}
