using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Segments;

/// <summary>
/// SA-11 pin: the GPS-summary stride uses a CEIL factor so it never returns more than <c>maxPoints</c>
/// (the floor factor let 99 points / max 50 return all 99, contradicting the endpoint's "up to N" doc).
/// </summary>
public class SegmentGpsSummaryTests : IAsyncLifetime
{
    private const string User = "u1";
    private RouteBaseDbContext _context = null!;
    private ISegmentCrud _crud = null!;

    public async Task InitializeAsync()
    {
        _context = new RouteBaseDbContext(new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:gpssummary_{Guid.NewGuid():N}?mode=memory&cache=shared").Options);
        await _context.Database.EnsureCreatedAsync();
        _context.Users.Add(new AppUser
        {
            UserId = User, DisplayName = User, Email = "u@t.com", IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });
        await _context.SaveChangesAsync();

        _crud = SegmentServices.Create(
            _context, new MemoryCache(new MemoryCacheOptions()), new TestVisibilityService(User),
            new UserCacheInvalidator(), null!, null!, null!, null!, null!).Crud;
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task GpsSummary_NeverExceedsMaxPoints()
    {
        var seg = new Segment { UserId = User, SegmentDistance = 100 };
        _context.Segments.Add(seg);
        await _context.SaveChangesAsync();
        for (int i = 0; i < 99; i++)
            _context.SegmentGps.Add(new SegmentGps { SegmentId = seg.SegmentId, GpsLat = 47.0 + i * 0.001, GpsLong = 11.0 });
        await _context.SaveChangesAsync();

        var summary = await _crud.GetSegmentGpsSummaryAsync(seg.SegmentId, maxPoints: 50);

        Assert.True(summary.Count <= 50, $"expected <= 50 points, got {summary.Count}");
    }
}
