using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace RouteBaseWeb.Services.Credentials;

public sealed class CredentialProtector : ICredentialProtector
{
    // Base64url form of the Data Protection magic header (09 F0 C9 F0 …) — every protected payload
    // starts with it, and no real credential does (Strava tokens are lowercase hex).
    private const string ProtectedPrefix = "CfDJ8";

    private readonly IDataProtector _protector;
    private readonly ILogger<CredentialProtector> _logger;

    public CredentialProtector(IDataProtectionProvider provider, ILogger<CredentialProtector> logger)
    {
        _protector = provider.CreateProtector("RouteBaseWeb.AppCredentials.v1");
        _logger = logger;
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public bool IsProtected(string stored) => stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal);

    public string? Unprotect(string stored)
    {
        // Pass an unprotected value through rather than throwing. No such row exists (every writer goes
        // through Protect), and a hand-edited one should read as the credential it plainly is.
        if (!IsProtected(stored)) return stored;

        try
        {
            return _protector.Unprotect(stored);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex,
                "Credential payload undecipherable (Data Protection key ring changed?) — treating as missing");
            return null;
        }
    }
}
