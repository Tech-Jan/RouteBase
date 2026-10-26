using System.Text.Json;
using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins the <see cref="FriendsApiController"/> contract: success bodies (FriendActionResponse /
/// PendingCountResponse / list DTOs), the error mapping — request/search keep 400 {error}, while
/// accept/decline/remove collapse missing/not-yours/wrong-status to a bodiless 404 (API-07: the old
/// distinguishing 400 was an existence/status oracle) — and the per-endpoint authorization rules
/// (addressee-only accept/decline, either-side remove, third parties rejected). Real
/// <see cref="FriendshipService"/> + <see cref="UserService"/>
/// over in-memory SQLite, like ActivitiesApiControllerTests; service-internal behavior (declined-reset,
/// search email privacy, friend-set memo) is already pinned by FriendshipServiceTests — not repeated here.
/// </summary>
public class FriendsApiControllerTests : IAsyncLifetime
{
    private const string Alice = "A";
    private const string Bob = "B";
    private const string Carol = "C";
    private const string Dave = "D";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private RouteBaseDbContext _context = null!;
    private FriendshipService _service = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:friends_api_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        foreach (var (id, name) in new[] { (Alice, "Alice"), (Bob, "Bob"), (Carol, "Carol"), (Dave, "Dave") })
#pragma warning disable CA1308 // seed email is genuine lowercase output (an address), not a normalization/comparison key
            _context.Users.Add(new AppUser { UserId = id, DisplayName = name, Email = $"{name.ToLowerInvariant()}@t.com", IsAdmin = false });
#pragma warning restore CA1308
        await _context.SaveChangesAsync();

        _service = new FriendshipService(
            _context, new UserService(_context, NullLogger<UserService>.Instance, TimeProvider.System),
            new UserCacheInvalidator(), NullLogger<FriendshipService>.Instance, TimeProvider.System);
    }

    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    private FriendsApiController ControllerAs(string userId) =>
        new(_service, new TestCurrentUserService(userId), NullLogger<FriendsApiController>.Instance);

    private async Task<int> SeedFriendshipAsync(string requester, string addressee, string status)
    {
        var now = DateTime.UtcNow.ToString("o");
        var f = new Friendship
        {
            RequesterId = requester, AddresseeId = addressee, Status = status,
            CreatedAt = now, UpdatedAt = now
        };
        _context.Friendships.Add(f);
        await _context.SaveChangesAsync();
        return f.FriendshipId;
    }

    private static T OkBody<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<T>(ok.Value);
    }

    /// <summary>Error bodies are the canonical <c>ApiError { code, message }</c> (D32) — serialize to read
    /// the human <c>message</c> the wire carries.</summary>
    private static string ErrorOf(IActionResult result)
    {
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var e = JsonDocument.Parse(JsonSerializer.Serialize(bad.Value, Web)).RootElement;
        return e.GetProperty("message").GetString()!;
    }

    // ─── GET /api/friends ───

    [Fact]
    public async Task GetFriends_ReturnsCounterpartOfAcceptedRowsInBothDirections()
    {
        var abId = await SeedFriendshipAsync(Alice, Bob, "accepted");     // Alice was requester
        var caId = await SeedFriendshipAsync(Carol, Alice, "accepted");   // Alice was addressee
        await SeedFriendshipAsync(Alice, Dave, "pending");                // not accepted → excluded
        await SeedFriendshipAsync(Bob, Carol, "accepted");                // not Alice's → excluded

        var friends = OkBody<List<FriendListItem>>(await ControllerAs(Alice).GetFriends());

        Assert.Equal(2, friends.Count);
        var bob = Assert.Single(friends, f => f.UserId == Bob);
        Assert.Equal("Bob", bob.DisplayName);
        Assert.Equal("bob@t.com", bob.Email);
        Assert.Equal(abId, bob.FriendshipId);
        var carol = Assert.Single(friends, f => f.UserId == Carol);
        Assert.Equal(caId, carol.FriendshipId);
    }

    [Fact]
    public async Task GetFriends_NoAcceptedFriendships_ReturnsEmptyList()
    {
        await SeedFriendshipAsync(Bob, Alice, "pending");
        await SeedFriendshipAsync(Alice, Carol, "declined");

        var friends = OkBody<List<FriendListItem>>(await ControllerAs(Alice).GetFriends());

        Assert.Empty(friends);
    }

    // ─── GET /api/friends/pending-count ───

    [Fact]
    public async Task PendingCount_CountsOnlyIncomingPendingRequests()
    {
        await SeedFriendshipAsync(Bob, Alice, "pending");      // incoming → counts
        await SeedFriendshipAsync(Carol, Alice, "declined");   // not pending
        await SeedFriendshipAsync(Alice, Dave, "pending");     // outgoing → must not count

        var body = OkBody<PendingCountResponse>(await ControllerAs(Alice).GetPendingCount());

        Assert.Equal(1, body.Count);
    }

    // ─── POST /api/friends/request ───

    [Fact]
    public async Task SendRequest_Success_Returns200AndPersistsPendingRow()
    {
        var result = await ControllerAs(Alice).SendRequest(new SendRequestBody { AddresseeId = Bob });

        Assert.Equal("Friend request sent.", OkBody<FriendActionResponse>(result).Message);
        var row = await _context.Friendships.SingleAsync();
        Assert.Equal(Alice, row.RequesterId);
        Assert.Equal(Bob, row.AddresseeId);
        Assert.Equal("pending", row.Status);
    }

    [Fact]
    public async Task SendRequest_ToSelf_Returns400AndPersistsNothing()
    {
        var result = await ControllerAs(Alice).SendRequest(new SendRequestBody { AddresseeId = Alice });

        Assert.Equal("Cannot send a friend request to yourself.", ErrorOf(result));
        Assert.Empty(await _context.Friendships.ToListAsync());
    }

    [Fact]
    public async Task SendRequest_UnknownAddressee_Returns400()
    {
        var result = await ControllerAs(Alice).SendRequest(new SendRequestBody { AddresseeId = "ghost" });

        Assert.Equal("User not found.", ErrorOf(result));
    }

    [Fact]
    public async Task SendRequest_WhenAlreadyFriends_Returns400EvenAcrossDirections()
    {
        // Accepted row in the OPPOSITE direction — the duplicate check must see both.
        await SeedFriendshipAsync(Bob, Alice, "accepted");

        var result = await ControllerAs(Alice).SendRequest(new SendRequestBody { AddresseeId = Bob });

        Assert.Equal("Already friends.", ErrorOf(result));
        Assert.Equal("accepted", (await _context.Friendships.SingleAsync()).Status);
    }

    [Fact]
    public async Task SendRequest_WhenRequestAlreadyPending_Returns400()
    {
        await SeedFriendshipAsync(Alice, Bob, "pending");

        var result = await ControllerAs(Alice).SendRequest(new SendRequestBody { AddresseeId = Bob });

        Assert.Equal("A friend request already exists.", ErrorOf(result));
    }

    // ─── POST /api/friends/{id}/accept ───

    [Fact]
    public async Task Accept_ByAddressee_Returns200AndMarksAccepted()
    {
        var id = await SeedFriendshipAsync(Bob, Alice, "pending");

        var result = await ControllerAs(Alice).AcceptRequest(id);

        Assert.Equal("Friend request accepted.", OkBody<FriendActionResponse>(result).Message);
        Assert.Equal("accepted", (await _context.Friendships.SingleAsync()).Status);
    }

    [Fact]
    public async Task Accept_ByRequester_Returns404AndStaysPending()
    {
        var id = await SeedFriendshipAsync(Bob, Alice, "pending");

        var result = await ControllerAs(Bob).AcceptRequest(id);

        Assert.IsType<NotFoundResult>(result); // bodiless — no "not yours" tell
        Assert.Equal("pending", (await _context.Friendships.SingleAsync()).Status);
    }

    [Fact]
    public async Task Accept_UnknownId_Returns404()
    {
        var result = await ControllerAs(Alice).AcceptRequest(999);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Accept_NonPendingRequest_Returns404WithoutStatusTell()
    {
        var id = await SeedFriendshipAsync(Bob, Alice, "declined");

        var result = await ControllerAs(Alice).AcceptRequest(id);

        Assert.IsType<NotFoundResult>(result); // same 404 as missing — status not disclosed
        Assert.Equal("declined", (await _context.Friendships.SingleAsync()).Status);
    }

    // ─── POST /api/friends/{id}/decline ───

    [Fact]
    public async Task Decline_ByAddressee_Returns200AndMarksDeclined()
    {
        var id = await SeedFriendshipAsync(Bob, Alice, "pending");

        var result = await ControllerAs(Alice).DeclineRequest(id);

        Assert.Equal("Friend request declined.", OkBody<FriendActionResponse>(result).Message);
        Assert.Equal("declined", (await _context.Friendships.SingleAsync()).Status);
    }

    [Fact]
    public async Task Decline_ByThirdParty_Returns404AndStaysPending()
    {
        var id = await SeedFriendshipAsync(Bob, Alice, "pending");

        var result = await ControllerAs(Carol).DeclineRequest(id);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal("pending", (await _context.Friendships.SingleAsync()).Status);
    }

    // ─── DELETE /api/friends/{id} ───

    [Fact]
    public async Task Remove_ByRequester_Returns200AndDeletesRow()
    {
        var id = await SeedFriendshipAsync(Alice, Bob, "accepted");

        var result = await ControllerAs(Alice).RemoveFriend(id);

        Assert.Equal("Friend removed.", OkBody<FriendActionResponse>(result).Message);
        Assert.Empty(await _context.Friendships.ToListAsync());
    }

    [Fact]
    public async Task Remove_ByAddressee_Returns200AndDeletesRow()
    {
        var id = await SeedFriendshipAsync(Alice, Bob, "accepted");

        var result = await ControllerAs(Bob).RemoveFriend(id);

        Assert.Equal("Friend removed.", OkBody<FriendActionResponse>(result).Message);
        Assert.Empty(await _context.Friendships.ToListAsync());
    }

    [Fact]
    public async Task Remove_ByThirdParty_Returns404AndRowRemains()
    {
        var id = await SeedFriendshipAsync(Alice, Bob, "accepted");

        var result = await ControllerAs(Carol).RemoveFriend(id);

        Assert.IsType<NotFoundResult>(result);
        Assert.Single(await _context.Friendships.ToListAsync());
    }

    [Fact]
    public async Task Remove_UnknownId_Returns404()
    {
        var result = await ControllerAs(Alice).RemoveFriend(999);

        Assert.IsType<NotFoundResult>(result);
    }

    // ─── GET /api/friends/search ───

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Search_MissingOrWhitespaceQuery_Returns400(string? q)
    {
        var result = await ControllerAs(Alice).SearchUsers(q!);

        Assert.Equal("Query parameter 'q' is required.", ErrorOf(result));
    }

    [Fact]
    public async Task Search_MapsFriendshipStatusPerResult()
    {
        await SeedFriendshipAsync(Bob, Alice, "accepted");   // opposite direction must still map
        await SeedFriendshipAsync(Alice, Carol, "pending");

        var controller = ControllerAs(Alice);

        var bob = Assert.Single(OkBody<List<UserSearchResult>>(await controller.SearchUsers("bob")));
        Assert.Equal(Bob, bob.UserId);
        Assert.Equal("accepted", bob.FriendshipStatus);

        var carol = Assert.Single(OkBody<List<UserSearchResult>>(await controller.SearchUsers("carol")));
        Assert.Equal("pending", carol.FriendshipStatus);

        var dave = Assert.Single(OkBody<List<UserSearchResult>>(await controller.SearchUsers("dave")));
        Assert.Equal("none", dave.FriendshipStatus);
    }

    [Fact]
    public async Task Search_ExcludesTheSearcherThemselves()
    {
        var results = OkBody<List<UserSearchResult>>(await ControllerAs(Alice).SearchUsers("alice"));

        Assert.Empty(results);
    }
}
