namespace RouteBaseWeb.Services.ErrorLog;

/// <summary>
/// One captured error row. Immutable value carried from the logger mirror through the channel to the store.
/// <paramref name="TimestampUtc"/> is the house "yyyy-MM-dd HH:mm:ss" (space) form; <paramref name="Source"/>
/// is "api" | "background"; <paramref name="Level"/> is "Error" | "Critical".
/// </summary>
public sealed record ErrorLogEntry(
    string TimestampUtc,
    string Source,
    string Category,
    string Level,
    string Message,
    string? ExceptionType,
    string? StackTrace,
    string? Path,
    string? UserId);
