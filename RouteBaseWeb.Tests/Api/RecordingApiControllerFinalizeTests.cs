using RouteBaseWeb.Api;
using RouteBaseWeb.Hubs;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Recording;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Sharing;
using RouteBaseWeb.Services.Tracks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RouteBaseWeb.Tests.Helpers;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins the two surviving <c>catch (Exception)</c> sites on <see cref="RecordingApiController"/> — the
/// inner finalize catches in StopSession and RetryFinalize — that the I1b filter deletion leaves in place
/// (they are semantic: a finalize failure is non-fatal / triggers compensation, not a generic 500). A
/// throwing <see cref="IRecordingFinalizationService"/> fake, resolved through the coordinator's own scope
/// factory, forces the failure exactly as production would surface it.
/// </summary>
public class RecordingApiControllerFinalizeTests : IAsyncLifetime
{
    private const string UserId = "test-user";

    private string _connStr = null!;
    private RouteBaseDbContext _context = null!;
    private ServiceProvider _provider = null!;
    private RecordingService _service = null!;

    public async Task InitializeAsync()
    {
        var dbName = $"recording_finalize_{Guid.NewGuid():N}";
        _connStr = $"Data Source=file:{dbName}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_connStr).Options;

        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        var schema = new DbSchemaManager(_context);
        await schema.EnsureActivityBboxTableAsync();
        await schema.EnsureTrackContentTablesAsync();
        await schema.EnsureRecordingStagingTableAsync();

        _context.Users.Add(new AppUser
        {
            UserId = UserId, DisplayName = "Test User", Email = "test@test.com", IsAdmin = false,
        });
        await _context.SaveChangesAsync();

        _provider = new ServiceCollection().AddSingleton(TimeProvider.System)
            .AddDbContext<RouteBaseDbContext>(o => o.UseSqlite(_connStr))
            .AddScoped<ITrackObjectStore, TrackObjectStore>()
            .AddScoped<TrackContentStore>()
            .AddScoped<RecordingStagingStore>()
            .BuildServiceProvider();
        var reader = new ContentTrackReader(
            new TrackReaderCache(64L * 1024 * 1024), new SingleFlight(),
            _provider.GetRequiredService<IServiceScopeFactory>());
        var content = new TrackContentStore(_context, new TrackObjectStore(_context, TimeProvider.System), TimeProvider.System);
        var stagingStore = new RecordingStagingStore(_context);
        var importService = new ActivityImportService(_context, content, NullLogger<ActivityImportService>.Instance);
        var commandVis   = new TestVisibilityService(UserId);
        var commandInval = new UserCacheInvalidator();
        var commandService = new ActivityCommandService(
            _context, commandVis, commandInval,
            NullLogger<ActivityCommandService>.Instance,
            TestLifecycle.For(_context, commandVis, commandInval));

        _service = new RecordingService(
            _context, importService, stagingStore, reader, commandService,
            new RecordingFinalizationCoordinator(new SingleFlight(), _provider.GetRequiredService<IServiceScopeFactory>()),
            NullLogger<RecordingService>.Instance, TimeProvider.System);
    }

    public Task DisposeAsync()
    {
        _provider.Dispose();
        return _context.DisposeAsync().AsTask();
    }

    // A coordinator whose finalize always throws — the scope factory resolves the throwing fake, exactly
    // as the production coordinator resolves the real service through IServiceScopeFactory.
    private static RecordingFinalizationCoordinator ThrowingCoordinator()
    {
        var provider = new ServiceCollection().AddSingleton(TimeProvider.System)
            .AddScoped<IRecordingFinalizationService, ThrowingFinalizationService>()
            .BuildServiceProvider();
        return new RecordingFinalizationCoordinator(
            new SingleFlight(), provider.GetRequiredService<IServiceScopeFactory>());
    }

    private RecordingApiController Controller() => new(
        _service,
        new ShareLinkService(_context, new TestVisibilityService(UserId), _service,
            new RouteBaseWeb.Services.Races.RaceAccess(_context, new TestVisibilityService(UserId)), TimeProvider.System),
        ThrowingCoordinator(),
        new TestCurrentUserService(UserId),
        new TestVisibilityService(UserId),
        new RecordingRateLimiter(() => DateTime.UtcNow),
        new FinalizeNoOpHubContext(),
        new TestLiveSessionDirectory(),
        new LiveViewerRegistry(() => DateTime.UtcNow),   // no viewers: these tests never assert on the `watched` flag
        NullLogger<RecordingApiController>.Instance,
        TimeProvider.System);

    private static StartRecordingRequest StartRequest() => new()
    {
        ActivityType = "Run", TrackName = "Test Run", Visibility = "friends", ClientVersion = "test-1.0",
    };

    [Fact]
    public async Task StopSession_FinalizeThrows_StillReturnsOk()
    {
        var session = await _service.StartSessionAsync(StartRequest(), UserId);
        Assert.NotNull(session);

        var result = await Controller().StopSession(session!.SessionId);

        // Finalize threw (inner STAY catch swallowed it) — session is stopped, endpoint still returns 200.
        Assert.IsType<OkObjectResult>(result);
        var stopped = await _service.GetSessionAsync(session.SessionId);
        Assert.Equal("stopped", stopped!.Status);
    }

    [Fact]
    public async Task RetryFinalize_FinalizeThrows_ReturnsFinalizedFalse_AndReparks()
    {
        var session = await _service.StartSessionAsync(StartRequest(), UserId);
        Assert.NotNull(session);
        await _service.StopSessionAsync(session!.SessionId, UserId);
        await _service.MarkFinalizeFailedAsync(session.SessionId, UserId);

        var result = await Controller().RetryFinalize(session.SessionId);

        // Inner STAY catch compensation: re-park at finalize_failed and return Ok { finalized = false }.
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.False(Assert.IsType<FinalizeRetryResponse>(ok.Value).Finalized);
        var reparked = await _service.GetSessionAsync(session.SessionId);
        Assert.Equal("finalize_failed", reparked!.Status);
    }
}

/// <summary>Finalization fake that always throws — drives the surviving inner-finalize catches.</summary>
file sealed class ThrowingFinalizationService : IRecordingFinalizationService
{
    public Task FinalizeAsync(string sessionId) =>
        throw new InvalidOperationException("Simulated finalize failure");
}

file sealed class FinalizeNoOpHubContext : IHubContext<RecordingHub>
{
    public IHubClients Clients { get; } = new FinalizeNoOpClients();
    public IGroupManager Groups { get; } = new FinalizeNoOpGroups();
}

file sealed class FinalizeNoOpClients : IHubClients
{
    private static readonly IClientProxy Proxy = new FinalizeNoOpProxy();
    public IClientProxy All => Proxy;
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
    public IClientProxy Client(string connectionId) => Proxy;
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
    public IClientProxy Group(string groupName) => Proxy;
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
    public IClientProxy User(string userId) => Proxy;
    public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
}

file sealed class FinalizeNoOpProxy : IClientProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

file sealed class FinalizeNoOpGroups : IGroupManager
{
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
