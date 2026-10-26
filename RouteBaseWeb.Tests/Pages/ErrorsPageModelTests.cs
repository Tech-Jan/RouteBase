using System.Globalization;
using RouteBaseWeb.Configuration;
using RouteBaseWeb.Models;
using RouteBaseWeb.Pages.Admin;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.ErrorLog;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace RouteBaseWeb.Tests.Pages;

/// <summary>
/// ErrorsModel (OBS-04, /Admin/Errors): the Admin:Users gate 404s non-admins on every handler
/// (no existence leak), admin access reads newest-first rows with level/source filtering and page
/// clamping, and the POST actions delegate to ErrorLogStore + redirect (PRG). Real in-memory SQLite
/// (ErrorLogStoreTests fixture pattern) since ErrorLogStore has no interface to fake against.
/// </summary>
public class ErrorsPageModelTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;
    private ErrorLogStore _store = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:errorspage_test_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _context = new RouteBaseDbContext(options);
        // One connection held open so the shared-cache in-memory DB survives the store's open/close cycles.
        await _context.Database.OpenConnectionAsync();
        await _context.Database.EnsureCreatedAsync();
        await new DbSchemaManager(_context).EnsureErrorLogTableAsync();
        _store = new ErrorLogStore(_context, TimeProvider.System);
    }

    public async Task DisposeAsync()
    {
        await _context.Database.CloseConnectionAsync();
        await _context.DisposeAsync();
    }

    private static ErrorLogEntry Entry(string message, string level = "Error", string source = "api", string? ts = null) =>
        new(ts ?? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            source, "RouteBaseWeb.Api.FooController", level, message, null, null, "GET /api/foo", "user-1");

    private static ErrorsModel CreateModel(ErrorLogStore store, string userId, string[]? adminUsers)
    {
        var configData = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (adminUsers is not null)
            for (var i = 0; i < adminUsers.Length; i++)
                configData[$"Admin:Users:{i}"] = adminUsers[i];
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        return new ErrorsModel(store, new AdminGate(configuration, new FakeCurrentUser(userId), new LocalModeOptions()))
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private sealed class FakeCurrentUser(string userId) : ICurrentUserService
    {
        public string GetUserId() => userId;
        public string? TryGetUserId() => userId;
        public string? GetDisplayName() => null;
        public bool IsAdmin() => false;
        public void SetUserIdOverride(string overrideUserId) { }
        public IDisposable BeginUserScope(string userId) => NoopUserScope.Instance;
    }

    // ── Gate: 404 on every handler for a non-admin ─────────────────────────────

    [Fact]
    public async Task OnGet_NonAdmin_ReturnsNotFound()
    {
        var model = CreateModel(_store, "someone", ["admin-1"]);

        Assert.IsType<NotFoundResult>(await model.OnGetAsync());
    }

    [Fact]
    public async Task OnGet_NoAdminUsersConfigured_ReturnsNotFound()
    {
        var model = CreateModel(_store, "someone", adminUsers: null);

        Assert.IsType<NotFoundResult>(await model.OnGetAsync());
    }

    [Fact]
    public async Task OnPostPrune_NonAdmin_ReturnsNotFound()
    {
        var model = CreateModel(_store, "someone", ["admin-1"]);

        Assert.IsType<NotFoundResult>(await model.OnPostPruneAsync());
    }

    [Fact]
    public async Task OnPostClear_NonAdmin_ReturnsNotFound()
    {
        var model = CreateModel(_store, "someone", ["admin-1"]);

        Assert.IsType<NotFoundResult>(await model.OnPostClearAsync());
    }

    // ── Admin access: rows, filter, paging ──────────────────────────────────────

    [Fact]
    public async Task OnGet_Admin_ReturnsPageWithNewestFirstRows()
    {
        await _store.WriteAsync(Entry("first"));
        await _store.WriteAsync(Entry("second"));
        var model = CreateModel(_store, "admin-1", ["admin-1"]);

        var result = await model.OnGetAsync();

        Assert.IsType<PageResult>(result);
        Assert.Equal(["second", "first"], model.Rows.Select(r => r.Message), StringComparer.Ordinal);
        Assert.Equal(2, model.TotalCount);
    }

    [Fact]
    public async Task OnGet_LevelFilter_NarrowsRows()
    {
        await _store.WriteAsync(Entry("e1", level: "Error"));
        await _store.WriteAsync(Entry("c1", level: "Critical"));
        var model = CreateModel(_store, "admin-1", ["admin-1"]);
        model.Level = "Critical";

        await model.OnGetAsync();

        var row = Assert.Single(model.Rows);
        Assert.Equal("c1", row.Message);
    }

    [Fact]
    public async Task OnGet_OutOfRangePage_Clamps()
    {
        await _store.WriteAsync(Entry("only"));
        var model = CreateModel(_store, "admin-1", ["admin-1"]);
        model.P = 999;

        await model.OnGetAsync();

        Assert.Equal(1, model.P);
        Assert.Single(model.Rows);
    }

    // ── POST actions: delegate to the store + PRG redirect ──────────────────────

    [Fact]
    public async Task OnPostClear_Admin_EmptiesStore_AndRedirects()
    {
        await _store.WriteAsync(Entry("x"));
        var model = CreateModel(_store, "admin-1", ["admin-1"]);

        var result = await model.OnPostClearAsync();

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(0, await _store.CountAsync(null, null));
    }

    [Fact]
    public async Task OnPostPrune_Admin_PrunesOldRows_AndRedirects()
    {
        var old = DateTime.UtcNow.AddDays(-100).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        await _store.WriteAsync(Entry("old", ts: old));
        await _store.WriteAsync(Entry("fresh"));
        var model = CreateModel(_store, "admin-1", ["admin-1"]);

        var result = await model.OnPostPruneAsync();

        Assert.IsType<RedirectToPageResult>(result);
        var rows = await _store.GetPageAsync(null, null, 1, 50);
        var row = Assert.Single(rows);
        Assert.Equal("fresh", row.Message);
    }

    // ── ShortCategory helper ─────────────────────────────────────────────────────

    [Fact]
    public void ShortCategory_StripsNamespacePrefix()
    {
        Assert.Equal("FooController", ErrorsModel.ShortCategory("RouteBaseWeb.Api.FooController"));
        Assert.Equal("NoDot", ErrorsModel.ShortCategory("NoDot"));
    }
}
