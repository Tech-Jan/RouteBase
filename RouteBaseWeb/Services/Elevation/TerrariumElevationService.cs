using System.Net;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Services.Elevation;

/// <summary>
/// Replaces GPS altitude with exact terrarium DEM elevation (full per-point replace — no offset).
/// Per call: the valid points' 4-pixel bilinear stencils resolve to a distinct tile set; cached tiles
/// come from <see cref="DemTileStore"/>, misses are fetched from the AWS US bucket (≤4 concurrent,
/// 10 s timeout), stored, and decoded once each into a per-operation cache. A tile that stays missing
/// (fetch failed / decode failed / no coverage) leaves the points whose stencil touches it on their
/// original GPS altitude, honouring the <see cref="IElevationService"/> fallback contract.
/// </summary>
public sealed class TerrariumElevationService : IElevationService
{
    private const int Zoom = TerrariumTileMath.TerrariumZoom;
    private const int FetchConcurrency = 4;
    private const int MaxConsecutiveFailures = 3; // endpoint-down tripwire — stop hammering, finish with cache
    private const long MaxTileBytes = 1024 * 1024; // real tiles are ~30–200 KB; cap bounds a hostile body (EL-02)
    private const double MinReasonableAltitude = -500.0;
    private const double MaxReasonableAltitude = 9000.0;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DemTileStore _tileStore;
    private readonly ILogger<TerrariumElevationService> _logger;

    public TerrariumElevationService(
        IHttpClientFactory httpClientFactory,
        DemTileStore tileStore,
        ILogger<TerrariumElevationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tileStore = tileStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> ApplyDemElevationAsync(List<GpsData> points)
    {
        var validIndices = new List<int>();
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].GpsLat.HasValue && points[i].GpsLong.HasValue)
                validIndices.Add(i);
        }
        if (validIndices.Count == 0)
            return 0;

        // Distinct tile set across every point's 4-pixel stencil (dozens of tiles, never per-point calls).
        var neededTiles = new HashSet<(int X, int Y)>();
        foreach (int i in validIndices)
            AddStencilTiles(neededTiles, points[i].GpsLat!.Value, points[i].GpsLong!.Value);

        var decoded = new Dictionary<(int X, int Y), float[]>();
        var missing = new List<(int X, int Y)>();

        // Sequential store reads (EF context is not thread-safe); decode each hit once.
        foreach (var tile in neededTiles)
        {
            var png = await _tileStore.ReadTileAsync(Zoom, tile.X, tile.Y);
            if (png is null) { missing.Add(tile); continue; }
            if (!TryDecode(png, tile, out var elevations))
            {
                // Poisoned cache entry (stored by a pre-validation build) — purge + refetch so it
                // self-heals instead of shadowing the tile forever. EL-01.
                await _tileStore.DeleteTileAsync(Zoom, tile.X, tile.Y);
                missing.Add(tile);
                continue;
            }
            decoded[tile] = elevations;
        }

        if (missing.Count > 0)
            await FetchDecodeStoreAsync(missing, decoded);

        int replaced = ApplyBilinear(points, validIndices, decoded);
        _logger.LogInformation(
            "DEM replace: {Replaced}/{Valid} points across {Tiles} tiles ({Cached} cached, {Fetched} decoded new)",
            replaced, validIndices.Count, neededTiles.Count, neededTiles.Count - missing.Count,
            decoded.Count - (neededTiles.Count - missing.Count));
        return replaced;
    }

    /// <summary>Fetches missing tiles (≤4 concurrent), then sequentially stores + decodes each.</summary>
    private async Task FetchDecodeStoreAsync(
        List<(int X, int Y)> missing, Dictionary<(int X, int Y), float[]> decoded)
    {
        var client = _httpClientFactory.CreateClient();
        // A hung endpoint must not stall finalization — bound each request independently of the pipeline.
        client.Timeout = FetchTimeout;
        // Bound the buffered body too: an overrun throws inside GetAsync → FetchStatus.Failure. EL-02.
        client.MaxResponseContentBufferSize = MaxTileBytes;

        var fetched = new List<((int X, int Y) Tile, byte[] Png)>();
        var gate = new object();
        int consecutiveFailures = 0;
        bool aborted = false;

        using var semaphore = new SemaphoreSlim(FetchConcurrency);
        var tasks = new List<Task>();
        foreach (var tile in missing)
        {
            lock (gate) { if (aborted) break; }
            await semaphore.WaitAsync();
            lock (gate) { if (aborted) { semaphore.Release(); break; } }

            var current = tile;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var (status, png) = await FetchTileAsync(client, current);
                    lock (gate)
                    {
                        switch (status)
                        {
                            case FetchStatus.Success:
                                consecutiveFailures = 0;
                                fetched.Add((current, png!));
                                break;
                            case FetchStatus.Missing: // 404 — no coverage, endpoint is up
                                consecutiveFailures = 0;
                                _logger.LogWarning("DEM tile {Z}/{X}/{Y} has no coverage (404)", Zoom, current.X, current.Y);
                                break;
                            case FetchStatus.Failure:
                                if (++consecutiveFailures >= MaxConsecutiveFailures)
                                {
                                    aborted = true;
                                    _logger.LogWarning(
                                        "Aborting DEM fetch after {Failures} consecutive failures; finishing with cache",
                                        consecutiveFailures);
                                }
                                break;
                        }
                    }
                }
                finally { semaphore.Release(); }
            }));
        }
        await Task.WhenAll(tasks);

        // Decode + store sequentially (EF context is single-threaded). Decode FIRST: bytes that don't
        // decode must never enter the no-expiry cache, or they'd shadow the tile permanently. EL-01.
        foreach (var (tile, png) in fetched)
        {
            if (!TryDecode(png, tile, out var elevations)) continue;
            await _tileStore.InsertTileAsync(Zoom, tile.X, tile.Y, png);
            decoded[tile] = elevations;
        }
    }

    /// <summary>One bilinear pass: full replace where a value is available and plausible; else keep GPS.</summary>
    private static int ApplyBilinear(
        List<GpsData> points, List<int> validIndices, Dictionary<(int X, int Y), float[]> decoded)
    {
        float[]? GetTile((int TileX, int TileY) t) =>
            decoded.TryGetValue((t.TileX, t.TileY), out var arr) ? arr : null;

        int replaced = 0;
        foreach (int i in validIndices)
        {
            var dem = TerrariumTileMath.SampleBilinear(points[i].GpsLat!.Value, points[i].GpsLong!.Value, GetTile);
            if (dem is null || dem.Value < MinReasonableAltitude || dem.Value > MaxReasonableAltitude)
                continue; // missing stencil tile or implausible sample → keep GPS altitude
            points[i].Altitude = dem.Value;
            replaced++;
        }
        return replaced;
    }

    private bool TryDecode(byte[] png, (int X, int Y) tile, out float[] elevations)
    {
        try
        {
            elevations = TerrariumTileDecoder.DecodeElevations(png);
            return true;
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning(ex, "DEM tile {Z}/{X}/{Y} failed to decode; treating as missing", Zoom, tile.X, tile.Y);
            elevations = [];
            return false;
        }
    }

    private async Task<(FetchStatus Status, byte[]? Png)> FetchTileAsync(HttpClient client, (int X, int Y) tile)
    {
        var url = TerrariumTileMath.TileUrlTemplate
            .Replace("{z}", Zoom.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{x}", tile.X.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{y}", tile.Y.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        try
        {
            using var response = await client.GetAsync(url);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (FetchStatus.Missing, null);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("DEM tile {Z}/{X}/{Y} fetch returned {Status}", Zoom, tile.X, tile.Y, response.StatusCode);
                return (FetchStatus.Failure, null);
            }
            return (FetchStatus.Success, await response.Content.ReadAsByteArrayAsync());
        }
        catch (Exception ex)
        {
            // Covers TaskCanceledException from the 10 s timeout and network errors.
            _logger.LogWarning(ex, "DEM tile {Z}/{X}/{Y} fetch failed", Zoom, tile.X, tile.Y);
            return (FetchStatus.Failure, null);
        }
    }

    /// <summary>Adds the up-to-4 tiles a coordinate's bilinear stencil touches (near edges it spans
    /// several) straight into <paramref name="tiles"/> — no per-point iterator/array allocation.</summary>
    private static void AddStencilTiles(HashSet<(int X, int Y)> tiles, double lat, double lon)
    {
        var s = TerrariumTileMath.ComputeStencil(lat, lon);
        AddTile(tiles, s.X0,     s.Y0);
        AddTile(tiles, s.X0 + 1, s.Y0);
        AddTile(tiles, s.X0,     s.Y0 + 1);
        AddTile(tiles, s.X0 + 1, s.Y0 + 1);
    }

    private static void AddTile(HashSet<(int X, int Y)> tiles, int px, int py)
    {
        var t = TerrariumTileMath.PixelToTile(px, py);
        tiles.Add((t.TileX, t.TileY));
    }

    private enum FetchStatus { Success, Missing, Failure }
}
