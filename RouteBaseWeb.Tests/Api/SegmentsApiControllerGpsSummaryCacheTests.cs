using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// perf-2 pin: GET /api/SegmentsApi/{id}/gps-summary — the per-card minimap thumbnail the segments index
/// fires once per card per revisit — now serves a cacheable body (ETag over the bytes + short private
/// max-age) so an unchanged segment gets a 304 instead of the full re-send. Mirrors the RacesApiController
/// replay idiom.
/// </summary>
public class SegmentsApiControllerGpsSummaryCacheTests : IAsyncLifetime
{
    private const string User = "u1";
    private RouteBaseDbContext _context = null!;
    private ISegmentCrud _crud = null!;

    public async Task InitializeAsync()
    {
        _context = new RouteBaseDbContext(new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:segcache_{Guid.NewGuid():N}?mode=memory&cache=shared").Options);
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

    private SegmentsApiController NewController() =>
        new(_crud, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<SegmentsApiController>.Instance, Options.Create(new JsonOptions()))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private async Task<int> SeedSegmentAsync(double baseLat)
    {
        var seg = new Segment { UserId = User, SegmentDistance = 100 };
        _context.Segments.Add(seg);
        await _context.SaveChangesAsync();
        for (int i = 0; i < 10; i++)
            _context.SegmentGps.Add(new SegmentGps { SegmentId = seg.SegmentId, GpsLat = baseLat + i * 0.001, GpsLong = 11.0 });
        await _context.SaveChangesAsync();
        return seg.SegmentId.Value;
    }

    [Fact]
    public async Task GpsSummary_FirstRequest_SetsEtagAndPrivateMaxAgeAndBody()
    {
        var id = await SeedSegmentAsync(47.0);

        var controller = NewController();
        var result = await controller.GetSegmentGpsSummary(id);

        Assert.IsType<FileContentResult>(result);
        Assert.True(controller.Response.Headers.ContainsKey("ETag"));
        Assert.Contains("max-age=", controller.Response.Headers.CacheControl.ToString(), StringComparison.Ordinal);
        Assert.Contains("private", controller.Response.Headers.CacheControl.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GpsSummary_MatchingIfNoneMatch_Returns304()
    {
        var id = await SeedSegmentAsync(47.0);

        var first = NewController();
        await first.GetSegmentGpsSummary(id);
        var etag = first.Response.Headers.ETag.ToString();

        var second = NewController();
        second.Request.Headers.IfNoneMatch = etag;
        var result = await second.GetSegmentGpsSummary(id);

        Assert.Equal(StatusCodes.Status304NotModified, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    [Fact]
    public async Task GpsSummary_DifferentGeometry_GetsDifferentEtag()
    {
        var a = await SeedSegmentAsync(47.0);
        var b = await SeedSegmentAsync(48.5); // different points → different body

        var ca = NewController();
        await ca.GetSegmentGpsSummary(a);
        var cb = NewController();
        await cb.GetSegmentGpsSummary(b);

        Assert.NotEqual(ca.Response.Headers.ETag.ToString(), cb.Response.Headers.ETag.ToString(), StringComparer.Ordinal);
    }

    [Fact]
    public async Task GpsSummary_UnknownSegment_StillNotFound()
    {
        var result = await NewController().GetSegmentGpsSummary(999);
        Assert.IsType<NotFoundResult>(result);
    }
}
