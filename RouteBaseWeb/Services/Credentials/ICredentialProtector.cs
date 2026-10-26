namespace RouteBaseWeb.Services.Credentials;

/// <summary>
/// Encrypts credential values at rest (decision ⑥ / SVC-10) using ASP.NET Data Protection.
/// Every stored value goes through <see cref="Protect"/>; <see cref="Unprotect"/> passes an
/// unprotected value through unchanged rather than throwing on one.
/// </summary>
public interface ICredentialProtector
{
    string Protect(string plaintext);

    /// <summary>Null when the value is a protected payload the current key ring cannot decipher —
    /// the caller treats it as a missing credential (user reconnects), never as an error.</summary>
    string? Unprotect(string stored);

    /// <summary>True when the value is a Data Protection payload (vs legacy plaintext).</summary>
    bool IsProtected(string stored);
}
