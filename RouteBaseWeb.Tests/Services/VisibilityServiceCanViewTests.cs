using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Per-item authorization tests for <see cref="VisibilityService.CanViewItemAsync"/> (RC-01 / INC-03).
/// Uses the real service with stubbed current-user + friendship deps and a null DbContext —
/// CanViewItemAsync must never touch the context.
/// </summary>
public class VisibilityServiceCanViewTests
{
    private const string Me = "me";
    private const string Friend = "friend";
    private const string Stranger = "stranger";

    private static VisibilityService Service(params string[] myFriends) =>
        new(new TestCurrentUserService(Me), new StubFriendshipService(myFriends), null!);

    [Fact]
    public async Task CanView_Owner_PrivateItem_True()
        => Assert.True(await Service().CanViewItemAsync(Me, "private"));

    [Fact]
    public async Task CanView_Friend_PrivateItem_False() // the INC-03 leak
        => Assert.False(await Service(Friend).CanViewItemAsync(Friend, "private"));

    [Fact]
    public async Task CanView_Friend_FriendsItem_True()
        => Assert.True(await Service(Friend).CanViewItemAsync(Friend, "friends"));

    [Fact]
    public async Task CanView_Friend_PublicItem_True()
        => Assert.True(await Service(Friend).CanViewItemAsync(Friend, "public"));

    [Fact]
    public async Task CanView_Stranger_FriendsItem_False()
        => Assert.False(await Service().CanViewItemAsync(Stranger, "friends"));

    [Fact]
    public async Task CanView_Stranger_PublicItem_True()
        => Assert.True(await Service().CanViewItemAsync(Stranger, "public"));

    [Fact]
    public async Task CanView_UnknownVisibilityValue_NonOwner_False()
        => Assert.False(await Service(Friend).CanViewItemAsync(Friend, "Friends")); // capitalized → deny

    // ── CR5-06: CanViewActivityAsync — the activity review gate encoded once (AS-01) ──
    [Fact]
    public async Task CanViewActivity_Owner_PendingActivity_True() // review slice needs its own access
    {
        await using var h = await Harness.NewAsync((new TrackId(1), Me, "private", LifecycleStates.InWork));
        Assert.True(await h.ServiceFor().CanViewActivityAsync(new TrackId(1)));
    }

    [Fact]
    public async Task CanViewActivity_Friend_PendingActivity_False() // gate hides pending from non-owners
    {
        await using var h = await Harness.NewAsync((new TrackId(2), Friend, "friends", LifecycleStates.InWork));
        Assert.False(await h.ServiceFor(Friend).CanViewActivityAsync(new TrackId(2)));
    }

    [Fact]
    public async Task CanViewActivity_Friend_ConfirmedFriendsActivity_True()
    {
        await using var h = await Harness.NewAsync((new TrackId(3), Friend, "friends", LifecycleStates.Released));
        Assert.True(await h.ServiceFor(Friend).CanViewActivityAsync(new TrackId(3)));
    }

    [Fact]
    public async Task CanViewActivity_Stranger_ConfirmedPublicActivity_True()
    {
        await using var h = await Harness.NewAsync((new TrackId(4), Stranger, "public", LifecycleStates.Released));
        Assert.True(await h.ServiceFor().CanViewActivityAsync(new TrackId(4)));
    }

    [Fact]
    public async Task CanViewActivity_Stranger_ConfirmedFriendsActivity_False() // not my friend → denied
    {
        await using var h = await Harness.NewAsync((new TrackId(5), Stranger, "friends", LifecycleStates.Released));
        Assert.False(await h.ServiceFor(Friend).CanViewActivityAsync(new TrackId(5)));
    }

    [Fact]
    public async Task CanViewActivity_UnknownTrack_False()
    {
        await using var h = await Harness.NewAsync();
        Assert.False(await h.ServiceFor().CanViewActivityAsync(new TrackId(999)));
    }

    /// <summary>In-memory SQLite (one kept-open connection) seeded with activities, for the DB-touching
    /// CanViewActivityAsync gate. The CanViewItemAsync tests above pass a null context (never touched).</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _conn;
        private readonly RouteBaseDbContext _db;
        private Harness(SqliteConnection conn, RouteBaseDbContext db) { _conn = conn; _db = db; }

        public static async Task<Harness> NewAsync(
            params (TrackId trackId, string owner, string visibility, string lifecycle)[] acts)
        {
            var conn = new SqliteConnection("Data Source=:memory:");
            await conn.OpenAsync();
            var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
            var db = new RouteBaseDbContext(options);
            await db.Database.EnsureCreatedAsync();
            // Activity.UserId → Users is an enforced FK; seed an AppUser for each distinct owner.
            foreach (var owner in acts.Select(a => a.owner).Distinct(StringComparer.Ordinal))
                db.Users.Add(new AppUser { UserId = owner, DisplayName = owner, Email = $"{owner}@t.com", IsAdmin = false });
            foreach (var (trackId, owner, visibility, lifecycle) in acts)
                db.Activities.Add(new Activity
                {
                    TrackId = trackId, UserId = owner, ActivityType = "Ride",
                    Source = "auto-recording", Visibility = visibility, Lifecycle = lifecycle,
                });
            await db.SaveChangesAsync();
            return new Harness(conn, db);
        }

        // Current user is always Me; args are Me's accepted friends.
        public VisibilityService ServiceFor(params string[] myFriends)
            => new(new TestCurrentUserService(Me), new StubFriendshipService(myFriends), _db);

        public async ValueTask DisposeAsync()
        {
            await _db.DisposeAsync();
            await _conn.DisposeAsync();
        }
    }

    private sealed class StubFriendshipService : IFriendshipService
    {
        private readonly List<string> _friends;
        public StubFriendshipService(string[] friends) => _friends = friends.ToList();

        public Task<List<string>> GetAcceptedFriendIdsAsync(string userId) => Task.FromResult(_friends);

        public Task<List<FriendListItem>> GetFriendsAsync(string userId) => throw new NotImplementedException();
        public Task<List<FriendRequestItem>> GetPendingRequestsAsync(string userId) => throw new NotImplementedException();
        public Task<int> GetPendingRequestCountAsync(string userId) => throw new NotImplementedException();
        public Task SendRequestAsync(string requesterId, string addresseeId) => throw new NotImplementedException();
        public Task AcceptRequestAsync(string currentUserId, int friendshipId) => throw new NotImplementedException();
        public Task DeclineRequestAsync(string currentUserId, int friendshipId) => throw new NotImplementedException();
        public Task RemoveFriendAsync(string currentUserId, int friendshipId) => throw new NotImplementedException();
        public Task<List<UserSearchResult>> SearchUsersAsync(string query, string excludeUserId) => throw new NotImplementedException();
    }
}
