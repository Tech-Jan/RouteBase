namespace RouteBaseWeb.Services.ErrorLog;

/// <summary>
/// Belt-and-braces recursion guard for the logger mirror. The drain service sets <see cref="Active"/> around
/// each store write so any log raised inside the write (e.g. a future raw-ADO retry logger, or an EF failure
/// on a category the prefix list doesn't enumerate) is dropped by the provider instead of re-entering the
/// store. AsyncLocal flows across the write's awaits. The category-prefix ignores are the primary guard
/// (§4); this covers everything the static prefix list can't name.
/// </summary>
public static class ErrorLogSuppression
{
    private static readonly AsyncLocal<bool> _active = new();

    public static bool Active
    {
        get => _active.Value;
        set => _active.Value = value;
    }
}
