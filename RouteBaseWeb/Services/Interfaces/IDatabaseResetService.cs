using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Interfaces;

public interface IDatabaseResetService
{
    /// <summary>
    /// Deletes exactly the data groups specified in <paramref name="opts"/>.
    /// </summary>
    Task ResetDatabaseAsync(DeleteOptions opts);
}
