namespace RouteBaseWeb.Auth;

/// <summary>
/// Scheme name for the mobile API-key authentication handler. Extracted from the string literals it was
/// spelled with so policies can exclude it by symbol rather than by a copy of the text.
/// </summary>
public static class ApiKeyDefaults
{
    public const string AuthenticationScheme = "ApiKey";
}
