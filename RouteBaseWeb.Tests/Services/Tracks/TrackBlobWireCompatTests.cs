using System.Buffers.Binary;
using System.IO.Compression;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Tracks;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Tracks;

/// <summary>
/// Wire-format compatibility pins the DR-13 golden anchor doesn't cover (data-model audit 2026-07-17,
/// BLOB-01/BLOB-04). The DR-13 fixture's mask is 0x00FF, so cadence (bit 8), power (bit 9) and pressure
/// (bit 10) have no byte-stability pin there — a scale or channel-order change above bit 7 would pass every
/// round-trip test while misreading real blobs. Dense pin below freezes all 11 channels. The forward-compat
/// test pins the additive-channel guarantee itself: a blob carrying an unknown future presence bit (≥ 11)
/// with trailing payload must decode its known channels, not throw — this is what lets a new sensor channel
/// ship without a version bump.
/// </summary>
public class TrackBlobWireCompatTests
{
    private const string DenseGoldenHex =
        "0104000000FF0700789C6368E87CF38C87898989A169F94B1D10DD786A4D092310705FE067626160C8CBCB63F8C26C64"
        + "B49485E103231717178388B68411C32A30F30BB3483BCB1C1646EE59EB790C0D01F2861239";

    // The DECOMPRESSED channel payload (8-byte header + zlib framing stripped): the exact per-channel wire
    // bytes for all 11 channels. This is the real byte-stability contract and is deterministic across
    // machines. The compressed blob is NOT: zlib-ng output varies by .NET runtime patch, so hashing the
    // compressed form is a false pin that fails on any runtime other than the one that minted it.
    private const string DenseGoldenPayloadHex =
        "008089ECE60C0202020082A7E92C0202020081CAAC74010101010BD00F020400006E6E6E00F4033232A50400F0010A0A0A"
        + "00142B183200AA010A0A0A00F4031487049C04010B9AAF0C3131";

    private static List<GpsData> DenseFixture()
    {
        var t0 = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        return new List<GpsData>
        {
            new() { DateTimeUtc = t0,               GpsLat = 47.000001, GpsLong = -122.000001, Altitude = 100.0, Distance = 0.0,  Speed = 2.50, HeartRate = 120, GradeSmooth = 1.0,  Cadence = 85,  Power = 250,  Pressure = 1013.25 },
            new() { DateTimeUtc = t0.AddSeconds(1),  GpsLat = 47.000002, GpsLong = -122.000002, Altitude = 100.1, Distance = 5.5,  Speed = 2.75, HeartRate = 125, GradeSmooth = -1.2, Cadence = 90,  Power = 260,  Pressure = 1013.0 },
            new() { DateTimeUtc = t0.AddSeconds(2),  GpsLat = 47.000003, GpsLong = -122.000003, Altitude = null,  Distance = 11.0, Speed = 3.00, HeartRate = 130, GradeSmooth = 0.0,  Cadence = 95,  Power = 0,    Pressure = null },
            new() { DateTimeUtc = t0.AddSeconds(3),  GpsLat = 47.000004, GpsLong = -122.000004, Altitude = 100.3, Distance = 16.5, Speed = 0.25, HeartRate = 135, GradeSmooth = 2.5,  Cadence = 100, Power = 270,  Pressure = 1012.75 },
        };
    }

    [Fact]
    public void DenseGolden_Encode_ByteStable_AllElevenChannels()
    {
        byte[] blob = TrackBlobCodec.Encode(DecodedTrackExtensions.FromGpsDataList(DenseFixture()));

        // Guard the pin's own coverage: all 11 presence bits must be set or the fixture stopped being dense.
        Assert.Equal(0x07FF, BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(5, 2)));

        // Pin the pre-compression channel bytes, not the compressed blob (zlib output is runtime-dependent).
        byte[] payload;
        using (var compressed = new MemoryStream(blob, 8, blob.Length - 8))
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
        using (var decompressed = new MemoryStream())
        {
            zlib.CopyTo(decompressed);
            payload = decompressed.ToArray();
        }
        Assert.Equal(DenseGoldenPayloadHex, Convert.ToHexString(payload));
    }

    [Fact]
    public void DenseGolden_Decode_MapsEveryChannelToItsField()
    {
        var t = TrackBlobCodec.Decode(Convert.FromHexString(DenseGoldenHex));
        AssertMatchesFixture(t);
    }

    [Fact]
    public void Decode_UnknownFutureChannelBit_SkipsIt_KnownChannelsIntact()
    {
        var original = DecodedTrackExtensions.FromGpsDataList(DenseFixture());
        int n = original.Count;
        byte[] blob = TrackBlobCodec.Encode(original);

        byte[] payload;
        using (var compressed = new MemoryStream(blob, 8, blob.Length - 8))
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
        using (var decompressed = new MemoryStream())
        {
            zlib.CopyTo(decompressed);
            payload = decompressed.ToArray();
        }

        // Append a hypothetical channel 11: dense nullability + one single-byte varint per point.
        var future = new MemoryStream();
        future.Write(payload);
        future.WriteByte(0);                                  // nullability: dense
        future.WriteByte(0x0A);                               // zigzag(5 - 0)
        for (int j = 1; j < n; j++) future.WriteByte(0x02);   // zigzag(+1)

        using var outStream = new MemoryStream();
        Span<byte> header = stackalloc byte[8];
        blob.AsSpan(0, 8).CopyTo(header);
        ushort mask = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(5, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(5, 2), (ushort)(mask | (1 << 11)));
        outStream.Write(header);
        using (var zlib = new ZLibStream(outStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] futurePayload = future.ToArray();
            zlib.Write(futurePayload, 0, futurePayload.Length);
        }

        var t = TrackBlobCodec.Decode(outStream.ToArray());
        AssertMatchesFixture(t);
    }

    /// <summary>Asserts each decoded channel bit-equals the fixture put through the codec's own
    /// quantization oracle — a decode that assigns any channel's payload to the wrong field fails here.</summary>
    private static void AssertMatchesFixture(DecodedTrack t)
    {
        var fixture = DenseFixture();
        Assert.Equal(fixture.Count, t.Count);
        for (int i = 0; i < fixture.Count; i++)
        {
            var p = fixture[i];
            AssertSample(p.GpsLat, TrackBlobCodec.LatScale, t.Lat[i]);
            AssertSample(p.GpsLong, TrackBlobCodec.LngScale, t.Lng[i]);
            AssertSample(p.Altitude, TrackBlobCodec.AltitudeScale, t.Altitude[i]);
            AssertSample(p.Distance, TrackBlobCodec.DistanceScale, t.Distance[i]);
            AssertSample(p.Speed, TrackBlobCodec.SpeedScale, t.Speed[i]);
            AssertSample(p.HeartRate, TrackBlobCodec.HeartRateScale, t.HeartRate[i]);
            AssertSample(p.GradeSmooth, TrackBlobCodec.GradeSmoothScale, t.GradeSmooth[i]);
            AssertSample(p.Cadence, TrackBlobCodec.CadenceScale, t.Cadence[i]);
            AssertSample(p.Power, TrackBlobCodec.PowerScale, t.Power[i]);
            AssertSample(p.Pressure, TrackBlobCodec.PressureScale, t.Pressure[i]);
        }
    }

    private static void AssertSample(double? original, double scale, double decoded)
    {
        double expected = TrackBlobCodec.RoundTripValue(original ?? double.NaN, scale);
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(decoded));
    }
}
