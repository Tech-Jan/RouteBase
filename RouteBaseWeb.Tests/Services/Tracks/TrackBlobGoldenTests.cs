using System.IO.Compression;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Tracks;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Tracks;

/// <summary>
/// DR-13 parity anchor: literals below were generated against v1 <see cref="DecodedTrack"/>/<see cref="TrackBlobCodec"/>
/// at commit cdffd29 (pre repr-swap), via a scratchpad console snippet compiling the same prod sources
/// (PerformanceBaseline method) — never re-derive them from a rebuilt fixture. Must stay green across the
/// int32-backed repr swap (I2-c2): same blob bytes, same decoded bits, bit-identical. The ONLY allowed edit
/// to this file post repr-swap was flipping the null-channel assertions in <see cref="Golden_Decode_BitStable"/>
/// from <c>Assert.Null</c> to <c>.IsNull</c> (c2 scope, per analysis.md DR-13); the 2026-07-22 data-model
/// audit additionally converted <see cref="Golden_Encode_ByteStable"/> to compare header + DECOMPRESSED
/// payload — same literal, same contract, minus the zlib-runtime dependence (the compressed form varies by
/// .NET runtime patch; see TrackBlobWireCompatTests).
/// </summary>
public class TrackBlobGoldenTests
{
    private const int TrackId = 42;

    private const string GoldenHex =
        "0106000000FF0000789C01670098FF013B8089ECE60C020402020080B0F82C0283D7E15982A7E92CFDA9EA55FED9E2820100E1B9FC7401E683A9E90181CAAC74FED3D4AB01DF8DD1A002013DD00F6468A5025A000064278C011DE60100F4036427AF04D206890500F0010A0D18143100140A13214027BB9A29D8";

    private static List<GpsData> Fixture()
    {
        var t0 = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        return new List<GpsData>
        {
            new() { DateTimeUtc = t0,               GpsLat = 47.123456,    GpsLong = -122.654321,   Altitude = 100.0, Distance = 0.0,  Speed = 2.5,  HeartRate = 120, GradeSmooth = 1.0,  Cadence = null, Power = null },
            new() { DateTimeUtc = t0.AddSeconds(1),  GpsLat = 47.1234565,   GpsLong = -122.6543215,  Altitude = null,  Distance = 5.0,  Speed = 3.0,  HeartRate = 125, GradeSmooth = 1.5,  Cadence = null, Power = null },
            new() { DateTimeUtc = null,              GpsLat = -47.000001,  GpsLong = 122.000001,    Altitude = 105.0, Distance = 3.0,  Speed = 2.8,  HeartRate = 118, GradeSmooth = 0.5,  Cadence = null, Power = null },
            new() { DateTimeUtc = t0.AddSeconds(3),  GpsLat = 0.0,          GpsLong = 0.0,           Altitude = 110.2, Distance = 10.0, Speed = 0.0,  HeartRate = 130, GradeSmooth = -1.2, Cadence = null, Power = null },
            new() { DateTimeUtc = t0.AddSeconds(4),  GpsLat = -89.999999,   GpsLong = 179.999999,    Altitude = 95.5,  Distance = 8.5,  Speed = 4.25, HeartRate = 140, GradeSmooth = 2.0,  Cadence = null, Power = null },
            new() { DateTimeUtc = t0.AddSeconds(5),  GpsLat = 47.123456,    GpsLong = -122.654321,   Altitude = 100.0, Distance = 20.0, Speed = 1.0,  HeartRate = 115, GradeSmooth = 0.0,  Cadence = null, Power = null },
        };
    }

    [Fact]
    public void Golden_Encode_ByteStable()
    {
        var t = DecodedTrackExtensions.FromGpsDataList(Fixture());
        byte[] encoded = TrackBlobCodec.Encode(t);
        byte[] golden = Convert.FromHexString(GoldenHex);

        // The 8-byte header is uncompressed — pin it byte-for-byte against the historical blob.
        Assert.Equal(Convert.ToHexString(golden.AsSpan(0, 8)), Convert.ToHexString(encoded.AsSpan(0, 8)));

        // Pin the DECOMPRESSED channel payload, not the compressed bytes: zlib output varies by .NET
        // runtime patch, so the compressed form is a false pin that fails on any runtime other than the
        // one that minted it (TrackBlobWireCompatTests has the same rationale for the dense fixture).
        Assert.Equal(Convert.ToHexString(DecompressPayload(golden)), Convert.ToHexString(DecompressPayload(encoded)));
    }

    private static byte[] DecompressPayload(byte[] blob)
    {
        using var compressed = new MemoryStream(blob, 8, blob.Length - 8);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var decompressed = new MemoryStream();
        zlib.CopyTo(decompressed);
        return decompressed.ToArray();
    }

    [Fact]
    public void Golden_Decode_BitStable()
    {
        var t = TrackBlobCodec.Decode(Convert.FromHexString(GoldenHex));
        Assert.Equal(6, t.Count);

        AssertChannel(t.Lat, new long[]
        {
            4631828854149562203, 4631828854290299691, -4591560557451838512, 0, -4587338433012284904, 4631828854149562203,
        });
        AssertChannel(t.Lng, new long[]
        {
            -4585040589381171839, -4585040589310803095, 4638285403796913640, 0, 4640537203505045772, -4585040589381171839,
        });
        AssertChannel(t.TimeEpochS, new long?[]
        {
            4744994521446612992, 4744994521450807296, null, 4744994521459195904, 4744994521463390208, 4744994521467584512,
        });
        AssertChannel(t.Altitude!, new long?[]
        {
            4636737291354636288, null, 4637089135075524608, 4637455052545248461, 4636420632005836800, 4636737291354636288,
        });
        AssertChannel(t.Distance!, new long[]
        {
            0, 4617315517961601024, 4613937818241073152, 4621819117588971520, 4620974692658839552, 4626322717216342016,
        });
        AssertChannel(t.Speed!, new long[]
        {
            4612811918334230528, 4613937818241073152, 4613487458278336103, 0, 4616471093031469056, 4607182418800017408,
        });
        AssertChannel(t.HeartRate!, new long[]
        {
            4638144666238189568, 4638496509959077888, 4638003928749834240, 4638777984935788544, 4639129828656676864, 4637792822517301248,
        });
        AssertChannel(t.GradeSmooth!, new long[]
        {
            4607182418800017408, 4609434218613702656, 4602678819172646912, -4615288898129284300, 4611686018427387904, 0,
        });

        // Whole-null optional channel is the default (null-backed) TrackChannel post repr-swap (DR-13 spec).
        Assert.True(t.Cadence.IsNull);
        Assert.True(t.Power.IsNull);
    }

    [Fact]
    public void Quantize_Idempotent_OnDequantizedValues()
    {
        long[] qs = { 0, 1, -1, 7, -7, 244_784, -244_784, 2_700_000, -2_700_000, 51_400_000, -51_400_000, 120_000_000, -120_000_000 };
        foreach (double scale in TrackBlobCodec.Scales)
        foreach (long q in qs)
        {
            double v = q * scale;
            double roundTripped = TrackBlobCodec.RoundTripValue(v, scale);
            Assert.Equal(BitConverter.DoubleToInt64Bits(v), BitConverter.DoubleToInt64Bits(roundTripped));
        }
    }

    // DR-13a: channels are int32-backed TrackChannels post repr-swap — param type flipped, channel
    // .Length asserts dropped (length pinned by Assert.Equal(6, t.Count) + the fixed literal arrays).
    private static void AssertChannel(TrackChannel actual, long[] expectedBits)
    {
        Assert.Equal(6, expectedBits.Length);
        for (int i = 0; i < expectedBits.Length; i++)
            Assert.Equal(expectedBits[i], BitConverter.DoubleToInt64Bits(actual[i]));
    }

    private static void AssertChannel(TrackChannel actual, long?[] expectedBitsOrNaN)
    {
        Assert.Equal(6, expectedBitsOrNaN.Length);
        for (int i = 0; i < expectedBitsOrNaN.Length; i++)
        {
            if (expectedBitsOrNaN[i] is null) Assert.True(double.IsNaN(actual[i]));
            else Assert.Equal(expectedBitsOrNaN[i]!.Value, BitConverter.DoubleToInt64Bits(actual[i]));
        }
    }
}
