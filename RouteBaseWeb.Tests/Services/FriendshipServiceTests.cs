using System.Data.Common;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// First coverage for FriendshipService. SH-05 pins SendRequest's addressee-existence + declined-reset
/// behavior; SH-03 (search email privacy) adds to the same fixture. Real in-memory SQLite.
/// </summary>
public class FriendshipServiceTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;
    private FriendshipService _service = null!;
    private UserService _userService = null!;
    private UserCacheInvalidator _cacheInvalidator = null!;
    private FriendshipQueryCounter _counter = null!;

    /// <summary>Counts the SELECTs that actually reached SQLite against the Friendship table — the only way
    /// to prove an N+1 is gone (LV-07). Observes; it changes nothing about what any test does.</summary>
    private sealed class FriendshipQueryCounter : DbCommandInterceptor
    {
        public int Reads;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            if (command.CommandText.Contains("FROM \"Friendship\"", StringComparison.Ordinal)) Reads++;
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken ct = default)
        {
            if (command.CommandText.Contains("FROM \"Friendship\"", StringComparison.Ordinal)) Reads++;
            return ValueTask.FromResult(result);
        }
    }

    public async Task InitializeAsync()
    {
        var connectionString = $"Data Source=file:testdb_{Guid.NewGuid():N}?mode=memory&cache=shared";
        _counter = new FriendshipQueryCounter();
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite(connectionString).AddInterceptors(_counter).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        _userService = new UserService(_context, NullLogger<UserService>.Instance, TimeProvider.System);
        _cacheInvalidator = new UserCacheInvalidator();
        _service = new FriendshipService(
            _context, _userService, _cacheInvalidator, NullLogger<FriendshipService>.Instance, TimeProvider.System);
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    private async Task SeedUserAsync(string userId, string? displayName, string? email)
    {
        _context.Users.Add(new AppUser
        {
            UserId      = userId,
            DisplayName = displayName,
            Email       = email,
            IsAdmin     = false,
            CreatedAt   = DateTime.UtcNow.ToString("O"),
            LastLogin   = DateTime.UtcNow.ToString("O"),
        });
        await _context.SaveChangesAsync();
    }

    /// <summary>Seeds a Friendship row directly (bypassing the service) so a test can set an arbitrary
    /// status and control CreatedAt/UpdatedAt for ordering assertions. Returns the assigned id.</summary>
    private async Task<int> SeedFriendshipAsync(
        string requesterId, string addresseeId, string status,
        string? createdAt = null, string? updatedAt = null)
    {
        var now = DateTime.UtcNow.ToString("o");
        var f = new Friendship
        {
            RequesterId = requesterId,
            AddresseeId = addresseeId,
            Status = status,
            CreatedAt = createdAt ?? now,
            UpdatedAt = updatedAt ?? now
        };
        _context.Friendships.Add(f);
        await _context.SaveChangesAsync();
        return f.FriendshipId;
    }

    // ── SH-05 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendRequest_UnknownAddressee_ThrowsUserNotFound()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.SendRequestAsync("A", "ghost"));
        Assert.Equal("User not found.", ex.Message);
    }

    [Fact]
    public async Task SendRequest_EmptyAddressee_ThrowsUserNotFound()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.SendRequestAsync("A", "   "));
        Assert.Equal("User not found.", ex.Message);
    }

    [Fact]
    public async Task SendRequest_Self_Throws()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.SendRequestAsync("A", "A"));
        Assert.Equal("Cannot send a friend request to yourself.", ex.Message);
    }

    [Fact]
    public async Task SendRequest_Duplicate_Throws()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");

        await _service.SendRequestAsync("A", "B");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.SendRequestAsync("A", "B"));
        Assert.Equal("A friend request already exists.", ex.Message);
    }

    [Fact]
    public async Task SendRequest_AfterDecline_ResetsRowToPending()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");

        // B previously asked A and A declined (opposite direction).
        var now = DateTime.UtcNow.ToString("o");
        _context.Friendships.Add(new Friendship
        {
            RequesterId = "B", AddresseeId = "A", Status = "declined", CreatedAt = now, UpdatedAt = now
        });
        await _context.SaveChangesAsync();

        // A now sends to B → same row is reset to pending with the direction flipped to A→B.
        await _service.SendRequestAsync("A", "B");

        var row = await _context.Friendships.SingleAsync();
        Assert.Equal("pending", row.Status);
        Assert.Equal("A", row.RequesterId);
        Assert.Equal("B", row.AddresseeId);
    }

    // ── SH-03 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchUsers_NonExactEmailMatch_EmailNull()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");

        // Alice searches by Bob's display name — a name hit must not leak his email.
        var results = await _service.SearchUsersAsync("Bob", "A");

        var bob = Assert.Single(results);
        Assert.Equal("B", bob.UserId);
        Assert.Null(bob.Email);
    }

    [Fact]
    public async Task SearchUsers_ExactEmailMatch_EmailPopulated()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");

        var results = await _service.SearchUsersAsync("bob@test.com", "A");

        var bob = Assert.Single(results);
        Assert.Equal("B", bob.UserId);
        Assert.Equal("bob@test.com", bob.Email);
    }

    // ── LV-07: the friend-set N+1 ──────────────────────────────────────────
    // CanViewItemAsync asks for the accepted-friend set once PER ITEM, so a LiveView page with 8 watched
    // sessions issued 8 identical Friendships queries — and the same loop runs on Activities, Photos and
    // Segments. The set cannot change within a request except through this service's own writes, so it is
    // memoized for the (scoped) request lifetime.

    private async Task MakeFriendsAsync(string a, string b)
    {
        await _service.SendRequestAsync(a, b);
        var pending = await _context.Friendships.SingleAsync(f => f.RequesterId == a && f.AddresseeId == b);
        await _service.AcceptRequestAsync(b, pending.FriendshipId);
    }

    [Fact]
    public async Task GetAcceptedFriendIds_EightCalls_QueriesFriendshipsOnce()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");
        await MakeFriendsAsync("A", "B");

        _counter.Reads = 0;
        for (int i = 0; i < 8; i++)
        {
            var ids = await _service.GetAcceptedFriendIdsAsync("A");
            Assert.Equal(["B"], ids);
        }

        Assert.Equal(1, _counter.Reads);   // was 8
    }

    // The invalidation pin — this is what makes the memo safe. A request that changes a friendship and then
    // re-reads the set must see the change, not the set it memoized moments earlier.
    [Fact]
    public async Task GetAcceptedFriendIds_AfterAFriendshipWrite_ReturnsTheNewSet()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");
        await SeedUserAsync("C", "Carol", "carol@test.com");
        await MakeFriendsAsync("A", "B");

        Assert.Equal(["B"], await _service.GetAcceptedFriendIdsAsync("A"));   // memo is now warm

        await MakeFriendsAsync("A", "C");

        var after = await _service.GetAcceptedFriendIdsAsync("A");
        Assert.Equal(["B", "C"], after.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal);

        // ...and a removal is seen too.
        var ab = await _context.Friendships.SingleAsync(f =>
            (f.RequesterId == "A" && f.AddresseeId == "B") || (f.RequesterId == "B" && f.AddresseeId == "A"));
        await _service.RemoveFriendAsync("A", ab.FriendshipId);

        Assert.Equal(["C"], await _service.GetAcceptedFriendIdsAsync("A"));
    }

    // The memo is keyed by user id, so a second caller in the same scope cannot be handed the first one's set.
    [Fact]
    public async Task GetAcceptedFriendIds_ForADifferentUser_DoesNotReuseTheMemo()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");
        await SeedUserAsync("C", "Carol", "carol@test.com");
        await MakeFriendsAsync("A", "B");

        Assert.Equal(["B"], await _service.GetAcceptedFriendIdsAsync("A"));
        Assert.Equal(["A"], await _service.GetAcceptedFriendIdsAsync("B"));
        Assert.Empty(await _service.GetAcceptedFriendIdsAsync("C"));
    }

    // The core accepted-set filter: only "accepted" rows count, and the OTHER party is returned regardless
    // of which side of the row the caller is on. Pending/declined must never leak into the friend set.
    [Fact]
    public async Task GetAcceptedFriendIds_ExcludesNonAcceptedStatuses_BothDirections()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");
        await SeedUserAsync("C", "Carol", "carol@test.com");
        await SeedUserAsync("D", "Dave", "dave@test.com");

        await SeedFriendshipAsync("B", "A", "accepted");   // A is the addressee here
        await SeedFriendshipAsync("A", "C", "pending");    // must not count
        await SeedFriendshipAsync("D", "A", "declined");   // must not count

        Assert.Equal(["B"], await _service.GetAcceptedFriendIdsAsync("A"));
    }

    // ── GetFriendsAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetFriends_ReturnsAcceptedBothDirections_WithOtherPartyDetailsAndFriendsSince()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");
        await SeedUserAsync("C", "Carol", "carol@test.com");

        // A is requester of B, addressee of C — both accepted. The projection must return the *other* user.
        var idAB = await SeedFriendshipAsync("A", "B", "accepted", updatedAt: "2026-03-01T00:00:00.0000000Z");
        await SeedFriendshipAsync("C", "A", "accepted");

        var friends = await _service.GetFriendsAsync("A");

        Assert.Equal(2, friends.Count);
        var bob = friends.Single(f => f.UserId == "B");
        Assert.Equal("Bob", bob.DisplayName);
        Assert.Equal("bob@test.com", bob.Email);
        Assert.Equal(idAB, bob.FriendshipId);
        Assert.Equal("2026-03-01T00:00:00.0000000Z", bob.FriendsSince);   // FriendsSince = UpdatedAt
        Assert.Contains(friends, f => f.UserId == "C");                    // addressee-side friend too
    }

    [Fact]
    public async Task GetFriends_ExcludesPendingAndDeclined()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");
        await SeedUserAsync("C", "Carol", "carol@test.com");

        await SeedFriendshipAsync("A", "B", "pending");
        await SeedFriendshipAsync("A", "C", "declined");

        Assert.Empty(await _service.GetFriendsAsync("A"));
    }

    // ── GetPendingRequestsAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetPendingRequests_ReturnsIncomingPending_NewestFirst_WithRequesterDetails()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");
        await SeedUserAsync("C", "Carol", "carol@test.com");

        // Both B and C ask A. C's request is newer, so it must sort first (OrderByDescending CreatedAt).
        await SeedFriendshipAsync("B", "A", "pending", createdAt: "2026-01-01T00:00:00.0000000Z");
        var idCA = await SeedFriendshipAsync("C", "A", "pending", createdAt: "2026-02-01T00:00:00.0000000Z");

        var pending = await _service.GetPendingRequestsAsync("A");

        Assert.Equal(2, pending.Count);
        Assert.Equal("C", pending[0].UserId);                 // newest first
        Assert.Equal(idCA, pending[0].FriendshipId);
        Assert.Equal("Carol", pending[0].DisplayName);        // requester's details, not the addressee's
        Assert.Equal("carol@test.com", pending[0].Email);
        Assert.Equal("2026-02-01T00:00:00.0000000Z", pending[0].RequestedAt);
        Assert.Equal("B", pending[1].UserId);
    }

    [Fact]
    public async Task GetPendingRequests_ExcludesOutgoingAcceptedAndDeclined()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");
        await SeedUserAsync("C", "Carol", "carol@test.com");
        await SeedUserAsync("D", "Dave", "dave@test.com");

        await SeedFriendshipAsync("A", "B", "pending");    // outgoing — A is requester, not addressee
        await SeedFriendshipAsync("C", "A", "accepted");   // already friends
        await SeedFriendshipAsync("D", "A", "declined");   // declined

        Assert.Empty(await _service.GetPendingRequestsAsync("A"));
    }

    // ── GetPendingRequestCountAsync ─────────────────────────────────────────

    [Fact]
    public async Task GetPendingRequestCount_CountsIncomingPendingOnly()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");
        await SeedUserAsync("C", "Carol", "carol@test.com");
        await SeedUserAsync("D", "Dave", "dave@test.com");

        await SeedFriendshipAsync("B", "A", "pending");    // counts
        await SeedFriendshipAsync("C", "A", "pending");    // counts
        await SeedFriendshipAsync("A", "D", "pending");    // outgoing — does NOT count
        await SeedFriendshipAsync("D", "A", "accepted");   // accepted — does NOT count

        Assert.Equal(2, await _service.GetPendingRequestCountAsync("A"));
    }

    [Fact]
    public async Task GetPendingRequestCount_None_ReturnsZero()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        Assert.Equal(0, await _service.GetPendingRequestCountAsync("A"));
    }

    // ── SendRequestAsync — happy path & remaining status branches ────────────

    [Fact]
    public async Task SendRequest_HappyPath_CreatesPendingRow()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");

        await _service.SendRequestAsync("A", "B");

        var row = await _context.Friendships.SingleAsync();
        Assert.Equal("A", row.RequesterId);
        Assert.Equal("B", row.AddresseeId);
        Assert.Equal("pending", row.Status);
        Assert.False(string.IsNullOrEmpty(row.CreatedAt));
        Assert.Equal(row.CreatedAt, row.UpdatedAt);   // set together at insert
    }

    [Fact]
    public async Task SendRequest_WhenAlreadyFriends_Throws_EitherDirection()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");
        await SeedFriendshipAsync("A", "B", "accepted");

        var forward = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.SendRequestAsync("A", "B"));
        Assert.Equal("Already friends.", forward.Message);

        // The existing-relationship check is bidirectional, so the reverse direction is blocked too.
        var reverse = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.SendRequestAsync("B", "A"));
        Assert.Equal("Already friends.", reverse.Message);
    }

    // Defensive branch: no product path writes "blocked" today, but SendRequest guards it. Pin current
    // behavior so a future path that starts writing "blocked" can't silently fall through to an insert.
    [Fact]
    public async Task SendRequest_WhenBlocked_Throws()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");
        await SeedFriendshipAsync("A", "B", "blocked");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.SendRequestAsync("A", "B"));
        Assert.Equal("Cannot send request.", ex.Message);
    }

    // ── AcceptRequestAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task AcceptRequest_NotFound_Throws()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AcceptRequestAsync("A", 9999));
        Assert.Equal("Friend request not found.", ex.Message);
    }

    [Fact]
    public async Task AcceptRequest_ByNonAddressee_Throws()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");
        var id = await SeedFriendshipAsync("A", "B", "pending");   // only B (addressee) may accept

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AcceptRequestAsync("A", id));           // A is the requester
        Assert.Equal("Only the addressee can accept a request.", ex.Message);

        var row = await _context.Friendships.FindAsync(id);
        Assert.Equal("pending", row!.Status);   // unchanged
    }

    [Fact]
    public async Task AcceptRequest_NonPending_Throws()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");
        var id = await SeedFriendshipAsync("A", "B", "accepted");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AcceptRequestAsync("B", id));
        Assert.Equal("Cannot accept a request with status 'accepted'.", ex.Message);
    }

    [Fact]
    public async Task AcceptRequest_HappyPath_SetsAccepted_AndInvalidatesBothCaches()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");
        var id = await SeedFriendshipAsync("A", "B", "pending", updatedAt: "2020-01-01T00:00:00.0000000Z");

        var tokenA = _cacheInvalidator.GetChangeToken("A");
        var tokenB = _cacheInvalidator.GetChangeToken("B");

        await _service.AcceptRequestAsync("B", id);

        var row = await _context.Friendships.FindAsync(id);
        Assert.Equal("accepted", row!.Status);
        Assert.NotEqual("2020-01-01T00:00:00.0000000Z", row.UpdatedAt, StringComparer.Ordinal);   // UpdatedAt refreshed
        Assert.True(tokenA.HasChanged);   // both parties' cache entries evicted
        Assert.True(tokenB.HasChanged);
    }

    // ── DeclineRequestAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task DeclineRequest_NotFound_Throws()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.DeclineRequestAsync("A", 9999));
        Assert.Equal("Friend request not found.", ex.Message);
    }

    [Fact]
    public async Task DeclineRequest_ByNonAddressee_Throws()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");
        var id = await SeedFriendshipAsync("A", "B", "pending");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.DeclineRequestAsync("A", id));   // A is the requester, not the addressee
        Assert.Equal("Only the addressee can decline a request.", ex.Message);
    }

    [Fact]
    public async Task DeclineRequest_NonPending_Throws()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");
        var id = await SeedFriendshipAsync("A", "B", "accepted");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.DeclineRequestAsync("B", id));
        Assert.Equal("Cannot decline a request with status 'accepted'.", ex.Message);
    }

    [Fact]
    public async Task DeclineRequest_HappyPath_SetsDeclined_AndDoesNotInvalidateCache()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");
        var id = await SeedFriendshipAsync("A", "B", "pending");

        var tokenA = _cacheInvalidator.GetChangeToken("A");
        var tokenB = _cacheInvalidator.GetChangeToken("B");

        await _service.DeclineRequestAsync("B", id);

        var row = await _context.Friendships.FindAsync(id);
        Assert.Equal("declined", row!.Status);
        // Declining a pending request changes no one's visible-friend set, so no cache eviction fires
        // (only Accept and Remove touch the invalidator).
        Assert.False(tokenA.HasChanged);
        Assert.False(tokenB.HasChanged);
    }

    // ── RemoveFriendAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task RemoveFriend_NotFound_Throws()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RemoveFriendAsync("A", 9999));
        Assert.Equal("Friendship not found.", ex.Message);
    }

    [Fact]
    public async Task RemoveFriend_ByThirdParty_Throws()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");
        await SeedUserAsync("C", "Carol", "carol@test.com");
        var id = await SeedFriendshipAsync("A", "B", "accepted");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RemoveFriendAsync("C", id));   // C is neither party
        Assert.Equal("Not authorized to remove this friendship.", ex.Message);

        Assert.NotNull(await _context.Friendships.FindAsync(id));   // still there
    }

    [Fact]
    public async Task RemoveFriend_ByAddressee_DeletesRow_AndInvalidatesBothCaches()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");
        // A initiated; the addressee B removes it — the non-initiator is authorized too.
        var id = await SeedFriendshipAsync("A", "B", "accepted");

        var tokenA = _cacheInvalidator.GetChangeToken("A");
        var tokenB = _cacheInvalidator.GetChangeToken("B");

        await _service.RemoveFriendAsync("B", id);

        Assert.Null(await _context.Friendships.FindAsync(id));
        Assert.True(tokenA.HasChanged);
        Assert.True(tokenB.HasChanged);
    }

    [Fact]
    public async Task RemoveFriend_ByRequester_DeletesRow()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");
        var id = await SeedFriendshipAsync("A", "B", "accepted");

        await _service.RemoveFriendAsync("A", id);

        Assert.Null(await _context.Friendships.FindAsync(id));
    }

    // ── SearchUsersAsync — status mapping & scoping ─────────────────────────

    [Fact]
    public async Task SearchUsers_FriendshipStatus_ReflectsExistingRelationship()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");
        await SeedUserAsync("C", "Carol", "carol@test.com");
        await SeedUserAsync("D", "Dave", "dave@test.com");

        await SeedFriendshipAsync("A", "B", "accepted");
        await SeedFriendshipAsync("A", "C", "pending");
        // D has no relationship with A.

        Assert.Equal("accepted", (Assert.Single(await _service.SearchUsersAsync("Bob", "A"))).FriendshipStatus);
        Assert.Equal("pending", (Assert.Single(await _service.SearchUsersAsync("Carol", "A"))).FriendshipStatus);
        Assert.Equal("none", (Assert.Single(await _service.SearchUsersAsync("Dave", "A"))).FriendshipStatus);
    }

    [Fact]
    public async Task SearchUsers_ExcludesTheSearcher()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");

        // Alice searches her own exact name; excludeUserId drops her from her own results.
        Assert.Empty(await _service.SearchUsersAsync("Alice", "A"));
    }

    [Fact]
    public async Task SearchUsers_BlankQuery_ReturnsEmpty()
    {
        await SeedUserAsync("A", "Alice", "alice@test.com");
        await SeedUserAsync("B", "Bob", "bob@test.com");

        Assert.Empty(await _service.SearchUsersAsync("   ", "A"));
    }
}
