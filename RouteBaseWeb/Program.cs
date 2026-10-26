using RouteBaseWeb.Auth;
using RouteBaseWeb.Configuration;
using RouteBaseWeb.Services.Sharing;
using RouteBaseWeb.Hubs;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Credentials;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Queue;
using RouteBaseWeb.Startup;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

// AppContext.BaseDirectory points to the folder containing the executable.
// wwwroot is copied there by the Web SDK on every build.
// database is created automatically if missing.
var exeDir = AppContext.BaseDirectory;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = exeDir,
    WebRootPath = Path.Combine(exeDir, "wwwroot")
});

// Localhost mode (README): run as one always-authenticated local user, no Authentik. The switch is
// read ONLY from the physical appsettings.Local.json next to the exe (LocalModeGuard) — never from env vars,
// CLI args, or the main appsettings.json — so authentication cannot be turned off on the server remotely.
// Off unless that one file ships (only the desktop artifact carries it). Gates the auth registration, the
// admin gate, the local-user seed, host filtering, the auto-open-browser step below — and, first of all,
// whether the file's remaining keys are allowed into the main configuration at all:
var localMode = LocalModeGuard.ReadFromFile(exeDir);
builder.Services.AddSingleton(localMode);

// The desktop artifact's appsettings.Local.json merges into the MAIN config (loaded last so it wins over
// appsettings(.Development).json; resolved against ContentRootPath = exeDir) ONLY when the file itself
// enables LocalMode. UNUSUAL LAYERING, ON PURPOSE — do not "simplify" this into an unconditional
// AddJsonFile: config sources ordinarily don't gate each other, but on a server (LocalMode off) an
// unconditional load would let a mere file drop next to the binary silently redirect Authentik:*,
// Admin:Users, or connection strings while authentication stays on. Gated, the file influences nothing
// unless it ALSO flips the app into local mode — which refuses non-loopback binds right below, and which
// the A7 Dockerfile assert keeps out of the server image entirely. Pinned by LocalSettingsGateTests.
if (localMode.Enabled)
    builder.Configuration.AddJsonFile(LocalModeOptions.SettingsFileName, optional: true, reloadOnChange: false);

// Uploads from a slow/bursty mobile uplink stall below Kestrel's 240 B/s body-rate floor; Kestrel then
// aborts the connection mid-body and the reverse proxy reports a 502. Relax the floor + grace window.
builder.WebHost.ConfigureKestrel(o =>
    o.Limits.MinRequestBodyDataRate = new MinDataRate(bytesPerSecond: 50, gracePeriod: TimeSpan.FromSeconds(30)));

// Database path: always  <exeDir>\database\routebase.db
var dbDirectory = Path.Combine(exeDir, "database");
var fullDbPath = Path.Combine(dbDirectory, "routebase.db");
bool createdNewDatabase = false;

if (!File.Exists(fullDbPath))
{
    Console.WriteLine($"⚠️  No database found at: {fullDbPath}");
    Console.WriteLine("    Creating a new empty database...");
    Directory.CreateDirectory(dbDirectory);
    createdNewDatabase = true;
}
else
{
    Console.WriteLine($"✅ Database: {fullDbPath}");
}

// THE memory budget (Services/MemoryBudget.cs) — one file owns every memory number, in one unit. Every cache
// below takes its size from here; none of them sizes itself. Validated + logged after the app is built.
var memoryBudget = builder.Configuration.GetSection(MemoryBudget.SectionName).Get<MemoryBudget>() ?? new MemoryBudget();
builder.Services.AddSingleton(memoryBudget);

if (localMode.Enabled)
{
    // No auth in local mode → it must never be reachable off the machine. Refuse to start if ANY bind
    // source is non-loopback: urls/ASPNETCORE_URLS, a Kestrel:Endpoints:*:Url section, or the
    // ASPNETCORE_HTTP(S)_PORTS shorthands (which always mean http://*:port).
    if (!LocalModeGuard.IsLoopbackOnly(builder.Configuration))
        throw new InvalidOperationException(
            "Localhost mode has no authentication and must bind to loopback only, but the configuration " +
            "(urls, Kestrel:Endpoints, or HTTP_PORTS/HTTPS_PORTS) produces a non-loopback binding. " +
            "Refusing to start.");

    // Pin host filtering to loopback names so a malicious web page can't reach the app via DNS rebinding
    // (a loopback bind alone does not stop that). AllowedHosts is "*" — pass-through — otherwise.
    builder.Services.PostConfigure<HostFilteringOptions>(o =>
        o.AllowedHosts = new List<string> { "localhost", "127.0.0.1", "[::1]" });

    // The startup browser launch, behind an interface so test hosts can substitute a recorder.
    builder.Services.AddSingleton<ILocalModeBrowserOpener, ShellBrowserOpener>();
}

// Add services to the container.
builder.Services.AddRazorPages(o => o.Conventions.AddPageRoute("/KeysAndData", "StravaSync"));
// OBS-01: global filter is the single log+500 path for api/ controllers (replaces per-endpoint catches).
// D1: global cross-site guard covers every state-changing api/ verb by default (supersedes per-action attrs).
builder.Services.AddControllers(o =>
{
    o.Filters.Add<RouteBaseWeb.Api.ApiExceptionFilter>();
    o.Filters.Add<RouteBaseWeb.Api.CrossSiteApiGuardFilter>();
});
// API-07: [ApiController]'s automatic validation 400 is the last error on the wire that did NOT speak
// ApiError — it emitted the framework's ValidationProblemDetails, which no generated client can read
// (they all parse {code, message}). Every malformed request body now answers the canonical shape.
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(o =>
    o.InvalidModelStateResponseFactory = ctx =>
        new Microsoft.AspNetCore.Mvc.BadRequestObjectResult(
            RouteBaseWeb.Api.ValidationErrorBody.From(ctx.ModelState)));
// OpenAPI document — the single source of truth for the generated TS client contracts (src/js/generated).
// Emitted at BUILD time only by Microsoft.Extensions.ApiDescription.Server (see csproj); no runtime endpoint
// (D8: the API shape is not served publicly). Endpoint response shapes come from [ProducesResponseType<T>].
// The schema transformer fixes STJ property-presence (required) so nullable-vs-omitted survives to TypeScript.
builder.Services.AddOpenApi(o =>
{
    o.AddSchemaTransformer<RouteBaseWeb.Api.OpenApiContracts.RequiredPropertiesSchemaTransformer>();
    o.AddSchemaTransformer<RouteBaseWeb.Api.OpenApiContracts.CustomConvertedTimestampSchemaTransformer>();
    o.AddSchemaTransformer<RouteBaseWeb.Api.OpenApiContracts.WireValueEnumSchemaTransformer>();
    o.AddDocumentTransformer<RouteBaseWeb.Api.OpenApiContracts.OpaqueSchemasDocumentTransformer>();
    o.AddDocumentTransformer<RouteBaseWeb.Api.OpenApiContracts.IslandSchemasDocumentTransformer>();
    // Last on purpose: it patches schemas the earlier transformers assembled (K3 — RuleDocument union).
    o.AddDocumentTransformer<RouteBaseWeb.Api.OpenApiContracts.RuleDocumentSchemasDocumentTransformer>();
});
// [RequestSizeLimit] raises Kestrel's body cap but NOT the multipart form cap (128 MB default): a chunk
// over it aborts the connection mid-read, which the reverse proxy reports as a 502. Match the largest
// endpoint limit so an oversized upload fails as a normal error response instead.
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 200L * 1024 * 1024);
// The clock seam (K2): services ctor-inject TimeProvider and read _clock.GetUtcNow().UtcDateTime
// instead of DateTime.UtcNow, so expiry/sweep/age-out suites can drive time with a TestClock.
builder.Services.AddSingleton(TimeProvider.System);
// The shared cache has NO SizeLimit — it is bounded by TTL (every entry sets one), not by size. Giving it a
// size limit would mean a Size on every entry app-wide; its peak is therefore not declared in MemoryBudget.
builder.Services.AddMemoryCache();
// GPS coordinate payloads get their own size-bounded cache, sized from the budget.
builder.Services.AddSingleton<GpsCoordinateCache>();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(opts =>
{
    opts.Cookie.HttpOnly  = true;
    opts.Cookie.IsEssential = true;
    // Always in server mode: the Secure attribute must not depend on forwarded-proto handling.
    // SameAsRequest in localhost mode, which serves plain http://localhost by design.
    opts.Cookie.SecurePolicy = localMode.Enabled ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    opts.IdleTimeout = TimeSpan.FromMinutes(30);
});

// Compress JSON API responses (GPS coordinates are highly compressible — ~60-70% reduction).
builder.Services.AddResponseCompression(opts =>
{
    opts.EnableForHttps = true;
    opts.Providers.Add<BrotliCompressionProvider>();
    opts.Providers.Add<GzipCompressionProvider>();
    opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        ["application/json"]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(opts =>
    opts.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(opts =>
    opts.Level = System.IO.Compression.CompressionLevel.Fastest);

// AddDbContextFactory (not AddDbContext): EF Core also registers RouteBaseDbContext itself as a
// scoped service from the factory, so existing constructor injections keep working unchanged, and
// services that must not share the request/worker-scoped context (StravaAuthService, SV-03) can
// create short-lived instances via IDbContextFactory. Do NOT also call AddDbContext — the
// scoped-options vs singleton-factory lifetimes would clash.
// Foreign Keys=True is already the effective behavior — the bundled e_sqlite3 is compiled with
// SQLITE_DEFAULT_FOREIGN_KEYS — but a provider/bundle swap would silently drop that guarantee,
// so pin it in the connection string too (SqliteForeignKeyEnforcementTests pins both layers).
// SqliteBusyTimeoutInterceptor does the same for lock contention: it pins PRAGMA busy_timeout on every
// connection EF opens (busy_timeout is connection-scoped, so it can't go in the string like Foreign Keys).
// Removes the silent reliance on the provider's managed SQLITE_BUSY retry.
builder.Services.AddDbContextFactory<RouteBaseDbContext>(options =>
    options.UseSqlite($"Data Source={fullDbPath};Foreign Keys=True")
           .AddInterceptors(new RouteBaseWeb.Services.Sql.SqliteBusyTimeoutInterceptor()));

// Data Protection key ring on the persisted DB volume (decision ⑥): encrypted credentials — and
// auth cookies — must survive container redeploys; the in-container default key location is
// ephemeral. DatabaseBackupService mirrors this directory beside the DB snapshots (PI-05).
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(
        Path.Combine(dbDirectory, RouteBaseWeb.Services.DatabaseBackupService.DataProtectionKeysDirName)))
    .SetApplicationName("RouteBaseWeb");
builder.Services.AddSingleton<ICredentialProtector, CredentialProtector>();

// Feature-slice DI. Each AddXxxServices lives in RouteBaseWeb/Startup/* and holds one slice's
// registrations verbatim and in order ("features as vertical slices"); no reflection or
// assembly scanning — every service is still found by grepping its interface name.
builder.Services.AddActivityServices();
builder.Services.AddSegmentServices();
builder.Services.AddRaceServices();
// Cross-cutting support services (grid owner-scope, map tiles, profile settings, schema manager) belong
// to no single feature slice, so they stay inline.
builder.Services.AddScoped<RouteBaseWeb.Helpers.Grid.IOwnerScopeService, RouteBaseWeb.Helpers.Grid.OwnerScopeService>();
builder.Services.AddScoped<IMapTilerService, MapTilerService>();
builder.Services.AddScoped<IProfileSettingsService, ProfileSettingsService>();
// Scoped so it memoizes for the request: the layouts render the settings island on every page.
builder.Services.AddScoped<RouteBaseWeb.Services.Settings.IAppSettingsProvider,
    RouteBaseWeb.Services.Settings.AppSettingsProvider>();
builder.Services.AddScoped<IDbSchemaManager, DbSchemaManager>();

// Statistics page aggregation service
builder.Services.AddScoped<IStatisticsService, StatisticsService>();

builder.Services.AddImportExportServices(builder.Configuration);

builder.Services.AddPhotoServices(builder.Configuration);

// Per-user progress trackers — cross-cutting singletons read by their slices' handlers/services.
builder.Services.AddSingleton<SegmentAnalysisProgress>();
builder.Services.AddSingleton<ActivityImportProgress>();
builder.Services.AddSingleton<ActivityExportProgress>();

// Finished export ZIPs — same storage root as the photo renditions, swept after the retention window.
builder.Services.AddSingleton(new ActivityExportStorageOptions { Root = dbDirectory });
builder.Services.AddSingleton<IActivityExportStore, ActivityExportStore>();

builder.Services.AddHeatmapServices(dbDirectory);
builder.Services.AddLifecycleServices();

builder.Services.AddOperationQueueServices();
// Server-clock recording rate limiter (RC-02): singleton so batches across requests share state.
builder.Services.AddSingleton<IRecordingRateLimiter>(sp => new RecordingRateLimiter(
    () => sp.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime));
// Live-viewer registry: singleton so the hub's connection scopes and the API's request scopes share it.
builder.Services.AddSingleton<ILiveViewerRegistry>(sp => new LiveViewerRegistry(
    () => sp.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime));
builder.Services.AddBackgroundWorkerServices();

// Error-log mirror (OBS-03): an ILoggerProvider captures Error+ from every category into a bounded channel;
// the drain service writes them to Error_Log. Recursion-guarded by category-prefix ignore (own namespace +
// EF) + the AsyncLocal ErrorLogSuppression flag set around the store write. The provider is registered in DI
// so LoggerFactory ctor-injects the channel + IHttpContextAccessor; AddFilter widens the Error+ gate.
builder.Services.AddSingleton<RouteBaseWeb.Services.ErrorLog.ErrorLogChannel>();
builder.Services.AddSingleton<ILoggerProvider, RouteBaseWeb.Services.ErrorLog.ErrorLogLoggerProvider>();
builder.Logging.AddFilter<RouteBaseWeb.Services.ErrorLog.ErrorLogLoggerProvider>(null, LogLevel.Error);
builder.Services.AddHostedService<RouteBaseWeb.Services.ErrorLog.ErrorLogWriteService>();

// Strava decomposed services
builder.Services.AddScoped<IStravaAuthService, StravaAuthService>();
builder.Services.AddScoped<IStravaApiClient, StravaApiClient>();
builder.Services.AddScoped<IStravaImportOrchestrator, StravaImportOrchestrator>();
builder.Services.AddScoped<IDatabaseResetService, DatabaseResetService>();

// Provider registrations — add new providers here (e.g. GarminService) in the future
builder.Services.AddScoped<StravaService>();
builder.Services.AddScoped<IActivitySyncService>(sp => sp.GetRequiredService<StravaService>());

builder.Services.AddHttpClient();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<IVisibilityService, VisibilityService>();
builder.Services.AddScoped<IFriendshipService, FriendshipService>();
builder.Services.AddScoped<IApiKeyService, ApiKeyService>();
// SVC-08: process-wide api-key cache generation stamp — read by the auth handler, bumped on revoke/generate.
builder.Services.AddSingleton<ApiKeyCacheGeneration>();
// Share slice: resolves a token to a ShareGrant and owns the ONE widening seam. Scoped — it memoises the
// current request's grant and nothing more (a cross-request grant cache would break revocation).
builder.Services.AddScoped<RouteBaseWeb.Services.Sharing.IShareLinkAccess,
    RouteBaseWeb.Services.Sharing.ShareLinkAccess>();
builder.Services.AddScoped<RouteBaseWeb.Services.Sharing.IShareLinkService,
    RouteBaseWeb.Services.Sharing.ShareLinkService>();
// Open counting. Separate from IShareLinkService so the anonymous page does not take a dependency on the
// owner-facing mint surface (and everything IT depends on) just to bump a counter.
builder.Services.AddScoped<RouteBaseWeb.Services.Sharing.IShareOpenRecorder,
    RouteBaseWeb.Services.Sharing.ShareOpenRecorder>();
// The share path's ONLY segment query surface. Owner-scoped by construction: it takes neither
// IVisibilityService nor ICurrentUserService, so there is no ambient identity for it to widen through.
builder.Services.AddScoped<RouteBaseWeb.Services.Sharing.IShareSegmentReader,
    RouteBaseWeb.Services.Sharing.ShareSegmentReader>();
builder.Services.AddScoped<RouteBaseWeb.Services.Sharing.ShareSegmentViewSource>();
// The share path's ONLY race query surface — same construction rule as the segment reader, plus a DTO
// family that carries no entrant identity at all (see ShareRaceReader).
builder.Services.AddScoped<RouteBaseWeb.Services.Sharing.IShareRaceReader,
    RouteBaseWeb.Services.Sharing.ShareRaceReader>();
// Activity detail view-model construction, shared by /Activities/Details and the share view so both
// render from one loading sequence rather than two copies that drift.
builder.Services.AddScoped<RouteBaseWeb.Services.Activities.IActivityDetailViewBuilder,
    RouteBaseWeb.Services.Activities.ActivityDetailViewBuilder>();

// Share-link rate limiting (Configuration/ShareRateLimiterSetup.cs — moved whole, design rationale
// lives with the code): three chained tiers keyed on the share-token route value, never on client IP,
// rejecting as 404. Non-share requests are untouched.
builder.Services.AddShareRateLimiter();
builder.Services.AddSingleton<UserCacheInvalidator>();
// Process-wide coalescing of identical concurrent bulk-GPS queries (zoom-storm collapse).
builder.Services.AddSingleton<SingleFlight>();

// Decoded-track read seam (TrackReader): one shared per-track decode + strict-LRU cache, replacing
// per-consumer EF GPS materialization. Its size comes from MemoryBudget — no cache sizes itself.
builder.Services.AddSingleton(_ =>
    new RouteBaseWeb.Services.Tracks.TrackReaderCache(memoryBudget.TrackDecodeCacheMB * 1024L * 1024L));
builder.Services.AddSingleton<RouteBaseWeb.Services.Tracks.ITrackReader,
    RouteBaseWeb.Services.Tracks.ContentTrackReader>();
// Content axis: immutable sha-keyed objects + the append-only version list / head pointer over them
// (raw-ADO, non-EF; Dem_Tiles precedent). The ITrackObjectStore seam is where a filesystem / object-storage
// backend would swap in (the .htiles precedent) without touching a caller.
builder.Services.AddScoped<RouteBaseWeb.Services.Tracks.ITrackObjectStore,
    RouteBaseWeb.Services.Tracks.TrackObjectStore>();
builder.Services.AddScoped<RouteBaseWeb.Services.Tracks.TrackContentStore>();
builder.Services.AddScoped<RouteBaseWeb.Services.Tracks.RecordingStagingStore>();
// Diagnostic error store (OBS-02): raw-ADO, non-EF; scope-resolved by the error-log drain service.
builder.Services.AddScoped<RouteBaseWeb.Services.ErrorLog.ErrorLogStore>();
// TrackEdit session/audit stores (EditCore A1 §2.8): raw-ADO, non-EF, Dem_Tiles precedent.
builder.Services.AddScoped<RouteBaseWeb.Services.TrackEdit.EditSessionStore>();
builder.Services.AddScoped<RouteBaseWeb.Services.TrackEdit.TrackEditCommitStore>();
// TrackEdit session lifecycle service (A2 §3): guards + stamps + validation replay + write-through.
builder.Services.AddScoped<RouteBaseWeb.Services.TrackEdit.EditSessionService>();
// TrackEdit add-track suggestions (A4 §3.3): proximity + same-day + name/date search over Activity scalars.
builder.Services.AddScoped<RouteBaseWeb.Services.TrackEdit.EditSuggestionService>();
// TrackEdit post-save/revert derive chain (A2 §5.3/§14.4): re-match segments + photos off-request.
builder.Services.AddScoped<RouteBaseWeb.Services.TrackEdit.TrackEditDeriveService>();
// TrackEdit save + revert pipelines (A2 §4/§6/§14.4): snapshot-first blob overwrite + sync stats in-tx.
builder.Services.AddScoped<RouteBaseWeb.Services.TrackEdit.TrackEditSaveService>();
builder.Services.AddScoped<RouteBaseWeb.Services.TrackEdit.TrackEditRevertService>();
// Admin:Users config gate — shared by /Admin/Errors and the sidebar's admin-only nav entry.
builder.Services.AddScoped<RouteBaseWeb.Services.IAdminGate, RouteBaseWeb.Services.AdminGate>();

// Photo rendition file store (§1b): thin photo layer over the domain-agnostic FileSystemBlobStore, rooted
// at the DB's data dir so photos back up as a unit with the DB. Stateless (no DbContext) → singleton.
builder.Services.AddSingleton(new RouteBaseWeb.Services.Photos.PhotoStorageOptions { Root = dbDirectory });
builder.Services.AddSingleton<RouteBaseWeb.Services.Photos.IPhotoImageStore,
    RouteBaseWeb.Services.Photos.FileSystemPhotoImageStore>();

// In-memory match engine + effort math (TrackEngine): THE segment analyze path since Cutover — stages
// 1–6 match + INSERT, then the pair-coherence offset search + effort aggregates. Both are required by
// SegmentAnalysis (which runs them unconditionally).
builder.Services.AddScoped<RouteBaseWeb.Services.Matching.IMatchEngine,
    RouteBaseWeb.Services.Matching.MatchEngineService>();
builder.Services.AddScoped<RouteBaseWeb.Services.Matching.IEffortMath,
    RouteBaseWeb.Services.Matching.EffortMathService>();

// Live recording
builder.Services.AddSignalR();
// Terrarium DEM tile cache (raw-PNG SQLite blob store).
builder.Services.AddScoped<RouteBaseWeb.Services.Elevation.DemTileStore>();
builder.Services.AddScoped<IElevationService, RouteBaseWeb.Services.Elevation.TerrariumElevationService>();
builder.Services.AddScoped<IGpsNormalizationService, GpsNormalizationService>();
builder.Services.AddScoped<IRecordingService, RecordingService>();
builder.Services.AddScoped<ILiveSessionDirectory, LiveSessionDirectory>();
builder.Services.AddScoped<IRecordingFinalizationService, RecordingFinalizationService>();
// Elevation refresh is served by the same impl but named neutrally (B1-S2): Activities API depends on
// IElevationRefreshService, not the Recording-named interface. Forward to the same scoped instance.
builder.Services.AddScoped<IElevationRefreshService>(sp =>
    (RecordingFinalizationService)sp.GetRequiredService<IRecordingFinalizationService>());
// Singleton: coalesces concurrent finalizations (stop retry + cleanup retry) onto one execution.
builder.Services.AddSingleton<RecordingFinalizationCoordinator>();

if (localMode.Enabled)
{
    // Localhost mode: no Authentik. One always-authenticated local user is the only scheme, so the
    // global RequireAuthenticatedUser fallback policy is satisfied on every request with no login.
    builder.Services.AddAuthentication(LocalModeOptions.AuthenticationScheme)
        .AddScheme<AuthenticationSchemeOptions, LocalUserAuthenticationHandler>(
            LocalModeOptions.AuthenticationScheme, null)
        // Also registered here, not only in server mode: localhost is where share links get developed and
        // smoke-tested, and without this they would silently 404 there.
        .AddScheme<AuthenticationSchemeOptions, ShareLinkAuthenticationHandler>(
            ShareLinkDefaults.AuthenticationScheme, null);
}
else
{
    // Authentik OIDC authentication
    // "Auto" policy scheme selects cookie auth for browser sessions and JWT bearer for API clients
    // (Android app) based on the presence of an Authorization: Bearer header.
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "Auto";
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddPolicyScheme("Auto", "Auto", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            if (context.Request.Headers.ContainsKey("X-Api-Key"))
                return ApiKeyDefaults.AuthenticationScheme;
            // ShareLink is deliberately NOT reachable from here, and must not be added. This selector sets
            // HttpContext.User for the global RequireAuthenticatedUser fallback policy — a share principal
            // returned here would satisfy that policy on EVERY endpoint, leaving the fail-closed share:anon
            // identity as the only thing standing between a token and the whole app. Kept out, a share
            // principal exists only where an endpoint names the scheme explicitly, which IS the
            // default-deny. Pinned by ShareLinkDefaultDenyTests.
            return CookieAuthenticationDefaults.AuthenticationScheme;
        };
    })
    .AddCookie(options =>
    {
        options.LoginPath = "/Auth/Login";
        options.AccessDeniedPath = "/Auth/AccessDenied";
        // /api/* callers are fetch(), not navigations: a 302 to an HTML page reads as success. (CC-01)
        ApiCookieRedirectEvents.Configure(options);
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyDefaults.AuthenticationScheme, null)
    .AddScheme<AuthenticationSchemeOptions, ShareLinkAuthenticationHandler>(
        ShareLinkDefaults.AuthenticationScheme, null)
    .AddOpenIdConnect(options =>
    {
        options.Authority = builder.Configuration["Authentik:Authority"];
        options.ClientId = builder.Configuration["Authentik:ClientId"];
        options.ClientSecret = builder.Configuration["Authentik:ClientSecret"];
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.Events = new OpenIdConnectEvents
        {
            OnTicketReceived = async context =>
            {
                var userService = context.HttpContext.RequestServices
                    .GetRequiredService<IUserService>();
                await userService.ProvisionOrUpdateUserAsync(context.Principal
                    ?? throw new InvalidOperationException("TicketReceived fires with a principal"));
            },
            OnRedirectToIdentityProvider = context =>
            {
                // API clients (X-Api-Key) can't follow an OIDC browser redirect — answer 401 instead.
                if (ApiClientChallengeGate.ShortCircuit(context.HttpContext))
                    context.HandleResponse();
                return Task.CompletedTask;
            },
            OnRemoteFailure = OidcRemoteFailureEvents.Handle,
        };
    });
}

builder.Services.AddAuthorization(options =>
{
    // Default-deny for share principals, in two layers.
    //
    // Layer 1 (structural): ShareLink is not in the "Auto" ForwardDefaultSelector, so a share principal
    // never lands in HttpContext.User except on an endpoint that names the scheme.
    // Layer 2 (this): even if someone wires it into the selector anyway, a share identity is rejected
    // outright by both the fallback and the default policy.
    //
    // Expressed as RequireAssertion and NOT as new AuthorizationPolicyBuilder(schemes…): a policy that
    // lists AuthenticationSchemes makes AuthorizationMiddlewareResultHandler challenge EVERY listed scheme
    // on failure, so one anonymous navigation would emit a Cookie 302, an OIDC 302 and an ApiKey 401 on the
    // same response — breaking login for the whole app. FallbackPolicy.AuthenticationSchemes must stay
    // empty; ShareLinkDefaultDenyTests pins that.
    static AuthorizationPolicy RealUserOnly() =>
        new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => !ShareLinkPrincipal.IsShare(ctx.User))
            .Build();

    // FallbackPolicy covers endpoints with no authorization metadata (almost all of them); DefaultPolicy
    // covers a bare [Authorize], which the fallback does NOT cover — both are needed.
    options.FallbackPolicy = RealUserOnly();
    options.DefaultPolicy = RealUserOnly();

    // The ONLY policy that admits a share principal. Endpoints opt in by name, and the allowlist is pinned
    // to a hard-coded set by test, so widening the share surface is a deliberate act.
    options.AddPolicy(ShareLinkDefaults.ReadPolicy, p => p
        .AddAuthenticationSchemes(ShareLinkDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx => ShareLinkPrincipal.IsShare(ctx.User)));

    // Interactive-human-only actions: minting a share link (an API key must not be able to manufacture a
    // login-free window into its owner's data — a leaked phone key would otherwise become a permanent
    // one) and managing API keys (a leaked key must not rotate itself; a share principal must not touch
    // keys). Same policy body under two names, one builder.
    //
    // ONE narrow, deliberate exception exists to the mint bar: the phone may mint a LIVE-only share for its
    // OWN active session via POST /api/recording/sessions/{id}/share-live (RecordingApiController) — 24h,
    // own-session-scoped, bounded to a ride in progress, so it is not the "permanent window" this bars. That
    // path does NOT go through this policy (MintAsync is owner-id-parameterized, not policy-gated); this
    // policy still rejects every API-key attempt on /api/sharelinks.
    //
    // Deliberately NOT expressed as AuthenticationSchemes = Cookie: that names a scheme which does not
    // exist in local mode, where only "Local" is registered (API-03 — it 500'd every /api/apikeys call).
    static AuthorizationPolicy InteractiveHumanOnly() =>
        new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx =>
                !ShareLinkPrincipal.IsShare(ctx.User) &&
                ctx.User.Identities.All(i =>
                    i.AuthenticationType != ApiKeyDefaults.AuthenticationScheme))
            .Build();
    options.AddPolicy(ShareLinkDefaults.MintPolicy, InteractiveHumanOnly());
    options.AddPolicy(InteractiveUserPolicy.Name, InteractiveHumanOnly());
});

var app = builder.Build();

// The memory budget, as the process ACTUALLY sees it — not as we asked for it. The GC heap hard limit lives in
// runtimeconfig.json (the .csproj) because the runtime reads it before configuration exists, so it is the one
// number MemoryBudget cannot own — and a malformed value there is silently ignored rather than rejected.
// Validate() cross-checks the real limit against Memory:GcHeapLimitMB, so that duplication cannot drift
// quietly: move to a bigger box, change appsettings, forget the .csproj, and this shouts on the next boot.
{
    long actualHeapLimit = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    app.Logger.LogInformation("Memory budget: {Budget}", memoryBudget.Describe(actualHeapLimit));
    foreach (string problem in memoryBudget.Validate(actualHeapLimit))
        app.Logger.LogWarning("Memory budget: {Problem}", problem);
}

var webRootPath = Path.Combine(exeDir, "wwwroot");
app.Environment.WebRootPath = webRootPath;
app.Environment.ContentRootPath = exeDir;

if (Directory.Exists(webRootPath))
{
    Console.WriteLine($"✅ WebRootPath: {webRootPath}");
    Console.WriteLine($"✅ ContentRootPath: {exeDir}");
}
else
{
    Console.WriteLine($"❌ ERROR: wwwroot folder NOT FOUND at: {webRootPath}");
    throw new DirectoryNotFoundException($"wwwroot folder not found at: {webRootPath}");
}

// Boot-time DB preparation (Services/DatabaseStartup.cs — moved whole, phase order and rationale live
// with the code): schema phases (tables → columns → indexes), object GC, WAL, default settings, the
// local-mode user seed, and the credential encryption migration.
await DatabaseStartup.RunAsync(app.Services, localMode, createdNewDatabase);

if (!app.Environment.IsDevelopment())
{
    // CC2-01: fetch() clients expect JSON — the /Error page is HTML. Branch by path so an unhandled
    // API exception can never fall into the HTML handler; this net catches what ApiExceptionFilter
    // can't (result-execution throws, non-MVC middleware). The share subresources at /s/{token}/api/*
    // are an API surface for anonymous fetch clients too — StartsWithSegments("/api") alone sent their
    // middleware-class exceptions to the HTML page. Body is the canonical ApiError (D31/D32), the only
    // error shape on the wire; pinned by ApiErrorBackstopTests.
    static bool IsApiPath(HttpContext ctx) =>
        ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        || (ctx.Request.Path.StartsWithSegments("/s", StringComparison.OrdinalIgnoreCase)
            && (ctx.Request.Path.Value?.Contains("/api/", StringComparison.Ordinal) ?? false));

    app.UseWhen(IsApiPath,
        api => api.UseExceptionHandler(err => err.Run(async ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await ctx.Response.WriteAsJsonAsync(ApiError.ServerError("An unexpected error occurred."));
        })));
    app.UseWhen(ctx => !IsApiPath(ctx),
        pages => pages.UseExceptionHandler("/Error"));
}

// Reverse proxy: trust forwarded headers so OIDC redirects use the correct scheme/host.
// KnownNetworks/KnownProxies are cleared so headers are trusted from any upstream proxy
// (safe only behind a reverse proxy that sets them, never directly internet-facing — README requires it).
var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedOptions.KnownIPNetworks.Clear();
forwardedOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedOptions);

// Must be before UseStaticFiles and UseRouting so all responses are eligible for compression.
app.UseResponseCompression();

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(webRootPath),
    RequestPath = "",
    // C2: assets carry no Cache-Control by default → per-nav 304 chatter. asp-append-version busts
    // the URL on change, so versioned hits cache hard and unversioned hits get a short TTL. Hashed
    // Vite chunks (chunks/*-<hash>.js) get no ?v= but are just as immutable — the filename is the bust.
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers.CacheControl =
            StaticAssetCachePolicy.CacheControlFor(
                ctx.Context.Request.Query.ContainsKey("v"),
                ctx.Context.Request.Path.Value ?? string.Empty)
});

app.UseRouting();
app.UseSession();
// After UseRouting: the share-link partition key comes from a ROUTE value, which does not exist until
// routing has run. Before UseAuthentication: a flood is then rejected without paying for the token
// lookup that authentication would otherwise do on every request.
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// H1: liveness endpoint for external monitors + container healthchecks. AllowAnonymous is
// required — the global FallbackPolicy (RequireAuthenticatedUser) would otherwise 401 it. Body carries
// no auth data; a cheap DB probe distinguishes "app up" from "app up but DB unreachable" (503).
app.MapGet("/healthz", async (
    IDbContextFactory<RouteBaseDbContext> contextFactory, CancellationToken ct) =>
        await RouteBaseWeb.Services.Health.HealthProbe.IsHealthyAsync(contextFactory, ct)
            ? Results.Text("ok", "text/plain")
            : Results.Text("unavailable", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable))
    .AllowAnonymous();

app.MapHub<RecordingHub>("/hubs/recording");
app.MapControllers();
// No runtime OpenAPI endpoint (D8, decided 2026-07-23): the build-time document is the only capture path, and
// a public route map is free reconnaissance on a personal server. AddOpenApi() stays — doc-gen needs it.
// AllowAnonymous below is REQUIRED, not a relaxation. MapStaticAssets registers real ENDPOINTS for the
// fingerprinted asset URLs (…/core.25h2pjdror.js), and an endpoint with no authorization metadata inherits
// the global FallbackPolicy — so every script and stylesheet demanded a login and 302'd to the identity
// provider. That was invisible while every page required auth; the anonymous share view is the first page
// that ever requests an asset without a session.
//
// It grants nothing new: app.UseStaticFiles above runs BEFORE UseAuthentication, so the same wwwroot bytes
// are already served anonymously at their un-fingerprinted URLs. This only makes the two paths agree.
app.MapStaticAssets().AllowAnonymous();
app.MapRazorPages().WithStaticAssets();

// Staging TTL sweep now runs periodically via StagingSweepService (PI-07) — its immediate first pass
// replaces the former startup one-shots.

// Localhost mode: open the app in the default browser once Kestrel is listening. Server mode never does this.
if (localMode.Enabled)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        // app.Urls is the CONFIGURED address, not proof of a bound socket: a TestServer boot fills it from
        // the same "urls" config and reaches this line too (it did — dotnet test opened a real browser).
        // Not opening a window under test is therefore the opener's job, not this null check's.
        var url = app.Urls.FirstOrDefault();
        if (url is null) return;
        app.Services.GetRequiredService<ILocalModeBrowserOpener>().Open(url, app.Logger);
    });
}

app.Run();

// The full-pipeline tests (Tests/Hosting) boot this file through WebApplicationFactory<Program>,
// which needs the generated entry-point class to be publicly visible.
public partial class Program { }

