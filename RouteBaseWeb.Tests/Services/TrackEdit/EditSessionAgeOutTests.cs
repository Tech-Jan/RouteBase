using RouteBaseWeb.Models;
using RouteBaseWeb.Services.TrackEdit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services.TrackEdit;

/// <summary>
/// Pins the B6 age-out: an ACTIVE edit session idle past <see cref="EditSessionAgeOutService.IdleDiscardThreshold"/>
/// is discarded and its ride checkout released (FK ON DELETE SET NULL); recent ACTIVE and any SAVED
/// session (commit provenance) are never touched.
/// </summary>
public class EditSessionAgeOutTests
{
    private const string Me = "me";

    private static async Task<(TrackEditTestDb Db, string SessionId)> CheckedOutSessionAsync()
    {
        var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(1), Me);
        await db.SeedRampAsync(new TrackId(1), 5);
        var created = await db.ServiceFor(Me).CreateAsync(new CreateSessionRequest([1]));
        return (db, created.Response!.SessionId);
    }

    private static Task BackdateAsync(TrackEditTestDb db, string sessionId, DateTime updatedAt) =>
        db.Context.Database.ExecuteSqlRawAsync(
            "UPDATE Activity_Edit_Session SET updated_at = {0} WHERE session_id = {1};",
            SqliteAdo.Fmt(updatedAt), sessionId);

    [Fact]
    public async Task IdlePastThreshold_DiscardedAndCheckoutReleased()
    {
        var (db, sessionId) = await CheckedOutSessionAsync();
        await using var _ = db;
        Assert.Equal(sessionId, (await db.ActivityAsync(new TrackId(1)))!.InEditSessionId); // checked out

        await BackdateAsync(db, sessionId,
            DateTime.UtcNow - EditSessionAgeOutService.IdleDiscardThreshold - TimeSpan.FromDays(1));

        var discarded = await db.Sessions.DiscardIdleActiveAsync(
            DateTime.UtcNow - EditSessionAgeOutService.IdleDiscardThreshold);

        Assert.Equal(1, discarded);
        Assert.Null(await db.Sessions.GetAsync(sessionId));
        Assert.Null((await db.ActivityAsync(new TrackId(1)))!.InEditSessionId); // checkout released by FK SET NULL
    }

    [Fact]
    public async Task RecentActive_Untouched()
    {
        var (db, sessionId) = await CheckedOutSessionAsync();
        await using var _ = db;

        await BackdateAsync(db, sessionId,
            DateTime.UtcNow - EditSessionAgeOutService.IdleDiscardThreshold + TimeSpan.FromDays(1));

        var discarded = await db.Sessions.DiscardIdleActiveAsync(
            DateTime.UtcNow - EditSessionAgeOutService.IdleDiscardThreshold);

        Assert.Equal(0, discarded);
        Assert.NotNull(await db.Sessions.GetAsync(sessionId));
        Assert.Equal(sessionId, (await db.ActivityAsync(new TrackId(1)))!.InEditSessionId);
    }

    [Fact]
    public async Task OldSavedSession_NeverAgedOut()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        var old = DateTime.UtcNow.AddDays(-100);
        await db.Sessions.CreateAsync(new EditSessionSnapshot(
            "saved-old", Me, new EditScript(1, [new LoadActivityOp(1)]), 1, [], "saved", old, old));

        var discarded = await db.Sessions.DiscardIdleActiveAsync(
            DateTime.UtcNow - EditSessionAgeOutService.IdleDiscardThreshold);

        Assert.Equal(0, discarded);
        Assert.NotNull(await db.Sessions.GetAsync("saved-old"));
    }
}
