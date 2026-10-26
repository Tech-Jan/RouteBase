namespace RouteBaseWeb.Models;

/// <summary>Provider-neutral "API rate limit hit" signal — the worker aborts the import run
/// and surfaces the message instead of counting activities as failed.</summary>
public class ProviderRateLimitException : Exception
{
    public ProviderRateLimitException(string message) : base(message) { }
}
