using System.Security.Cryptography;

namespace RouteBaseWeb.Services.Tracks;

/// <summary>
/// Immutable, content-addressed store for packed track bytes (the <see cref="TrackBlobCodec"/> output).
/// An object is keyed by the SHA-256 of its bytes: written once, never updated, never mutated in place.
///
/// That immutability is what the whole content axis rests on — a hash can never point at different bytes, so
/// a decode can never go stale, "restore an old version" costs zero new bytes (the object is already there),
/// and no delete can destroy content another version still references. Identical content is stored once
/// (re-importing the same GPX, or two activities sharing a track).
///
/// The seam exists so the bytes can later move to the filesystem / object storage (the .htiles precedent)
/// without touching a single caller.
/// </summary>
public interface ITrackObjectStore
{
    /// <summary>Store bytes and return their sha. Idempotent: storing identical bytes twice is one object.</summary>
    Task<string> PutAsync(byte[] data, int codecVersion, int pointCount, CancellationToken ct = default);

    /// <summary>The bytes for a sha, or null when no such object exists.</summary>
    Task<byte[]?> GetAsync(string sha256, CancellationToken ct = default);

    /// <summary>
    /// Delete objects no <c>Activity_Version</c> references any more (GC after a permanent purge).
    /// Returns the number swept.
    ///
    /// <paramref name="candidates"/> narrows the sweep to the shas a purge could actually have orphaned —
    /// the deleted versions' objects. Pass them whenever they are known: the scan is then O(candidates)
    /// instead of a full anti-join over every object, and — more importantly — an object that some other
    /// writer has just PUT but not yet referenced is not a candidate, so it cannot be swept out from under
    /// the version row about to point at it. Null sweeps everything (a full wipe, where that is the point).
    /// </summary>
    Task<int> SweepUnreferencedAsync(IReadOnlyCollection<string>? candidates = null, CancellationToken ct = default);

    /// <summary>The address of these bytes. Pure — the one place the hash format is defined.</summary>
#pragma warning disable CA1308 // content address is lowercase hex — the defined hash format
    static string ShaOf(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
#pragma warning restore CA1308
}
