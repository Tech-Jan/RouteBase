namespace RouteBaseWeb.Services.Photos;

/// <summary>
/// Where photo rendition files live. <see cref="Root"/> defaults to the DB's data dir
/// (<c>&lt;exeDir&gt;/database</c>) so photos back up as a unit with the DB (§1b); the store appends the
/// <c>photos/</c> namespace + fan-out beneath it. Kept as config, not a constant, so a second volume /
/// backend is DI config later (§1c) — and so tests can point it at a temp dir.
/// </summary>
public sealed class PhotoStorageOptions
{
    public required string Root { get; init; }
}
