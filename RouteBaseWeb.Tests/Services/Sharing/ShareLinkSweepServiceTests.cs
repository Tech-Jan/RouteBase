using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Sharing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Sharing;

/// <summary>
/// The share-link expiry sweep. It is hygiene, not enforcement — a dead link stops working at resolve time
/// regardless — so what these pin is that it reclaims only what it should: never a live link, and never one
/// that was OPENED, because a dead link's open_count is the resource's view history.
/// </summary>
public class ShareLinkSweepServiceTests : IAsyncLifetime
{
    private const string Owner = "owner-user";
    private static readonly DateTime Now = new(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Grace = TimeSpan.FromDays(30);

    private RouteBaseDbContext _db = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:sharesweep_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _db = new RouteBaseDbContext(options);
        await _db.Database.OpenConnectionAsync();
        await _db.Database.EnsureCreatedAsync();

        _db.Users.Add(new AppUser { UserId = Owner, DisplayName = Owner, Email = "o@t.com", IsAdmin = false });
        _db.Activities.Add(new Activity { TrackId = new TrackId(1), UserId = Owner, TrackName = "ride" });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private async Task<int> AddLinkAsync(DateTime expiresAt, DateTime? revokedAt = null, int opens = 0)
    {
        var row = new ShareLink
        {
            TokenHash = ShareTokens.Hash(ShareTokens.New()),
            Kind = ShareKindNames.Activity,
            TrackId = new TrackId(1),
            UserId = Owner,
            CreatedAt = Now.AddDays(-60),
            ExpiresAt = expiresAt,
            RevokedAt = revokedAt,
            OpenCount = opens,
        };
        _db.ShareLinks.Add(row);
        await _db.SaveChangesAsync();
        return row.ShareLinkId;
    }

    private Task<int> SweepAsync() =>
        ShareLinkSweepService.SweepOnceAsync(_db, Now, Grace, default);

    [Fact]
    public async Task Sweep_LeavesLiveLinksAlone()
    {
        await AddLinkAsync(expiresAt: Now.AddDays(7));

        Assert.Equal(0, await SweepAsync());
        Assert.Single(await _db.ShareLinks.ToListAsync());
    }

    [Fact]
    public async Task Sweep_LeavesRecentlyExpiredLinksListed()
    {
        // Inside the grace window: the owner's list should still be able to say "expired 3 days ago".
        await AddLinkAsync(expiresAt: Now.AddDays(-3));

        Assert.Equal(0, await SweepAsync());
        Assert.Single(await _db.ShareLinks.ToListAsync());
    }

    [Fact]
    public async Task Sweep_ReclaimsLongExpiredLinks()
    {
        await AddLinkAsync(expiresAt: Now.AddDays(-31));

        Assert.Equal(1, await SweepAsync());
        Assert.Empty(await _db.ShareLinks.ToListAsync());
    }

    [Fact]
    public async Task Sweep_ReclaimsLongRevokedLinks_EvenWhenTheirExpiryIsStillInTheFuture()
    {
        // The reason revoked_at is matched on its own timestamp: a link revoked the day it was minted must
        // not linger until its original 30-day expiry plus a grace period.
        await AddLinkAsync(expiresAt: Now.AddYears(1), revokedAt: Now.AddDays(-31));

        Assert.Equal(1, await SweepAsync());
        Assert.Empty(await _db.ShareLinks.ToListAsync());
    }

    [Fact]
    public async Task Sweep_LeavesRecentlyRevokedLinksListed()
    {
        await AddLinkAsync(expiresAt: Now.AddDays(7), revokedAt: Now.AddDays(-2));

        Assert.Equal(0, await SweepAsync());
        Assert.Single(await _db.ShareLinks.ToListAsync());
    }

    [Fact]
    public async Task Sweep_IsIdempotentAndSafeOnAnEmptyTable()
    {
        Assert.Equal(0, await SweepAsync());
        Assert.Equal(0, await SweepAsync());
    }

    [Fact]
    public async Task Sweep_KeepsALongExpiredLinkThatWasOpened()
    {
        // The view count outlives the link: deleting this row would silently reset the ride's badge a month
        // after it stopped being shared. Same age as Sweep_ReclaimsLongExpiredLinks — the ONLY difference is
        // that someone opened it.
        await AddLinkAsync(expiresAt: Now.AddDays(-31), opens: 4);

        Assert.Equal(0, await SweepAsync());
        Assert.Equal(4, (await _db.ShareLinks.SingleAsync()).OpenCount);
    }

    [Fact]
    public async Task Sweep_KeepsALongRevokedLinkThatWasOpened()
    {
        await AddLinkAsync(expiresAt: Now.AddYears(1), revokedAt: Now.AddDays(-31), opens: 2);

        Assert.Equal(0, await SweepAsync());
        Assert.Single(await _db.ShareLinks.ToListAsync());
    }

    [Fact]
    public async Task Sweep_ReclaimsOnlyTheDeadOnes_InAMixedTable()
    {
        var live = await AddLinkAsync(expiresAt: Now.AddDays(7));
        var recentlyExpired = await AddLinkAsync(expiresAt: Now.AddDays(-3));
        var deadButOpened = await AddLinkAsync(expiresAt: Now.AddDays(-40), opens: 7);
        await AddLinkAsync(expiresAt: Now.AddDays(-40));
        await AddLinkAsync(expiresAt: Now.AddYears(1), revokedAt: Now.AddDays(-40));

        Assert.Equal(2, await SweepAsync());

        var remaining = await _db.ShareLinks.AsNoTracking().Select(s => s.ShareLinkId).ToListAsync();
        Assert.Equal([live, recentlyExpired, deadButOpened], remaining.OrderBy(id => id));
    }
}
