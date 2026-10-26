using System.Buffers.Binary;
using System.IO.Compression;
using RouteBaseWeb.Services.Tracks;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Tracks;

/// <summary>
/// TE-29 equivalence tests for <see cref="TrackBlobCodec"/>. Encode→Decode must reproduce a
/// <see cref="DecodedTrack"/> identical to the input modulo one documented delta: values are
/// quantized to the per-channel scales. The oracle for each
/// sample is <see cref="TrackBlobCodec.RoundTripValue"/> (quantize→dequantize), asserted bit-exact.
/// </summary>
public class TrackBlobCodecTests
{
    private const int TrackId = 42;

    // ---- round-trip equivalence across the spec'd edge cases ----

    [Fact]
    public void RoundTrip_AllChannelsDense_QuantizedEquivalence()
    {
        var t = Build(3,
            time: new[] { 1_700_000_000.0, 1_700_000_010.0, 1_700_000_020.0 },
            lat: new[] { 47.123456, 47.1234565, 47.223456 },   // mid-quantum on point 1 → away-from-zero
            lng: new[] { 8.7654321, 8.7654329, 8.865432 },
            altitude: new[] { 100.04, 100.06, 105.15 },
            distance: new[] { 0.0, 12.34, 98.76 },
            speed: new[] { 3.005, 3.014, 4.999 },
            heartRate: new[] { 120.0, 121.0, 140.0 },
            gradeSmooth: new[] { -1.24, 0.06, 2.55 },
            cadence: new[] { 80.0, 81.0, 90.0 },
            power: new[] { 200.0, 210.0, 250.0 });

        AssertRoundTrip(t);
    }

    [Fact]
    public void RoundTrip_NaNHoles_PreservedPerChannel()
    {
        var t = Build(4,
            time: new[] { 10.0, double.NaN, 30.0, 40.0 },       // hole in a required channel
            lat: new[] { 1.0, 1.1, double.NaN, 1.3 },
            lng: new[] { 2.0, 2.1, 2.2, 2.3 },
            altitude: new[] { double.NaN, 11.0, 12.0, double.NaN },
            heartRate: new[] { 100.0, double.NaN, double.NaN, 110.0 });

        AssertRoundTrip(t);
    }

    [Fact]
    public void RoundTrip_WholeNullOptionalChannels_StayNull()
    {
        // Optional channels omitted → null arrays; presence bits clear; decode returns null arrays.
        var t = Build(3,
            time: new[] { 1.0, 2.0, 3.0 },
            lat: new[] { 1.0, 1.1, 1.2 },
            lng: new[] { 2.0, 2.1, 2.2 });

        var t2 = RoundTrip(t);
        Assert.True(t2.Altitude.IsNull);
        Assert.True(t2.Distance.IsNull);
        Assert.True(t2.Speed.IsNull);
        Assert.True(t2.HeartRate.IsNull);
        Assert.True(t2.GradeSmooth.IsNull);
        Assert.True(t2.Cadence.IsNull);
        Assert.True(t2.Power.IsNull);
        AssertRoundTrip(t);
    }

    [Fact]
    public void RoundTrip_NonMonotonicTime_AndNegativeDeltas()
    {
        // Time goes backwards mid-track; distance dips — exercises negative zigzag deltas both ways.
        var t = Build(5,
            time: new[] { 100.0, 130.0, 90.0, 200.0, 150.0 },
            lat: new[] { -1.0, -1.5, -0.5, -2.0, -1.2 },
            lng: new[] { -2.0, -1.0, -3.0, -0.5, -2.5 },
            distance: new[] { 500.0, 400.0, 600.0, 100.0, 550.0 });

        AssertRoundTrip(t);
    }

    [Fact]
    public void RoundTrip_SinglePoint()
    {
        var t = Build(1,
            time: new[] { 1_700_000_000.0 },
            lat: new[] { 47.5 },
            lng: new[] { 8.5 },
            altitude: new[] { 321.7 });

        AssertRoundTrip(t);
    }

    [Fact]
    public void RoundTrip_EmptyTrack_ZeroPoints()
    {
        var t = Build(0,
            time: Array.Empty<double>(),
            lat: Array.Empty<double>(),
            lng: Array.Empty<double>());

        var t2 = RoundTrip(t);
        Assert.Equal(0, t2.Count);
        AssertRoundTrip(t);
    }

    [Fact]
    public void RoundTrip_AllNanRequiredChannel_PresentButAllHoles()
    {
        // A present (non-null) but entirely-NaN required channel must survive as all-NaN, not collapse.
        var t = Build(3,
            time: new[] { double.NaN, double.NaN, double.NaN },
            lat: new[] { 1.0, 1.1, 1.2 },
            lng: new[] { 2.0, 2.1, 2.2 });

        var t2 = RoundTrip(t);
        for (int i = 0; i < t2.Count; i++) Assert.True(double.IsNaN(t2.TimeEpochS[i]));
        AssertRoundTrip(t);
    }

    [Fact]
    public void RoundTrip_Synthetic44k_AllChannels()
    {
        const int n = 44_000;
        var time = new double[n];
        var lat = new double[n];
        var lng = new double[n];
        var altitude = new double[n];
        var distance = new double[n];
        var speed = new double[n];
        var heartRate = new double[n];
        var gradeSmooth = new double[n];
        var cadence = new double[n];
        var power = new double[n];
        double dist = 0;
        for (int i = 0; i < n; i++)
        {
            time[i] = 1_700_000_000.0 + i;
            lat[i] = 47.0 + i * 1e-5 + Math.Sin(i / 50.0) * 1e-4;
            lng[i] = 8.0 + i * 1e-5 + Math.Cos(i / 50.0) * 1e-4;
            altitude[i] = 300 + Math.Sin(i / 100.0) * 40;
            dist += 2.5 + Math.Abs(Math.Sin(i / 30.0));
            distance[i] = dist;
            speed[i] = 2.5 + Math.Abs(Math.Sin(i / 30.0));
            heartRate[i] = (i % 500 == 0) ? double.NaN : 130 + (i % 40);   // periodic holes
            gradeSmooth[i] = Math.Sin(i / 20.0) * 12;
            cadence[i] = 80 + (i % 15);
            power[i] = 200 + (i % 120);
        }

        var t = Build(n, time, lat, lng, altitude, distance, speed, heartRate, gradeSmooth, cadence, power);
        AssertRoundTrip(t);
    }

    [Fact]
    public void Header_RoundTrips_MaskAndCount()
    {
        var t = Build(3,
            time: new[] { 1.0, 2.0, 3.0 },
            lat: new[] { 1.0, 1.1, 1.2 },
            lng: new[] { 2.0, 2.1, 2.2 },
            speed: new[] { 5.0, 5.1, 5.2 });   // one optional present → distinctive mask

        var blob = TrackBlobCodec.Encode(t);
        Assert.Equal(TrackBlobCodec.Version, blob[0]);
        int n = blob[1] | (blob[2] << 8) | (blob[3] << 16) | (blob[4] << 24);
        int mask = blob[5] | (blob[6] << 8);
        Assert.Equal(3, n);
        // bits 0 (time) 1 (lat) 2 (lng) 5 (speed) set; others clear.
        Assert.Equal(0b0000_0000_0010_0111, mask);
        Assert.Equal(0, blob[7]); // flags reserved
    }

    [Fact]
    public void Decode_UnknownVersion_Throws()
    {
        var t = Build(2,
            time: new[] { 1.0, 2.0 }, lat: new[] { 1.0, 1.1 }, lng: new[] { 2.0, 2.1 });
        var blob = TrackBlobCodec.Encode(t);
        blob[0] = 2; // corrupt the version byte

        Assert.Throws<InvalidDataException>(() => TrackBlobCodec.Decode(blob));
    }

    // dm-1: byte 7 is the reserved flags byte (0 in v1). A non-zero value = a future flags-based semantic
    // this decoder can't honour → fail fast like the version check above, never a silent misread.
    [Fact]
    public void Decode_NonZeroReservedFlagsByte_Throws()
    {
        var t = Build(2,
            time: new[] { 1.0, 2.0 }, lat: new[] { 1.0, 1.1 }, lng: new[] { 2.0, 2.1 });
        var blob = TrackBlobCodec.Encode(t);
        blob[7] = 1; // set the reserved flags byte

        Assert.Throws<InvalidDataException>(() => TrackBlobCodec.Decode(blob));
    }

    // TE-01: corrupt blobs must fail as InvalidDataException (the decode contract), never as raw
    // Overflow/IndexOutOfRange — and never attempt a wrapped-negative array allocation.
    [Fact]
    public void Decode_CountOverflowHeader_ThrowsInvalidData()
    {
        var t = Build(2,
            time: new[] { 1.0, 2.0 }, lat: new[] { 1.0, 1.1 }, lng: new[] { 2.0, 2.1 });
        var blob = TrackBlobCodec.Encode(t);
        blob[1] = blob[2] = blob[3] = blob[4] = 0xFF; // point count = uint.MaxValue

        Assert.Throws<InvalidDataException>(() => TrackBlobCodec.Decode(blob));
    }

    [Fact]
    public void Decode_TruncatedPayload_ThrowsInvalidData()
    {
        var t = Build(3,
            time: new[] { 1.0, 2.0, 3.0 }, lat: new[] { 1.0, 1.1, 1.2 }, lng: new[] { 2.0, 2.1, 2.2 },
            altitude: new[] { 10.0, 11.0, 12.0 });
        var blob = TrackBlobCodec.Encode(t);
        var truncated = blob.AsSpan(0, 10).ToArray(); // header + 2 bytes of compressed payload

        Assert.Throws<InvalidDataException>(() => TrackBlobCodec.Decode(truncated));
    }

    // TE-02: a (corrupt-only) mask missing a required channel must read as NaN, not 0.0 — a zeroed
    // lat/lng channel would feed Null-Island geometry downstream.
    [Fact]
    public void Decode_MaskMissingRequiredChannel_ReadsNaN()
    {
        var t = Build(1, time: new[] { 100.0 }, lat: new[] { 47.5 }, lng: new[] { 8.5 });
        var blob = TrackBlobCodec.Encode(t);
        blob[5] &= unchecked((byte)~0b10); // clear presence bit 1 (lat); trailing payload is ignored by design

        var decoded = TrackBlobCodec.Decode(blob);
        Assert.True(double.IsNaN(decoded.Lat[0]));
    }

    // ---- helpers ----

    private static DecodedTrack RoundTrip(DecodedTrack t) =>
        TrackBlobCodec.Decode(TrackBlobCodec.Encode(t));

    private static void AssertRoundTrip(DecodedTrack t)
    {
        var t2 = RoundTrip(t);

        Assert.Equal(t.Count, t2.Count);

        AssertChannel(t.TimeEpochS, t2.TimeEpochS, TrackBlobCodec.TimeScale, t.Count);
        AssertChannel(t.Lat, t2.Lat, TrackBlobCodec.LatScale, t.Count);
        AssertChannel(t.Lng, t2.Lng, TrackBlobCodec.LngScale, t.Count);
        AssertChannel(t.Altitude, t2.Altitude, TrackBlobCodec.AltitudeScale, t.Count);
        AssertChannel(t.Distance, t2.Distance, TrackBlobCodec.DistanceScale, t.Count);
        AssertChannel(t.Speed, t2.Speed, TrackBlobCodec.SpeedScale, t.Count);
        AssertChannel(t.HeartRate, t2.HeartRate, TrackBlobCodec.HeartRateScale, t.Count);
        AssertChannel(t.GradeSmooth, t2.GradeSmooth, TrackBlobCodec.GradeSmoothScale, t.Count);
        AssertChannel(t.Cadence, t2.Cadence, TrackBlobCodec.CadenceScale, t.Count);
        AssertChannel(t.Power, t2.Power, TrackBlobCodec.PowerScale, t.Count);
        AssertChannel(t.Pressure, t2.Pressure, TrackBlobCodec.PressureScale, t.Count);
    }

    private static void AssertChannel(TrackChannel original, TrackChannel decoded, double scale, int n)
    {
        if (original.IsNull) { Assert.True(decoded.IsNull); return; }
        Assert.False(decoded.IsNull);
        for (int i = 0; i < n; i++)
        {
            double expected = TrackBlobCodec.RoundTripValue(original[i], scale);
            if (double.IsNaN(expected))
                Assert.True(double.IsNaN(decoded[i]), $"expected NaN at {i}, got {decoded[i]}");
            else
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected),
                             BitConverter.DoubleToInt64Bits(decoded[i]));
        }
    }

    // Live-recording barometric pressure survives encode→decode (own channel; NaN holes preserved).
    [Fact]
    public void RoundTrip_Pressure_Preserved()
    {
        var t = Build(4,
            time: new[] { 1_700_000_000.0, 1_700_000_001.0, 1_700_000_002.0, 1_700_000_003.0 },
            lat: new[] { 47.10, 47.11, 47.12, 47.13 },
            lng: new[] { 8.70, 8.71, 8.72, 8.73 },
            pressure: new[] { 1013.25, 1013.12, double.NaN, 1012.98 });

        var t2 = TrackBlobCodec.Decode(TrackBlobCodec.Encode(t));
        Assert.False(t2.Pressure.IsNull);
        AssertChannel(t.Pressure, t2.Pressure, TrackBlobCodec.PressureScale, t.Count);
    }

    // A track with no pressure leaves the channel whole-NULL (no wasted bytes, back-compat with v1 blobs).
    [Fact]
    public void RoundTrip_NoPressure_ChannelWholeNull()
    {
        var t = Build(2,
            time: new[] { 1_700_000_000.0, 1_700_000_001.0 },
            lat: new[] { 47.10, 47.11 }, lng: new[] { 8.70, 8.71 });

        var t2 = TrackBlobCodec.Decode(TrackBlobCodec.Encode(t));
        Assert.True(t2.Pressure.IsNull);
    }

    // ==== mutation-testing additions ====

    // --- decode-guard boundaries + exact messages (L115/L117/L123/L126-127/L157/L254) ---

    // L115 Statement `;` (skip the length guard) + L115 String: a blob shorter than the 8-byte header must
    // fail as InvalidDataException *from the length guard* — not proceed into blob[7] (IndexOutOfRange, an
    // uncaught kind) nor throw a differently-worded error. version byte = 1 so a skipped guard reaches blob[7].
    [Fact]
    public void Decode_BlobShorterThanHeader_ThrowsInvalidData_NamingLength()
    {
        var ex = Assert.Throws<InvalidDataException>(() => TrackBlobCodec.Decode(new byte[] { 1, 0, 0, 0, 0 }));
        Assert.Contains("short", ex.Message, StringComparison.Ordinal);
    }

    // L115 Equality (`< 8` → `<= 8`): a header-only 8-byte blob (n=0, mask=0, empty payload section) is a
    // valid, decodable blob — the `<= 8` mutant rejects it. Pins that length 8 is accepted, not rejected.
    [Fact]
    public void Decode_HeaderOnlyEightByteBlob_DecodesEmptyTrack()
    {
        var blob = new byte[] { TrackBlobCodec.Version, 0, 0, 0, 0, 0, 0, 0 };
        var t = TrackBlobCodec.Decode(blob);
        Assert.Equal(0, t.Count);
    }

    // L117 String: the unknown-version error must actually name the version (empty message = a silent misread).
    [Fact]
    public void Decode_UnknownVersion_MessageNamesVersion()
    {
        var blob = TrackBlobCodec.Encode(Build(1, new[] { 1.0 }, new[] { 1.0 }, new[] { 2.0 }));
        blob[0] = 2;
        var ex = Assert.Throws<InvalidDataException>(() => TrackBlobCodec.Decode(blob));
        Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // L123 String: the reserved-flags-byte error must carry a message.
    [Fact]
    public void Decode_NonZeroReservedFlags_MessageNamesFlags()
    {
        var blob = TrackBlobCodec.Encode(Build(1, new[] { 1.0 }, new[] { 1.0 }, new[] { 2.0 }));
        blob[7] = 1;
        var ex = Assert.Throws<InvalidDataException>(() => TrackBlobCodec.Decode(blob));
        Assert.Contains("flags", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // L127 Statement `;` + String: an over-range point count must fail *from the count guard* (message names
    // the count), not fall through to a negative-length allocation whose OverflowException the catch relabels
    // "Corrupt track blob payload." — a different, misleading message.
    [Fact]
    public void Decode_CountOverflow_MessageNamesPointCount()
    {
        var blob = TrackBlobCodec.Encode(Build(1, new[] { 1.0 }, new[] { 1.0 }, new[] { 2.0 }));
        blob[1] = blob[2] = blob[3] = blob[4] = 0xFF;   // point count = uint.MaxValue
        var ex = Assert.Throws<InvalidDataException>(() => TrackBlobCodec.Decode(blob));
        Assert.Contains("point count", ex.Message, StringComparison.Ordinal);
    }

    // L157 String: the corrupt-payload wrapper must carry its message (and the original cause as inner).
    [Fact]
    public void Decode_TruncatedPayload_MessageSaysCorrupt_WithInner()
    {
        var blob = TrackBlobCodec.Encode(Build(3,
            new[] { 1.0, 2.0, 3.0 }, new[] { 1.0, 1.1, 1.2 }, new[] { 2.0, 2.1, 2.2 },
            altitude: new[] { 10.0, 11.0, 12.0 }));
        var truncated = blob.AsSpan(0, 10).ToArray();
        var ex = Assert.Throws<InvalidDataException>(() => TrackBlobCodec.Decode(truncated));
        Assert.Contains("Corrupt", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
    }

    // --- per-point int32 guard on DECODE (L248 in TrackBlobCodec.Guarded) ---
    // A hand-built single-channel blob lets a decoded quantized value land exactly on the int32 edges.

    // L248 Logical (`||`→`&&`) + Statement + L254 String: a decoded value ABOVE int.MaxValue must throw,
    // naming the int32 range — the mutants either never throw (silently truncating) or drop the message.
    [Fact]
    public void Decode_ChannelValueAboveInt32Max_ThrowsNamingRange()
    {
        var blob = MakeBlob(1, LatBit, SingleDenseChannel(2147483648L));   // int.MaxValue + 1
        var ex = Assert.Throws<InvalidDataException>(() => TrackBlobCodec.Decode(blob));
        Assert.Contains("int32", ex.Message, StringComparison.Ordinal);
    }

    // L248 Equality (`x <= int.MinValue` → `x < int.MinValue`): a decoded value of exactly int.MinValue is the
    // reserved NullQ sentinel and MUST be rejected — the `<` mutant lets it through and stores it as a NULL.
    [Fact]
    public void Decode_ChannelValueEqualsInt32Min_Throws()
    {
        var blob = MakeBlob(1, LatBit, SingleDenseChannel(int.MinValue));
        Assert.Throws<InvalidDataException>(() => TrackBlobCodec.Decode(blob));
    }

    // L248 Equality (`x > int.MaxValue` → `x >= int.MaxValue`): exactly int.MaxValue is a LEGAL quantized
    // value — it must decode, not throw. Pins the inclusive upper edge.
    [Fact]
    public void Decode_ChannelValueEqualsInt32Max_DecodesToThatValue()
    {
        var blob = MakeBlob(1, LatBit, SingleDenseChannel(int.MaxValue));
        var t = TrackBlobCodec.Decode(blob);
        Assert.Equal(int.MaxValue * TrackBlobCodec.LatScale, t.Lat[0]);
    }

    // --- time rebase on DECODE (L229 haveBias seed, L236 `!haveBias`) ---
    // For an epoch beyond int.MaxValue seconds (year ~2096) the decoder MUST fold the first present quantized
    // epoch into QBias so the stored int32 offsets stay small. A mutant that never captures the bias tries to
    // store the absolute ~4e9 epoch in an int32 and throws. Pre-2038 epochs (< int.MaxValue) hide this.
    [Fact]
    public void RoundTrip_FarFutureEpoch_BeyondInt32Seconds()
    {
        var t = Build(2,
            time: new[] { 4_000_000_000.0, 4_000_000_001.0 },
            lat: new[] { 47.0, 47.1 }, lng: new[] { 8.0, 8.1 });
        AssertRoundTrip(t);
    }

    // --- varint continuation boundary (L273 `value >= 0x80` → `value > 0x80`) ---
    // zigzag(64) == 128 == 0x80: the ONE value where `>=` and `>` differ. The `>` mutant emits a bare 0x80
    // (continuation bit set, no follow byte) and the round-trip corrupts. A first sample of exactly 64 forces it.
    [Fact]
    public void RoundTrip_VarintValueOnContinuationBoundary_Preserved()
    {
        var t = Build(2,
            time: new[] { 1.0, 2.0 }, lat: new[] { 1.0, 1.1 }, lng: new[] { 2.0, 2.1 },
            heartRate: new[] { 64.0, 65.0 });   // q0 = 64 → zigzag = 128 = 0x80
        AssertRoundTrip(t);
    }

    // ---- hand-built-blob helpers (control the exact decoded bytes; the codec's own encoder guards, so an
    // out-of-range value can only be produced by writing the payload directly) ----

    private const ushort LatBit = 0b010;   // presence bit 1 = latitude

    private static byte[] MakeBlob(int n, ushort mask, byte[] payload)
    {
        using var outStream = new MemoryStream();
        Span<byte> header = stackalloc byte[8];
        header[0] = TrackBlobCodec.Version;
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(1, 4), (uint)n);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(5, 2), mask);
        header[7] = 0;
        outStream.Write(header);
        using (var zlib = new ZLibStream(outStream, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(payload, 0, payload.Length);
        return outStream.ToArray();
    }

    // One dense (no-bitmap) channel of a single point whose absolute quantized value is q.
    private static byte[] SingleDenseChannel(long q)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0);                    // nullability: dense
        ms.Write(Varint(q));                // first delta vs 0 → the absolute value
        return ms.ToArray();
    }

    // LEB128 zigzag varint, byte-identical to the codec's WriteVarint(ZigZag(v)).
    private static byte[] Varint(long signed)
    {
        ulong v = (ulong)((signed << 1) ^ (signed >> 63));
        using var ms = new MemoryStream();
        while (v >= 0x80) { ms.WriteByte((byte)(v | 0x80)); v >>= 7; }
        ms.WriteByte((byte)v);
        return ms.ToArray();
    }

    private static DecodedTrack Build(
        int n, double[] time, double[] lat, double[] lng,
        double[]? altitude = null, double[]? distance = null, double[]? speed = null,
        double[]? heartRate = null, double[]? gradeSmooth = null, double[]? cadence = null,
        double[]? power = null, double[]? pressure = null) =>
        DecodedTrackExtensions.FromDoubleChannels(
            time, lat, lng, altitude, distance, speed, heartRate, gradeSmooth, cadence, power, pressure);
}
