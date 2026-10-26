namespace RouteBaseWeb.Models;

// Response contracts for ApiKeyController. Serialized camelCase (web defaults);
// wire shapes pinned by ApiKeyControllerWireShapeTests. Each mirrors a TS type — edit both
// in the same commit.

/// <summary>Response of GET /api/apikeys/status. Mirrors TS <c>ApiKeyStatusResponse</c> (pages/keys-and-data.ts).</summary>
public sealed record ApiKeyStatusResponse(bool HasKey, DateTimeOffset? CreatedAt);

/// <summary>
/// Response of POST /api/apikeys/generate. <c>Key</c> is the plaintext key — shown once, never
/// retrievable again. Mirrors TS <c>ApiKeyGenerateResponse</c> (pages/keys-and-data.ts).
/// </summary>
public sealed record ApiKeyGenerateResponse(string Key, DateTimeOffset CreatedAt);
