using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Requests;

namespace RouteBaseWeb.Api;

[ApiController]
[Route("api/friends")]
public class FriendsApiController : ControllerBase
{
    private readonly IFriendshipService _friendshipService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<FriendsApiController> _logger;

    public FriendsApiController(
        IFriendshipService friendshipService,
        ICurrentUserService currentUser,
        ILogger<FriendsApiController> logger)
    {
        _friendshipService = friendshipService;
        _currentUser = currentUser;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType<List<FriendListItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFriends()
    {
        var userId = _currentUser.GetUserId();
        var friends = await _friendshipService.GetFriendsAsync(userId);
        return Ok(friends);
    }

    [HttpGet("pending-count")]
    [ProducesResponseType<PendingCountResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPendingCount()
    {
        var userId = _currentUser.GetUserId();
        var count = await _friendshipService.GetPendingRequestCountAsync(userId);
        return Ok(new PendingCountResponse(count));
    }

    [HttpPost("request")]
    [ProducesResponseType<FriendActionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SendRequest([FromBody] SendRequestBody body)
    {
        var userId = _currentUser.GetUserId();
        try
        {
            await _friendshipService.SendRequestAsync(userId, body.AddresseeId);
            return Ok(new FriendActionResponse("Friend request sent."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiError.BadRequest(ex.Message));
        }
    }

    [HttpPost("{id}/accept")]
    [RejectCrossSiteBrowserRequests] // route-only POSTs: cross-site form/fetch-able without preflight (PI-01)
    [ProducesResponseType<FriendActionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AcceptRequest(int id)
    {
        var userId = _currentUser.GetUserId();
        try
        {
            await _friendshipService.AcceptRequestAsync(userId, id);
            return Ok(new FriendActionResponse("Friend request accepted."));
        }
        catch (InvalidOperationException ex)
        {
            // API-07: missing/not-yours/wrong-status collapse to a bodiless 404 like the rest of the
            // API — the old 400 + distinguishing message let any user probe others' friendship rows.
            _logger.LogDebug("Friends accept {FriendshipId} rejected for {UserId}: {Reason}", id, userId, ex.Message);
            return NotFound();
        }
    }

    [HttpPost("{id}/decline")]
    [RejectCrossSiteBrowserRequests]
    [ProducesResponseType<FriendActionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> DeclineRequest(int id)
    {
        var userId = _currentUser.GetUserId();
        try
        {
            await _friendshipService.DeclineRequestAsync(userId, id);
            return Ok(new FriendActionResponse("Friend request declined."));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug("Friends decline {FriendshipId} rejected for {UserId}: {Reason}", id, userId, ex.Message);
            return NotFound(); // API-07 — see AcceptRequest
        }
    }

    [HttpDelete("{id}")]
    [ProducesResponseType<FriendActionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveFriend(int id)
    {
        var userId = _currentUser.GetUserId();
        try
        {
            await _friendshipService.RemoveFriendAsync(userId, id);
            return Ok(new FriendActionResponse("Friend removed."));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug("Friends remove {FriendshipId} rejected for {UserId}: {Reason}", id, userId, ex.Message);
            return NotFound(); // API-07 — see AcceptRequest
        }
    }

    [HttpGet("search")]
    [ProducesResponseType<List<UserSearchResult>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SearchUsers([FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(ApiError.BadRequest("Query parameter 'q' is required."));

        var userId = _currentUser.GetUserId();
        var results = await _friendshipService.SearchUsersAsync(q, userId);
        return Ok(results);
    }
}
