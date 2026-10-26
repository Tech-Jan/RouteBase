namespace RouteBaseWeb.Services.Heatmaps;

/// <summary>
/// Red frequency colormap (deep red → bright red → orange → yellow → white-hot)
/// as a 256-entry RGBA LUT, piecewise-linearly interpolated from the node list.
/// Alpha ramps fast so a single pass is fully visible; the blur halo
/// (norm ≲ 0.03) stays near-transparent so strands read as crisp lines.
/// </summary>
public static class CountColorMap
{
    private static readonly (double Pos, double R, double G, double B, double A)[] Nodes =
    [
        (0.00, 0.00, 0.00, 0.00, 0.00),
        (0.05, 0.55, 0.05, 0.05, 0.60),
        (0.20, 0.90, 0.10, 0.05, 0.90),
        (0.50, 1.00, 0.45, 0.10, 0.95),
        (0.80, 1.00, 0.85, 0.30, 1.00),
        (1.00, 1.00, 1.00, 0.95, 1.00),
    ];

    /// <summary>256 RGBA quadruples; entry i maps norm = i / 255.</summary>
    private static readonly byte[] Lut = BuildLut();

    /// <summary>Map a normalized value [0, 1] to RGBA.</summary>
    public static (byte R, byte G, byte B, byte A) Lookup(double norm)
    {
        int i = (int)Math.Clamp(Math.Round(norm * 255.0), 0, 255) * 4;
        return (Lut[i], Lut[i + 1], Lut[i + 2], Lut[i + 3]);
    }

    private static byte[] BuildLut()
    {
        var lut = new byte[256 * 4];
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            int hi = 1;
            while (hi < Nodes.Length - 1 && Nodes[hi].Pos < t)
                hi++;
            var a = Nodes[hi - 1];
            var b = Nodes[hi];
            double f = (t - a.Pos) / (b.Pos - a.Pos);
            f = Math.Clamp(f, 0, 1);
            lut[i * 4 + 0] = ToByte(a.R + (b.R - a.R) * f);
            lut[i * 4 + 1] = ToByte(a.G + (b.G - a.G) * f);
            lut[i * 4 + 2] = ToByte(a.B + (b.B - a.B) * f);
            lut[i * 4 + 3] = ToByte(a.A + (b.A - a.A) * f);
        }
        return lut;
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v * 255.0), 0, 255);
}
