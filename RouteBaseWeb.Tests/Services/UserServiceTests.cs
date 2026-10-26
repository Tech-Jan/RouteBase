using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// SH-03: user search matches display name and email only on the exact value (case-insensitive),
/// so authenticated users can neither enumerate accounts by name fragment (q="b" → "b3n") nor
/// harvest emails by substring (q="@gmail").
/// </summary>
public class UserServiceTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;
    private UserService _service = null!;

    public async Task InitializeAsync()
    {
        var connectionString = $"Data Source=file:testdb_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(connectionString).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();
        _service = new UserService(_context, NullLogger<UserService>.Instance, TimeProvider.System);

        await SeedUserAsync("carol", "Carol", "carol@example.com");
        await SeedUserAsync("b3n", "B3n", "BigMaster@test.ok");
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

    [Fact]
    public async Task SearchUsers_EmailSubstring_NoMatch()
    {
        // A partial-email query must not surface anyone by email alone.
        var results = await _service.SearchUsersAsync("@example", excludeUserId: "someone-else");
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchUsers_ExactEmail_Matches()
    {
        var results = await _service.SearchUsersAsync("carol@example.com", excludeUserId: "someone-else");
        Assert.Single(results);
        Assert.Equal("carol", results[0].UserId);
    }

    [Fact]
    public async Task SearchUsers_DisplayNameSubstring_NoMatch()
    {
        // A name fragment must not surface anyone (account enumeration).
        var results = await _service.SearchUsersAsync("car", excludeUserId: "someone-else");
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchUsers_ExactDisplayName_Matches()
    {
        var results = await _service.SearchUsersAsync("carol", excludeUserId: "someone-else");
        Assert.Single(results);
        Assert.Equal("carol", results[0].UserId);
    }

    [Fact]
    public async Task SearchUsers_ExactDisplayName_CaseInsensitive()
    {
        var results = await _service.SearchUsersAsync("b3N", excludeUserId: "someone-else");
        Assert.Single(results);
        Assert.Equal("b3n", results[0].UserId);
    }

    [Fact]
    public async Task SearchUsers_ExactEmail_CaseInsensitive()
    {
        var results = await _service.SearchUsersAsync("bigmaster@TEST.ok", excludeUserId: "someone-else");
        Assert.Single(results);
        Assert.Equal("b3n", results[0].UserId);
    }
}
