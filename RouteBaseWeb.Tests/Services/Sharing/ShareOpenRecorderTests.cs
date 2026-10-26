using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Sharing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Sharing;

/// <summary>
/// Open counting. The number is a leak signal, so the properties here are that it goes UP once per open,
/// that it touches nothing but the one link, and that it can never take a working share page down with it.
/// </summary>
public class ShareOpenRecorderTests : IAsyncLifetime
{
    private const string Owner = "owner-user";

    private RouteBaseDbContext _db = null!;
    private ShareOpenRecorder _recorder = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:shareopen_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _db = new RouteBaseDbContext(options);
        await _db.Database.OpenConnectionAsync();
        await _db.Database.EnsureCreatedAsync();

        // Share_Link.track_id is a real FK and SQLite enforces it here (the bundled e_sqlite3 defaults
        // foreign_keys ON), so the rides a link points at have to exist before the link does.
        _db.Users.Add(new AppUser
        {
            UserId = Owner, DisplayName = Owner, Email = $"{Owner}@t.com", IsAdmin = false,
        });
        _db.Activities.Add(new Activity { TrackId = new TrackId(1), UserId = Owner, TrackName = "one" });
        _db.Activities.Add(new Activity { TrackId = new TrackId(2), UserId = Owner, TrackName = "two" });
        await _db.SaveChangesAsync();

        _recorder = new ShareOpenRecorder(_db, NullLogger<ShareOpenRecorder>.Instance, TimeProvider.System);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private async Task<int> SeedLinkAsync(TrackId trackId = default, int opens = 0)
    {
        if (trackId == default) trackId = new TrackId(1);
        var row = new ShareLink
        {
            TokenHash = ShareTokens.Hash(ShareTokens.New()),
            Kind = ShareKindNames.Activity,
            TrackId = trackId,
            UserId = Owner,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            OpenCount = opens,
        };
        _db.ShareLinks.Add(row);
        await _db.SaveChangesAsync();
        return row.ShareLinkId;
    }

    [Fact]
    public async Task RecordOpen_IncrementsTheCountAndStampsTheTime()
    {
        var id = await SeedLinkAsync();
        var before = DateTime.UtcNow.AddSeconds(-1);

        await _recorder.RecordOpenAsync(id, default);

        var row = await _db.ShareLinks.AsNoTracking().SingleAsync();
        Assert.Equal(1, row.OpenCount);
        Assert.NotNull(row.LastOpenedAt);
        Assert.InRange(row.LastOpenedAt!.Value, before, DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task RecordOpen_AccumulatesAcrossOpens()
    {
        var id = await SeedLinkAsync(opens: 41);

        await _recorder.RecordOpenAsync(id, default);

        Assert.Equal(42, (await _db.ShareLinks.AsNoTracking().SingleAsync()).OpenCount);
    }

    [Fact]
    public async Task RecordOpen_IncrementsInSql_SoTwoOpensDoNotOverwriteEachOther()
    {
        // The reason this is ExecuteUpdateAsync and not load-mutate-save. A read-modify-write pair would
        // both read 0 and both write 1, undercounting exactly when the link is being opened a lot — which
        // is the case the number exists to reveal. Two sequential calls prove the SQL-side increment; a
        // read-modify-write over one stale tracked entity would land on 1.
        var id = await SeedLinkAsync();

        await _recorder.RecordOpenAsync(id, default);
        await _recorder.RecordOpenAsync(id, default);

        Assert.Equal(2, (await _db.ShareLinks.AsNoTracking().SingleAsync()).OpenCount);
    }

    [Fact]
    public async Task RecordOpen_TouchesOnlyTheLinkItWasGiven()
    {
        var mine = await SeedLinkAsync(trackId: new TrackId(1));
        var other = await SeedLinkAsync(trackId: new TrackId(2));

        await _recorder.RecordOpenAsync(mine, default);

        Assert.Equal(0, (await _db.ShareLinks.AsNoTracking().SingleAsync(s => s.ShareLinkId == other)).OpenCount);
    }

    [Fact]
    public async Task RecordOpen_ForALinkThatIsGone_DoesNotThrow()
    {
        // The sweep can delete a row between authentication and the page's render. Accounting must not turn
        // that race into a 500 on a page that already authorized and rendered.
        await _recorder.RecordOpenAsync(999_999, default);

        Assert.Empty(await _db.ShareLinks.ToListAsync());
    }
}
