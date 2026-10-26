using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace RouteBaseWeb.Services.Tracks;

/// <summary>
/// Pure, I/O-free codec for the packed per-track GPS blob (format v1). Encodes a <see cref="DecodedTrack"/>'s
/// channels into one compressed blob and decodes it back; no EF/SQLite usings by contract (grep-gate).
///
/// Format v1 (one packed blob per track):
/// <code>
///   byte 0        version = 1
///   bytes 1..4    point count n           (uint32 LE)
///   bytes 5..6    channel presence mask   (uint16 LE; bit i set = channel i is present)
///   byte 7        flags = 0               (reserved)
///   bytes 8..     ZLibStream(payload)
/// </code>
/// Payload = per present channel, in fixed channel order:
/// <code>
///   1 byte              nullability: 0 = dense (no NaN), 1 = present-bitmap follows
///   [ceil(n/8) bytes]   present-bitmap (point i → byte i>>3, bit i&amp;7; set = value present)
///   varints             zigzag(delta) of quantized int64 over PRESENT points only; first delta vs 0
/// </code>
/// Quantize <c>q = round(v / scale, away-from-zero)</c>; decode <c>v' = q * scale</c>; NaN ⇔ bitmap 0.
/// Varints are byte-oriented LEB128 (endian-free); header ints are little-endian.
///
/// Decode produces a <see cref="DecodedTrack"/> of int32-backed <see cref="TrackChannel"/>s: each sample is
/// stored as its quantized value (dequantized on read), a per-point NULL as <see cref="TrackChannel.NullQ"/>.
/// The time channel is rebased — its first present quantized epoch is folded into <see cref="TrackChannel.QBias"/>
/// so the int32 offsets stay small; encode adds QBias back, reproducing the same int64 <c>q</c> the v1 decode
/// carried, hence byte-identical blobs. Values are quantized to the scales and GPS_IDs are the ordinal ramp
/// (not stored) — the only two deltas vs the v1 rows decode.
/// </summary>
public static class TrackBlobCodec
{
    public const byte Version = 1;

    // Fixed channel order = presence bit index. A new sensor channel is purely additive: it takes the next
    // presence bit, so a blob that lacks it (every pre-existing track) encodes byte-identically and decodes
    // with the new channel whole-NULL — no version bump needed (the golden byte-stable pin depends on this).
    public const int ChannelCount = 11;

    // Quantization scales per channel (ID-01 format). Index = channel/presence-bit.
    public const double TimeScale     = 1.0;      // 0 time (epoch seconds)
    public const double LatScale      = 1e-6;     // 1 latitude
    public const double LngScale      = 1e-6;     // 2 longitude
    public const double AltitudeScale = 0.1;      // 3 altitude
    public const double DistanceScale = 0.1;      // 4 distance
    public const double SpeedScale    = 0.01;     // 5 speed
    public const double HeartRateScale   = 1.0;   // 6 heart_rate
    public const double GradeSmoothScale = 0.1;   // 7 grade_smooth
    public const double CadenceScale     = 1.0;   // 8 cadence
    public const double PowerScale       = 1.0;   // 9 power
    public const double PressureScale    = 0.01;  // 10 barometric pressure (hPa) — raw sensor signal for the
                                                  // offline elevation-fusion work; 0.01 hPa ≈ 0.08 m, below
                                                  // the barometer's own noise, so quantization is lossless.

    /// <summary>Per-channel scale, indexed by presence-bit. Public so tests derive expected values from
    /// the same constants the codec uses.</summary>
    public static readonly double[] Scales =
    {
        TimeScale, LatScale, LngScale, AltitudeScale, DistanceScale,
        SpeedScale, HeartRateScale, GradeSmoothScale, CadenceScale, PowerScale, PressureScale,
    };

    /// <summary>The value a channel sample survives an encode/decode round-trip as: quantize then
    /// dequantize. NaN in ⇒ NaN out. The equivalence oracle for the round-trip tests.</summary>
    public static double RoundTripValue(double v, double scale) =>
        double.IsNaN(v) ? double.NaN : Quantize(v, scale) * scale;

    internal static long Quantize(double v, double scale) =>
        (long)Math.Round(v / scale, MidpointRounding.AwayFromZero);

    /// <summary>Pack a track's channels into a v1 blob.</summary>
    public static byte[] Encode(DecodedTrack t)
    {
        int n = t.Count;
        var channels = Channels(t);

        ushort mask = 0;
        for (int i = 0; i < ChannelCount; i++)
            if (!channels[i].IsNull) mask |= (ushort)(1 << i);

        byte[] payload;
        using (var payloadStream = new MemoryStream())
        {
            for (int i = 0; i < ChannelCount; i++)
            {
                if (channels[i].IsNull) continue;
                EncodeChannel(payloadStream, channels[i], n);
            }
            payload = payloadStream.ToArray();
        }

        using var outStream = new MemoryStream();
        Span<byte> header = stackalloc byte[8];
        header[0] = Version;
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(1, 4), (uint)n);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(5, 2), mask);
        header[7] = 0; // flags reserved
        outStream.Write(header);

        using (var zlib = new ZLibStream(outStream, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(payload, 0, payload.Length);

        return outStream.ToArray();
    }

    /// <summary>Decode a v1 blob. Throws on an unknown version byte. Takes no track id: a blob is content,
    /// and the same bytes can be the content of several activities (see <see cref="DecodedTrack"/>).</summary>
    public static DecodedTrack Decode(byte[] blob)
    {
        if (blob.Length < 8) throw new InvalidDataException($"Track blob too short: {blob.Length} bytes.");
        byte version = blob[0];
        if (version != Version) throw new InvalidDataException($"Unsupported track blob version {version}.");

        // dm-1: byte 7 is the reserved flags byte, written 0 in v1. A non-zero value means a future
        // flags-based semantic this decoder can't honour — fail fast like the version check above rather
        // than silently misreading. No existing blob is affected (Encode always writes 0).
        if (blob[7] != 0)
            throw new InvalidDataException($"Track blob: unsupported reserved flags byte 0x{blob[7]:X2}.");

        uint rawN = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(1, 4));
        if (rawN > int.MaxValue)
            throw new InvalidDataException($"Track blob: invalid point count {rawN}.");
        int n = (int)rawN;
        ushort mask = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(5, 2));

        // Corrupt-payload guard (TE-01): a truncated/garbled blob must surface as InvalidDataException
        // (the decode contract), not raw IndexOutOfRange/Overflow. Zero cost when nothing throws.
        var channels = new int[ChannelCount][];
        long timeBias = 0;
        try
        {
            byte[] payload;
            using (var compressed = new MemoryStream(blob, 8, blob.Length - 8))
            using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
            // Capacity hint (~typical inflate ratio, capped) skips most growth-doubling copies.
            using (var decompressed = new MemoryStream((int)Math.Min((long)blob.Length * 4, 64 << 20)))
            {
                zlib.CopyTo(decompressed);
                payload = decompressed.ToArray();
            }

            int pos = 0;
            for (int i = 0; i < ChannelCount; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                channels[i] = DecodeChannel(payload, ref pos, n, i, rebase: i == 0, out long bias);
                if (i == 0) timeBias = bias;
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("Corrupt track blob payload.", ex);
        }

        return new DecodedTrack
        {
            Count = n,
            Lat = new TrackChannel(channels[1] ?? EmptyRequired(n), LatScale),
            Lng = new TrackChannel(channels[2] ?? EmptyRequired(n), LngScale),
            TimeEpochS = new TrackChannel(channels[0] ?? EmptyRequired(n), TimeScale, timeBias),
            Altitude = new TrackChannel(channels[3], AltitudeScale),
            Distance = new TrackChannel(channels[4], DistanceScale),
            Speed = new TrackChannel(channels[5], SpeedScale),
            HeartRate = new TrackChannel(channels[6], HeartRateScale),
            GradeSmooth = new TrackChannel(channels[7], GradeSmoothScale),
            Cadence = new TrackChannel(channels[8], CadenceScale),
            Power = new TrackChannel(channels[9], PowerScale),
            Pressure = new TrackChannel(channels[10], PressureScale),
        };
    }

    // Encode-order channels; index = presence-bit. Required channels (time/lat/lng) are never null.
    private static TrackChannel[] Channels(DecodedTrack t) => new[]
    {
        t.TimeEpochS, t.Lat, t.Lng,
        t.Altitude, t.Distance, t.Speed, t.HeartRate, t.GradeSmooth, t.Cadence, t.Power, t.Pressure,
    };

    private static void EncodeChannel(Stream s, TrackChannel ch, int n)
    {
        var raw = ch.RawQ!;
        bool hasNaN = false;
        for (int j = 0; j < n; j++)
            if (raw[j] == TrackChannel.NullQ) { hasNaN = true; break; }

        s.WriteByte(hasNaN ? (byte)1 : (byte)0);
        if (hasNaN)
        {
            int bytes = (n + 7) / 8;
            Span<byte> bitmap = bytes <= 512 ? stackalloc byte[bytes] : new byte[bytes];
            bitmap.Clear();
            for (int j = 0; j < n; j++)
                if (raw[j] != TrackChannel.NullQ) bitmap[j >> 3] |= (byte)(1 << (j & 7));
            s.Write(bitmap);
        }

        long prev = 0;
        for (int j = 0; j < n; j++)
        {
            if (raw[j] == TrackChannel.NullQ) continue;
            long q = raw[j] + ch.QBias;
            WriteVarint(s, ZigZag(q - prev));
            prev = q;
        }
    }

    // Returns int32 quantized samples (NullQ for absent points) + the rebase bias (time only).
    private static int[] DecodeChannel(
        byte[] payload, ref int pos, int n, int channel, bool rebase, out long qBias)
    {
        qBias = 0;
        byte nullability = payload[pos++];
        byte[]? bitmap = null;
        if (nullability == 1)
        {
            int bytes = (n + 7) / 8;
            bitmap = new byte[bytes];
            Array.Copy(payload, pos, bitmap, 0, bytes);
            pos += bytes;
        }

        var arr = new int[n];
        long prev = 0;
        bool haveBias = false;
        for (int j = 0; j < n; j++)
        {
            bool present = bitmap == null || (bitmap[j >> 3] & (1 << (j & 7))) != 0;
            if (!present) { arr[j] = TrackChannel.NullQ; continue; }
            long q = prev + UnZigZag(ReadVarint(payload, ref pos));
            prev = q;
            if (rebase && !haveBias) { qBias = q; haveBias = true; }
            arr[j] = Guarded(q - qBias, channel);
        }
        return arr;
    }

    // Fold to int32; NullQ (int.MinValue) is reserved, so the low bound is exclusive.
    // Hot path (called per point per channel): the throw lives in a cold helper so the
    // guard stays small enough for the JIT to inline (the interpolated message otherwise blocks it).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Guarded(long x, int channel)
    {
        if (x <= int.MinValue || x > int.MaxValue) ThrowOutOfInt32Range(x, channel);
        return (int)x;
    }

    [DoesNotReturn]
    private static void ThrowOutOfInt32Range(long x, int channel) =>
        throw new InvalidDataException($"Track blob channel {channel}: quantized value {x} out of int32 range.");

    // Only for a corrupt mask (encode always sets bits 0/1/2). NullQ-filled so the missing channel reads
    // as NaN, not 0.0 — a zeroed required channel would feed Null-Island geometry downstream. TE-02.
    private static int[] EmptyRequired(int n)
    {
        var a = new int[n];
        Array.Fill(a, TrackChannel.NullQ);
        return a;
    }

    // ---- varint + zigzag (byte-oriented LEB128, endian-free) ----

    private static ulong ZigZag(long v) => (ulong)((v << 1) ^ (v >> 63));

    private static long UnZigZag(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);

    private static void WriteVarint(Stream s, ulong value)
    {
        while (value >= 0x80)
        {
            s.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        s.WriteByte((byte)value);
    }

    private static ulong ReadVarint(byte[] buf, ref int pos)
    {
        ulong value = 0;
        int shift = 0;
        while (true)
        {
            byte b = buf[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return value;
    }
}
