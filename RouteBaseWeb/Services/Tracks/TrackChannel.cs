namespace RouteBaseWeb.Services.Tracks;

/// <summary>One decoded channel: quantized int32 samples, dequantized on read.
/// value = (q + QBias) * Scale — bit-identical to the v1 double[] decode.
/// NullQ sentinel = per-point NULL (reads as double.NaN). Default instance = whole-NULL channel.</summary>
public readonly struct TrackChannel
{
    /// <summary>Reserved per-point NULL sentinel. Never a legal quantized value
    /// (|q| ≤ 1.2e8 live; builder+decoder guard collisions).</summary>
    public const int NullQ = int.MinValue;

    private readonly int[]? _q;
    private readonly double _scale;
    private readonly long _qBias;      // time: base quantized epoch; others: 0

    internal TrackChannel(int[]? q, double scale, long qBias = 0)
    { _q = q; _scale = scale; _qBias = qBias; }

    public bool IsNull => _q is null;

    public double this[int i]
    {
        get
        {
            int q = _q![i];
            return q == NullQ ? double.NaN : (q + _qBias) * _scale;
        }
    }

    internal int[]? RawQ => _q;
    internal long QBias => _qBias;
}
