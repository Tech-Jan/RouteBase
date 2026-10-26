using RouteBaseWeb.Services.Heatmaps;
using Microsoft.Extensions.Logging;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Heatmaps;

/// <summary>
/// Byte-level pin of the on-disk .htiles index/footer layout (data-model audit 2026-07-17, BLOB-02).
/// All other htiles tests round-trip through the same writer+reader pair, so a symmetric layout edit
/// (field reorder, record-size change) would stay green while every existing pack on disk misparses to
/// "no tiles". The literal below IS the format contract: record = layer(1)+z(1)+x(4)+y(4)+offset(8)+len(4),
/// footer = indexOffset(8)+tileCount(4)+version(2)+"HTIL" — change it only with a deliberate v2 migration.
/// </summary>
public class HtilesFormatGoldenTests
{
    private const string GoldenHex =                                       // per field, all little-endian:
        "00" + "03" + "05000000" + "06000000" + "0000000000000000" + "64000000"  // layer 0, z 3, x 5, y 6, offset 0, len 100
        + "01" + "10" + "FFFF0000" + "FEFF0000" + "6400000000000000" + "80841E00" // layer 1, z 16, x 65535, y 65534, offset 100, len 2_000_000
        + "0A00000000000000" + "02000000" + "0100" + "4854494C";                  // footer: indexOffset 10, count 2, version 1, magic "HTIL"

    private static readonly HtilesFormat.TileEntry[] Entries =
    {
        new(Layer: 0, Z: 3, X: 5, Y: 6, Offset: 0, Len: 100),
        new(Layer: 1, Z: 16, X: 65535, Y: 65534, Offset: 100, Len: 2_000_000),
    };

    [Fact]
    public void SerializeIndexAndFooter_ByteStable()
    {
        Assert.Equal(GoldenHex, Convert.ToHexString(HtilesFormat.SerializeIndexAndFooter(Entries, indexOffset: 10)));
    }

    [Fact]
    public async Task ReadEntries_ParsesGoldenBytes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"htiles-golden-{Guid.NewGuid():N}.htiles");
        try
        {
            var file = new byte[10 + GoldenHex.Length / 2];              // 10 fake body bytes + index/footer
            Convert.FromHexString(GoldenHex).CopyTo(file, 10);
            await File.WriteAllBytesAsync(path, file);

            var entries = await HtilesFormat.ReadEntriesAsync(path, CancellationToken.None);
            Assert.NotNull(entries);
            Assert.Equal(Entries, entries!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Data-model audit 2026-07-17, item G: the footer's version field was written but never read, so a
    // pack from a future/older format would be misparsed as current-format garbage instead of being
    // treated as regenerable "no tiles". Same GoldenHex with version bumped 0001 -> 0002.
    // dm-9: that discard must not be silent — the mismatch (and only the mismatch) logs a warning.
    [Fact]
    public async Task ReadEntries_VersionMismatch_ReturnsNullAndLogs()
    {
        string mismatchedHex = GoldenHex.Replace("0100" + "4854494C", "0200" + "4854494C", StringComparison.Ordinal);
        Assert.NotEqual(GoldenHex, mismatchedHex, StringComparer.Ordinal); // guard against the Replace silently no-op'ing

        string path = Path.Combine(Path.GetTempPath(), $"htiles-versionmismatch-{Guid.NewGuid():N}.htiles");
        try
        {
            var file = new byte[10 + mismatchedHex.Length / 2];
            Convert.FromHexString(mismatchedHex).CopyTo(file, 10);
            await File.WriteAllBytesAsync(path, file);

            var log = new CapturingLogger();
            var entries = await HtilesFormat.ReadEntriesAsync(path, CancellationToken.None, log);
            Assert.Null(entries);

            var entry = Assert.Single(log.Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Contains(path, entry.Message, StringComparison.Ordinal);
            Assert.Contains("version 2", entry.Message, StringComparison.Ordinal);
            Assert.Contains("expected 1", entry.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadEntries_MissingFile_StaysSilent()
    {
        var log = new CapturingLogger();
        var entries = await HtilesFormat.ReadEntriesAsync(
            Path.Combine(Path.GetTempPath(), $"htiles-absent-{Guid.NewGuid():N}.htiles"), CancellationToken.None, log);
        Assert.Null(entries);
        Assert.Empty(log.Entries);
    }
}

file sealed class CapturingLogger : ILogger
{
    public readonly List<(LogLevel Level, string Message)> Entries = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}
