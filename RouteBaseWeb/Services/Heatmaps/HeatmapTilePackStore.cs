using System.Buffers.Binary;
using RouteBaseWeb.Services.Storage;
using Microsoft.Win32.SafeHandles;

namespace RouteBaseWeb.Services.Heatmaps;

/// <summary>
/// Filesystem tile storage: one packed <c>.htiles</c> file per heatmap instead of thousands of DB blob
/// rows. Layout (ZIP-style — body appended as tiles are rendered, lookup index written last so writing
/// is single-pass and streams straight to disk):
/// <code>
/// [png0][png1]…[png n-1]                                        # body
/// [n × { layer:u8, z:u8, x:u32, y:u32, offset:u64, len:u32 }]   # index
/// [indexOffset:u64, tileCount:u32, version:u16, magic "HTIL"]   # 18-byte footer at EOF
/// </code>
/// Reads parse the footer+index once (cached by <see cref="HeatmapTileIndexCache"/>), then range-read one
/// tile's bytes. Wraps the domain-agnostic <see cref="FileSystemBlobStore"/> for path fan-out / deletion;
/// streaming writes are done here (PutAsync buffers whole blobs, a pyramid does not fit that).
/// </summary>
public sealed class HeatmapTilePackStore
{
    private const string Namespace = "heatmaps";
    private const string Variant = "tiles";
    private const string Ext = "htiles";

    private readonly FileSystemBlobStore _blobs;
    private readonly HeatmapTileIndexCache _indexCache;
    private readonly string _namespaceDir;

    private readonly TimeProvider _clock;

    public HeatmapTilePackStore(HeatmapStorageOptions options, HeatmapTileIndexCache indexCache, TimeProvider clock)
    {
        _clock = clock;
        _blobs = new FileSystemBlobStore(options.Root);
        _indexCache = indexCache;
        _namespaceDir = Path.Combine(options.Root, Namespace);
    }

    private string PathFor(int heatmapId) => _blobs.GetPath(Namespace, heatmapId, Variant, Ext);

    /// <summary>Absolute path of a heatmap's packed tile file (computed, touches no disk).</summary>
    public string GetPhysicalPath(int heatmapId) => PathFor(heatmapId);

    /// <summary>Open a streaming writer for a fresh generation. Bytes go to a sibling <c>.tmp</c> and are
    /// promoted with an atomic rename on <see cref="HeatmapTilePackWriter.CommitAsync"/>.</summary>
    public HeatmapTilePackWriter BeginWrite(int heatmapId) => new(PathFor(heatmapId));

    /// <summary>PNG bytes for one tile, or null if the file, layer, or {z,x,y} isn't present (the caller
    /// serves a transparent tile). <paramref name="generatedAtTicks"/> keys the parsed-index cache so a
    /// regenerate (new timestamp) is never served from a stale index.</summary>
    public async Task<byte[]?> ReadTileAsync(
        int heatmapId, long generatedAtTicks, string layer, int z, int x, int y, CancellationToken ct = default)
    {
        if (!HtilesFormat.TryLayerId(layer, out byte layerId))
            return null;

        var path = PathFor(heatmapId);
        var index = await _indexCache.GetOrLoadAsync(heatmapId, generatedAtTicks, path, ct);
        if (index is null || !index.TryGetValue(HtilesFormat.Key(layerId, z, x, y), out var loc))
            return null;

        // HM-12 hardening — this path must never throw for any file state (the caller's contract is
        // "null ⇒ transparent tile"): entry sanity vs corrupt indexes (a real tile PNG is ≥ ~60 B and
        // KB-scale; 4 MB is far above any 256² tile), IO failures when the pack was deleted while the
        // meta (≤30 s) / index (≤10 min) caches were still warm, and a PNG-signature check so a stale
        // cached index range-read against an atomically-replaced pack can't serve garbage bytes.
        if (loc.Len is < 8 or > 4_000_000 || loc.Offset < 0)
            return null;

        byte[] buffer;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            buffer = new byte[loc.Len];
            int read = await RandomAccess.ReadAsync(handle, buffer, loc.Offset, ct);
            if (read != loc.Len)
                return null;
        }
        catch (IOException) // covers FileNotFound/DirectoryNotFound/sharing/disk errors
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return buffer.AsSpan(0, 8).SequenceEqual(PngEncoder.SignatureSpan) ? buffer : null;
    }

    /// <summary>Delete a heatmap's tile file. Best-effort and idempotent — a missing file is not an error.</summary>
    public void Delete(int heatmapId) => _blobs.DeleteVariant(Namespace, heatmapId, Variant, Ext);

    /// <summary>Heatmap ids that currently have a tile file on disk. The orphan sweep reconciles these
    /// against live Heatmap rows; normal reads never scan — they open by id.</summary>
    public IEnumerable<int> EnumerateStoredHeatmapIds() =>
        _blobs.EnumerateIds(Namespace, Variant, Ext).Select(id => (int)id);

    /// <summary>Delete abandoned <c>.tmp</c> pack files older than <paramref name="olderThan"/> —
    /// hard-crash leftovers the writer's disposal cleanup never removed (HM-13). The age guard
    /// protects an in-flight writer's temp file; called by the daily sweep. Returns the count deleted.</summary>
    public int DeleteAbandonedTempFiles(TimeSpan olderThan)
    {
        if (!Directory.Exists(_namespaceDir)) return 0;

        var cutoff = _clock.GetUtcNow().UtcDateTime - olderThan;
        int count = 0;
        foreach (var path in Directory.EnumerateFiles(_namespaceDir, $"*.{Ext}.tmp", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) >= cutoff) continue;
                File.Delete(path);
                count++;
            }
            catch (IOException) { /* best-effort — retried next sweep */ }
        }
        return count;
    }
}

/// <summary>
/// Streaming writer for one heatmap's packed file. Append PNGs zoom-by-zoom (matching the pyramid
/// builder's flush batches), then <see cref="CommitAsync"/> to write the index+footer and atomically
/// rename into place. Only the ~22-byte-per-tile index is held in memory; PNG bytes stream to disk.
/// Dispose without committing (or call <see cref="Abort"/>) deletes the temp file.
/// </summary>
public sealed class HeatmapTilePackWriter : IAsyncDisposable
{
    private readonly string _path;
    private readonly string _tmp;
    private readonly FileStream _stream;
    private readonly List<HtilesFormat.TileEntry> _entries = new();
    private bool _committed;

    internal HeatmapTilePackWriter(string path)
    {
        _path = path;
        _tmp = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"htiles path has no directory: {path}"));
        _stream = new FileStream(_tmp, FileMode.Create, FileAccess.Write, FileShare.None);
    }

    /// <summary>Append a batch of rendered tiles for one layer, recording each one's index entry.</summary>
    public async Task AppendAsync(string layer, IReadOnlyList<HeatmapTileData> tiles, CancellationToken ct = default)
    {
        byte layerId = HtilesFormat.LayerId(layer);
        foreach (var t in tiles)
        {
            long offset = _stream.Position;
            await _stream.WriteAsync(t.Png, ct);
            _entries.Add(new HtilesFormat.TileEntry(layerId, (byte)t.Z, (uint)t.X, (uint)t.Y, offset, t.Png.Length));
        }
    }

    /// <summary>Write the index + footer and atomically promote the temp file. No-op body if no tiles were
    /// appended still produces a valid (empty-index) file, so readers see a real "no tiles" rather than a
    /// missing file mid-generation.</summary>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        long indexOffset = _stream.Position;
        var buffer = HtilesFormat.SerializeIndexAndFooter(_entries, indexOffset);
        await _stream.WriteAsync(buffer, ct);
        await _stream.FlushAsync(ct);
        await _stream.DisposeAsync();
        File.Move(_tmp, _path, overwrite: true);
        _committed = true;
    }

    public void Abort()
    {
        try { File.Delete(_tmp); } catch (DirectoryNotFoundException) { /* already gone */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_committed) return;
        await _stream.DisposeAsync();
        Abort();
    }
}

/// <summary>On-disk format for the packed <c>.htiles</c> file: constants, the index record, the
/// packed lookup key, and (de)serialization of the index + footer. Shared by the writer and the
/// index cache so the byte layout lives in exactly one place.</summary>
internal static class HtilesFormat
{
    public const ushort Version = 1;
    public const int FooterSize = 18;          // indexOffset(8) + tileCount(4) + version(2) + magic(4)
    public const int RecordSize = 22;          // layer(1) + z(1) + x(4) + y(4) + offset(8) + len(4)
    private static readonly byte[] Magic = "HTIL"u8.ToArray();

    public readonly record struct TileEntry(byte Layer, byte Z, uint X, uint Y, long Offset, int Len);

    // V1 renders only "count_log"; the byte id reserves room for future speed/HR/recency channels.
    public static bool TryLayerId(string layer, out byte id)
    {
        id = 0;
        return layer == HeatmapGenerationService.CountLogLayer;
    }

    public static byte LayerId(string layer) =>
        TryLayerId(layer, out var id) ? id : throw new ArgumentOutOfRangeException(nameof(layer), layer, "Unknown heatmap layer.");

    /// <summary>Pack (layer, z, x, y) into a dictionary key. x,y fit in 24 bits (max tile index at z=16
    /// is 65535); z ≤ 16 and layer ≤ a handful, so 8+8+24+24 bits is ample.</summary>
    public static ulong Key(byte layer, int z, int x, int y) =>
        ((ulong)layer << 56) | ((ulong)(byte)z << 48) | ((ulong)(uint)x << 24) | (uint)y;

    public static byte[] SerializeIndexAndFooter(IReadOnlyList<TileEntry> entries, long indexOffset)
    {
        var buffer = new byte[entries.Count * RecordSize + FooterSize];
        var span = buffer.AsSpan();
        int o = 0;
        foreach (var e in entries)
        {
            span[o] = e.Layer;
            span[o + 1] = e.Z;
            BinaryPrimitives.WriteUInt32LittleEndian(span[(o + 2)..], e.X);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(o + 6)..], e.Y);
            BinaryPrimitives.WriteInt64LittleEndian(span[(o + 10)..], e.Offset);
            BinaryPrimitives.WriteInt32LittleEndian(span[(o + 18)..], e.Len);
            o += RecordSize;
        }

        BinaryPrimitives.WriteInt64LittleEndian(span[o..], indexOffset);
        BinaryPrimitives.WriteInt32LittleEndian(span[(o + 8)..], entries.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(o + 12)..], Version);
        Magic.CopyTo(span[(o + 14)..]);
        return buffer;
    }

    /// <summary>Read footer + index from a file into a lookup dictionary, or null if the file is
    /// absent/too short/corrupt/version-mismatched (treated as "no tiles" — the caller serves
    /// transparent, never throws).</summary>
    public static async Task<Dictionary<ulong, (long Offset, int Len)>?> ReadIndexAsync(
        string path, CancellationToken ct, ILogger? logger = null)
    {
        var entries = await ReadEntriesAsync(path, ct, logger);
        if (entries is null) return null;

        var map = new Dictionary<ulong, (long, int)>(entries.Count);
        foreach (var e in entries)
            map[Key(e.Layer, e.Z, (int)e.X, (int)e.Y)] = (e.Offset, e.Len);
        return map;
    }

    /// <summary>Parse the index into raw entries (decoded z/x/y preserved), or null if absent/corrupt/
    /// written by a different format version — a stale-version pack is data no writer will ever add to
    /// again, so treating it as "no tiles" is safe: the caller serves transparent and a real regenerate
    /// (Attach/Regenerate) writes a fresh pack at the current <see cref="Version"/>.</summary>
    public static async Task<List<TileEntry>?> ReadEntriesAsync(string path, CancellationToken ct, ILogger? logger = null)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            long length = RandomAccess.GetLength(handle);
            if (length < FooterSize) return null;

            var footer = new byte[FooterSize];
            await RandomAccess.ReadAsync(handle, footer, length - FooterSize, ct);
            if (!footer.AsSpan(14, 4).SequenceEqual(Magic)) return null;
            ushort packVersion = BinaryPrimitives.ReadUInt16LittleEndian(footer.AsSpan(12));
            if (packVersion != Version)
            {
                // dm-9: the one null-return worth an operator's eye — a whole valid-looking pack
                // discarded as "no tiles" over its format version; only a regenerate resolves it.
                logger?.LogWarning(
                    "Discarding htiles pack {Path}: format version {Found}, expected {Expected}.",
                    path, packVersion, Version);
                return null;
            }

            long indexOffset = BinaryPrimitives.ReadInt64LittleEndian(footer);
            int count = BinaryPrimitives.ReadInt32LittleEndian(footer.AsSpan(8));
            long indexBytes = (long)count * RecordSize;
            if (indexOffset < 0 || indexBytes != length - FooterSize - indexOffset) return null;

            var index = new byte[indexBytes];
            await RandomAccess.ReadAsync(handle, index, indexOffset, ct);

            var entries = new List<TileEntry>(count);
            for (int i = 0, o = 0; i < count; i++, o += RecordSize)
            {
                byte layer = index[o];
                byte z = index[o + 1];
                uint x = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(o + 2));
                uint y = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(o + 6));
                long offset = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(o + 10));
                int len = BinaryPrimitives.ReadInt32LittleEndian(index.AsSpan(o + 18));
                entries.Add(new TileEntry(layer, z, x, y, offset, len));
            }
            return entries;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
