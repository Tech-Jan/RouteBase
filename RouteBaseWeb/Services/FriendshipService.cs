using System.Globalization;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services;

public class FriendshipService : IFriendshipService
{
    private readonly RouteBaseDbContext _context;
    private readonly IUserService _userService;
    private readonly UserCacheInvalidator _cacheInvalidator;
    private readonly ILogger<FriendshipService> _logger;
    private readonly TimeProvider _clock;

    // LV-07: CanViewItemAsync asks for the friend set once PER ITEM, so a single visibility loop (LiveView's
    // 8 sessions, and every Activities/Photos/Segments loop) re-ran the same Friendships query N times. The
    // service is SCOPED — one request — so the set cannot change under the memo except through this class's
    // own writes, and every one of those nulls it below.
    private List<string>? _acceptedIdsMemo;
    private string? _acceptedIdsMemoUser;

    public FriendshipService(
        RouteBaseDbContext context,
        IUserService userService,
        UserCacheInvalidator cacheInvalidator,
        ILogger<FriendshipService> logger,
        TimeProvider clock)
    {
        _context = context;
        _userService = userService;
        _cacheInvalidator = cacheInvalidator;
        _logger = logger;
        _clock = clock;
    }

    public async Task<List<string>> GetAcceptedFriendIdsAsync(string userId)
    {
        // Keyed by the id, not a bare flag: a caller asking about a different user must never get the
        // memoized set of the previous one.
        if (_acceptedIdsMemo is not null && _acceptedIdsMemoUser == userId)
            return _acceptedIdsMemo;

        var ids = await _context.Friendships
            .Where(f => f.Status == FriendshipStatuses.Accepted
                && (f.RequesterId == userId || f.AddresseeId == userId))
            .Select(f => f.RequesterId == userId ? f.AddresseeId : f.RequesterId)
            .ToListAsync();

        _acceptedIdsMemo = ids;
        _acceptedIdsMemoUser = userId;
        return ids;
    }

    /// <summary>Called after every write in this class: a request that changes a friendship and then re-reads
    /// the set must see the change, not the set it memoized a moment earlier.</summary>
    private void InvalidateAcceptedIdsMemo()
    {
        _acceptedIdsMemo = null;
        _acceptedIdsMemoUser = null;
    }

    public async Task<List<FriendListItem>> GetFriendsAsync(string userId)
    {
        var friendships = await _context.Friendships
            .AsNoTracking()
            .Include(f => f.Requester)
            .Include(f => f.Addressee)
            .Where(f => f.Status == FriendshipStatuses.Accepted
                && (f.RequesterId == userId || f.AddresseeId == userId))
            .ToListAsync();

        return friendships.Select(f =>
        {
            var friend = (f.RequesterId == userId ? f.Addressee : f.Requester)
                ?? throw new InvalidOperationException($"Friendship {f.FriendshipId}: navigation not loaded");
            return new FriendListItem
            {
                UserId = friend.UserId,
                DisplayName = friend.DisplayName,
                Email = friend.Email,
                FriendshipId = f.FriendshipId,
                FriendsSince = f.UpdatedAt
            };
        }).ToList();
    }

    public async Task<List<FriendRequestItem>> GetPendingRequestsAsync(string userId)
    {
        return await _context.Friendships
            .Include(f => f.Requester)
            .Where(f => f.AddresseeId == userId && f.Status == FriendshipStatuses.Pending)
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => new FriendRequestItem
            {
                FriendshipId = f.FriendshipId,
                UserId = f.RequesterId,
                // Required FK: the join always matches. Inside an expression tree `!` is the
                // documented pattern (CS8602 warns in lambdas; no runtime null is possible).
                DisplayName = f.Requester!.DisplayName,
                Email = f.Requester.Email,
                RequestedAt = f.CreatedAt
            })
            .ToListAsync();
    }

    public async Task<int> GetPendingRequestCountAsync(string userId)
    {
        return await _context.Friendships
            .CountAsync(f => f.AddresseeId == userId && f.Status == FriendshipStatuses.Pending);
    }

    public async Task SendRequestAsync(string requesterId, string addresseeId)
    {
        if (requesterId == addresseeId)
            throw new InvalidOperationException("Cannot send a friend request to yourself.");

        // Verify the addressee exists first: otherwise the FK violation on insert surfaces through
        // the DbUpdateException catch below as the misleading "A friend request already exists."
        if (string.IsNullOrWhiteSpace(addresseeId)
            || !await _context.Users.AnyAsync(u => u.UserId == addresseeId))
            throw new InvalidOperationException("User not found.");

        // Check for existing relationship in either direction
        var existing = await _context.Friendships.FirstOrDefaultAsync(f =>
            (f.RequesterId == requesterId && f.AddresseeId == addresseeId)
            || (f.RequesterId == addresseeId && f.AddresseeId == requesterId));

        if (existing is not null)
        {
            if (existing.Status == FriendshipStatuses.Accepted)
                throw new InvalidOperationException("Already friends.");
            if (existing.Status == FriendshipStatuses.Pending)
                throw new InvalidOperationException("A friend request already exists.");
            // Defensive: no code path writes "blocked" today.
            if (existing.Status == FriendshipStatuses.Blocked)
                throw new InvalidOperationException("Cannot send request.");
            if (existing.Status == FriendshipStatuses.Declined)
            {
                // Allow re-requesting after a decline: reset the row
                existing.RequesterId = requesterId;
                existing.AddresseeId = addresseeId;
                existing.Status = FriendshipStatuses.Pending;
                existing.UpdatedAt = _clock.GetUtcNow().UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
                await _context.SaveChangesAsync();
                InvalidateAcceptedIdsMemo();
                _logger.LogInformation("Friend request re-sent from {Requester} to {Addressee}",
                    requesterId, addresseeId);
                return;
            }
        }

        var now = _clock.GetUtcNow().UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        _context.Friendships.Add(new Friendship
        {
            RequesterId = requesterId,
            AddresseeId = addresseeId,
            Status = FriendshipStatuses.Pending,
            CreatedAt = now,
            UpdatedAt = now
        });
        try
        {
            await _context.SaveChangesAsync();
            InvalidateAcceptedIdsMemo();
        }
        catch (DbUpdateException)
        {
            // The unique index on (RequesterId, AddresseeId) rejected a concurrent duplicate insert.
            throw new InvalidOperationException("A friend request already exists.");
        }
        _logger.LogInformation("Friend request sent from {Requester} to {Addressee}",
            requesterId, addresseeId);
    }

    public async Task AcceptRequestAsync(string currentUserId, int friendshipId)
    {
        var friendship = await _context.Friendships.FindAsync(friendshipId)
            ?? throw new InvalidOperationException("Friend request not found.");

        if (friendship.AddresseeId != currentUserId)
            throw new InvalidOperationException("Only the addressee can accept a request.");

        if (friendship.Status != FriendshipStatuses.Pending)
            throw new InvalidOperationException($"Cannot accept a request with status '{friendship.Status}'.");

        friendship.Status = FriendshipStatuses.Accepted;
        friendship.UpdatedAt = _clock.GetUtcNow().UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        await _context.SaveChangesAsync();
        InvalidateAcceptedIdsMemo();

        _cacheInvalidator.InvalidateUser(friendship.RequesterId);
        _cacheInvalidator.InvalidateUser(friendship.AddresseeId);

        _logger.LogInformation("Friendship {Id} accepted between {Requester} and {Addressee}",
            friendshipId, friendship.RequesterId, friendship.AddresseeId);
    }

    public async Task DeclineRequestAsync(string currentUserId, int friendshipId)
    {
        var friendship = await _context.Friendships.FindAsync(friendshipId)
            ?? throw new InvalidOperationException("Friend request not found.");

        if (friendship.AddresseeId != currentUserId)
            throw new InvalidOperationException("Only the addressee can decline a request.");

        if (friendship.Status != FriendshipStatuses.Pending)
            throw new InvalidOperationException($"Cannot decline a request with status '{friendship.Status}'.");

        friendship.Status = FriendshipStatuses.Declined;
        friendship.UpdatedAt = _clock.GetUtcNow().UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        await _context.SaveChangesAsync();
        InvalidateAcceptedIdsMemo();

        _logger.LogInformation("Friendship {Id} declined by {User}", friendshipId, currentUserId);
    }

    public async Task RemoveFriendAsync(string currentUserId, int friendshipId)
    {
        var friendship = await _context.Friendships.FindAsync(friendshipId)
            ?? throw new InvalidOperationException("Friendship not found.");

        if (friendship.RequesterId != currentUserId && friendship.AddresseeId != currentUserId)
            throw new InvalidOperationException("Not authorized to remove this friendship.");

        _context.Friendships.Remove(friendship);
        await _context.SaveChangesAsync();
        InvalidateAcceptedIdsMemo();

        _cacheInvalidator.InvalidateUser(friendship.RequesterId);
        _cacheInvalidator.InvalidateUser(friendship.AddresseeId);

        _logger.LogInformation("Friendship {Id} removed by {User}", friendshipId, currentUserId);
    }

    public async Task<List<UserSearchResult>> SearchUsersAsync(string query, string excludeUserId)
    {
        var users = await _userService.SearchUsersAsync(query, excludeUserId);
        if (users.Count == 0) return [];

        var userIds = users.Select(u => u.UserId).ToList();

        // Batch-fetch all friendship statuses for these users
        var friendships = await _context.Friendships
            .AsNoTracking()
            .Where(f =>
                (f.RequesterId == excludeUserId && userIds.Contains(f.AddresseeId))
                || (f.AddresseeId == excludeUserId && userIds.Contains(f.RequesterId)))
            .ToListAsync();

        var statusMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in friendships)
        {
            var otherUserId = f.RequesterId == excludeUserId ? f.AddresseeId : f.RequesterId;
            statusMap[otherUserId] = f.Status;
        }

        // Only reveal an email when the searcher already typed the exact address (an exact-name hit
        // must not leak the matched user's email). Friend lists still carry emails — that's consent data.
        var exactEmail = query.Trim().ToUpperInvariant();
        return users.Select(u => new UserSearchResult
        {
            UserId = u.UserId,
            DisplayName = u.DisplayName,
            Email = (u.Email != null && u.Email.ToUpperInvariant() == exactEmail) ? u.Email : null,
            FriendshipStatus = statusMap.GetValueOrDefault(u.UserId, "none")
        }).ToList();
    }
}
