using RouteBaseWeb.Services.Matching;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Matching;

/// <summary>
/// Pins GeoCellHash (TE-08) to the SQL geo_cell CAST-toward-zero semantics, bit-for-bit, against
/// hand-computed literals. Pure (no DB). GeoCellHash.Pack is now the single source of the cell hash
/// (the geo_cell column and its import inline formula were dropped — matcher hashes cells in-memory).
/// </summary>
public class GeoCellHashTests
{
    // (lat, lng, expected packed cell) — hand-computed with CAST-toward-zero truncation.
    public static IEnumerable<object[]> PackCases() => new[]
    {
        //                       (long)(lat*500)          (long)(lng*500)
        new object[] { 47.0009, 11.0000, 23500L * 1_000_000L + 5500L },     // 23500.45→23500
        new object[] { 46.9999, 10.9999, 23499L * 1_000_000L + 5499L },     // 23499.95→23499, 5499.95→5499
        new object[] { -0.0001, -0.0001, 0L },                              // −0.05→0 (toward zero, NOT floor −1)
        new object[] {  0.0001,  0.0001, 0L },                              //  0.05→0 (shares cell 0 — quirk)
        new object[] {  0.0020,  0.0000, 1L * 1_000_000L + 0L },            // exactly on the 1/500 boundary → 1
        new object[] { -0.0020,  0.0000, -1L * 1_000_000L + 0L },           // −1.0→−1
    };

    [Theory]
    [MemberData(nameof(PackCases))]
    public void Pack_MatchesSqlCastSemantics(double lat, double lng, long expected)
    {
        Assert.Equal(expected, GeoCellHash.Pack(lat, lng));
    }

    [Fact]
    public void WriteNeighbors_ProducesNineDistinctPackedCells()
    {
        long cell = GeoCellHash.Pack(47.0, 11.0);
        Span<long> nb = stackalloc long[9];
        GeoCellHash.WriteNeighbors(cell, nb);

        var expected = new[]
        {
            cell - 1000001, cell - 1000000, cell - 999999,
            cell - 1,       cell,           cell + 1,
            cell + 999999,  cell + 1000000, cell + 1000001,
        };
        Assert.Equal(expected, nb.ToArray());
        Assert.Equal(9, nb.ToArray().Distinct().Count());
    }
}
