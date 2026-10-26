using RouteBaseWeb.Helpers;
using RouteBaseWeb.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Tests for the reusable grid filter <see cref="IQueryable{T}"/> helpers. Uses in-memory SQLite
/// (not the EF in-memory provider) so the generated LIKE/IN/range SQL is exercised for real.
/// </summary>
public class GridFilterExtensionsTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;

    public async Task InitializeAsync()
    {
        var dbName = $"gridfilter_test_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:{dbName}?mode=memory&cache=shared")
            .Options;

        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        _context.Users.Add(new AppUser
        {
            UserId = "u1", DisplayName = "U1", Email = "u1@test.com", IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });

        _context.Activities.AddRange(
            new Activity { TrackId = new TrackId(1), UserId = "u1", TrackName = "Morning Run",   ActivityType = "Run",  TotalDistance = 5_000,  TotalAscent = 50 },
            new Activity { TrackId = new TrackId(2), UserId = "u1", TrackName = "Evening Ride",   ActivityType = "Ride", TotalDistance = 40_000, TotalAscent = 600 },
            new Activity { TrackId = new TrackId(3), UserId = "u1", TrackName = "Trail run loop", ActivityType = "Run",  TotalDistance = 12_000, TotalAscent = 300 },
            new Activity { TrackId = new TrackId(4), UserId = "u1", TrackName = "50% effort day", ActivityType = "Walk", TotalDistance = 3_000,  TotalAscent = null }
        );
        await _context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    private IQueryable<Activity> Q => _context.Activities.AsQueryable();

    // ── WhereTextLike ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WhereTextLike_EmptyPattern_ReturnsAll(string? pattern)
    {
        var ids = await Q.WhereTextLike(a => a.TrackName, pattern).Select(a => a.TrackId).ToListAsync();
        Assert.Equal(4, ids.Count);
    }

    [Fact]
    public async Task WhereTextLike_NoWildcard_IsContainsMatch()
    {
        // "run" with no wildcard → %run% → matches "Morning Run" and "Trail run loop" (LIKE is case-insensitive)
        var ids = await Q.WhereTextLike(a => a.TrackName, "run").Select(a => a.TrackId).OrderBy(x => x).ToListAsync();
        Assert.Equal(new[] { new TrackId(1), new TrackId(3) }, ids);
    }

    [Fact]
    public async Task WhereTextLike_StarWildcard_AnchorsPattern()
    {
        // "Morning*" → Morning% → only the row that starts with "Morning"
        var ids = await Q.WhereTextLike(a => a.TrackName, "Morning*").Select(a => a.TrackId).ToListAsync();
        Assert.Equal(new[] { new TrackId(1) }, ids);
    }

    [Fact]
    public async Task WhereTextLike_EscapesLiteralPercent()
    {
        // "50%" has no * / ? → treated literally as contains; the % must be escaped so it matches
        // the literal "50%" row and not every row.
        var ids = await Q.WhereTextLike(a => a.TrackName, "50%").Select(a => a.TrackId).ToListAsync();
        Assert.Equal(new[] { new TrackId(4) }, ids);
    }

    [Fact]
    public async Task WhereTextLike_OrAlternation_MatchesAnyTerm()
    {
        // "morning|trail" → contains "morning" (1) OR "trail" (3).
        var ids = await Q.WhereTextLike(a => a.TrackName, "morning|trail")
            .Select(a => a.TrackId).OrderBy(x => x).ToListAsync();
        Assert.Equal(new[] { new TrackId(1), new TrackId(3) }, ids);
    }

    [Fact]
    public async Task WhereTextLike_OrAlternation_MixesWildcardAndContains()
    {
        // "*run|ride" → ends-with "run" (Morning Run = 1) OR contains "ride" (Evening Ride = 2).
        var ids = await Q.WhereTextLike(a => a.TrackName, "*run|ride")
            .Select(a => a.TrackId).OrderBy(x => x).ToListAsync();
        Assert.Equal(new[] { new TrackId(1), new TrackId(2) }, ids);
    }

    [Fact]
    public async Task WhereTextLike_TrailingEmptyTerm_IsIgnored()
    {
        // "run|" behaves exactly like "run".
        var ids = await Q.WhereTextLike(a => a.TrackName, "run|")
            .Select(a => a.TrackId).OrderBy(x => x).ToListAsync();
        Assert.Equal(new[] { new TrackId(1), new TrackId(3) }, ids);
    }

    // ── WhereTextLike negation (! / !=) ─────────────────────────────────────────

    [Theory]
    [InlineData("!run")]
    [InlineData("!=run")]
    public async Task WhereTextLike_Negated_ExcludesMatches(string pattern)
    {
        // NOT contains "run" → excludes "Morning Run" (1) and "Trail run loop" (3).
        var ids = await Q.WhereTextLike(a => a.TrackName, pattern)
            .Select(a => a.TrackId).OrderBy(x => x).ToListAsync();
        Assert.Equal(new[] { new TrackId(2), new TrackId(4) }, ids);
    }

    [Fact]
    public async Task WhereTextLike_MixedPositiveAndNegated()
    {
        // "run|!*loop*" → contains "run" (1,3) AND NOT contains "loop" (excludes 3) → 1.
        var ids = await Q.WhereTextLike(a => a.TrackName, "run|!*loop*")
            .Select(a => a.TrackId).OrderBy(x => x).ToListAsync();
        Assert.Equal(new[] { new TrackId(1) }, ids);
    }

    [Fact]
    public async Task WhereTextLike_Negated_IncludesNullName()
    {
        // A NULL name can't contain the term, so a negation must INCLUDE it (null-safe NOT LIKE).
        _context.Activities.Add(new Activity { TrackId = new TrackId(5), UserId = "u1", TrackName = null, ActivityType = "Run" });
        await _context.SaveChangesAsync();

        var ids = await Q.WhereTextLike(a => a.TrackName, "!run")
            .Select(a => a.TrackId).OrderBy(x => x).ToListAsync();
        Assert.Equal(new[] { new TrackId(2), new TrackId(4), new TrackId(5) }, ids);
    }

    // ── WhereInRange ───────────────────────────────────────────────────────────

    [Fact]
    public async Task WhereInRange_MinAndMax_Inclusive()
    {
        var ids = await Q.WhereInRange(a => a.TotalDistance, 5_000, 12_000)
            .Select(a => a.TrackId).OrderBy(x => x).ToListAsync();
        Assert.Equal(new[] { new TrackId(1), new TrackId(3) }, ids);
    }

    [Fact]
    public async Task WhereInRange_OnlyMin()
    {
        var ids = await Q.WhereInRange(a => a.TotalDistance, 10_000, null)
            .Select(a => a.TrackId).OrderBy(x => x).ToListAsync();
        Assert.Equal(new[] { new TrackId(2), new TrackId(3) }, ids);
    }

    [Fact]
    public async Task WhereInRange_NullColumnNeverMatches()
    {
        // TrackId 4 has null TotalAscent — must be excluded by a range filter.
        var ids = await Q.WhereInRange(a => a.TotalAscent, 0, 10_000)
            .Select(a => a.TrackId).OrderBy(x => x).ToListAsync();
        Assert.Equal(new[] { new TrackId(1), new TrackId(2), new TrackId(3) }, ids);
    }

    [Fact]
    public async Task WhereInRange_NoBounds_ReturnsAll()
    {
        var count = await Q.WhereInRange(a => a.TotalDistance, null, null).CountAsync();
        Assert.Equal(4, count);
    }

    // ── WhereInRanges (multi-range) ──────────────────────────────────────────────

    [Fact]
    public async Task WhereInRanges_SingleRange_MatchesWhereInRange()
    {
        var ids = await Q.WhereInRanges(a => a.TotalDistance, new (double?, double?)[] { (5_000, 12_000) })
            .Select(a => a.TrackId).OrderBy(x => x).ToListAsync();
        Assert.Equal(new[] { new TrackId(1), new TrackId(3) }, ids);
    }

    [Fact]
    public async Task WhereInRanges_DisjointRanges_OrTogether()
    {
        // [0,4000] (row 4 = 3000) OR [30000,∞) (row 2 = 40000).
        var ids = await Q.WhereInRanges(a => a.TotalDistance, new (double?, double?)[] { (0, 4_000), (30_000, null) })
            .Select(a => a.TrackId).OrderBy(x => x).ToListAsync();
        Assert.Equal(new[] { new TrackId(2), new TrackId(4) }, ids);
    }

    [Fact]
    public async Task WhereInRanges_NullColumnNeverMatches()
    {
        // TrackId 4 has null TotalAscent — excluded even with overlapping ranges.
        var ids = await Q.WhereInRanges(a => a.TotalAscent, new (double?, double?)[] { (0, 100), (0, 100_000) })
            .Select(a => a.TrackId).OrderBy(x => x).ToListAsync();
        Assert.Equal(new[] { new TrackId(1), new TrackId(2), new TrackId(3) }, ids);
    }

    [Fact]
    public async Task WhereInRanges_EmptyList_ReturnsAll()
    {
        var count = await Q.WhereInRanges(a => a.TotalDistance, Array.Empty<(double?, double?)>()).CountAsync();
        Assert.Equal(4, count);
    }

    // ── WhereInSet ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhereInSet_MatchesMembers()
    {
        var ids = await Q.WhereInSet(a => a.ActivityType, new[] { "Run", "Walk" })
            .Select(a => a.TrackId).OrderBy(x => x).ToListAsync();
        Assert.Equal(new[] { new TrackId(1), new TrackId(3), new TrackId(4) }, ids);
    }

    [Fact]
    public async Task WhereInSet_EmptyOrNull_ReturnsAll()
    {
        Assert.Equal(4, await Q.WhereInSet(a => a.ActivityType, Array.Empty<string>()).CountAsync());
        Assert.Equal(4, await Q.WhereInSet(a => a.ActivityType, null).CountAsync());
    }

    // ── ToLikePattern (pure) ────────────────────────────────────────────────────

    [Theory]
    [InlineData("run", "%run%")]            // no wildcard → contains
    [InlineData("Morning*", "Morning%")]    // star → percent, anchored
    [InlineData("a?c", "a_c")]              // question → underscore
    [InlineData("50%", "%50\\%%")]          // literal percent escaped, then wrapped
    [InlineData("a_b", "%a\\_b%")]          // literal underscore escaped
    [InlineData("c:\\x", "%c:\\\\x%")]      // literal backslash escaped
    public void ToLikePattern_BuildsExpectedPattern(string input, string expected)
    {
        Assert.Equal(expected, GridFilterExtensions.ToLikePattern(input));
    }
}
