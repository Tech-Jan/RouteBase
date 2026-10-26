namespace RouteBaseWeb.Services.Photos;

/// <summary>
/// Encoder for ThumbHash — a compact (~20–25 byte) placeholder that renders as a blurred preview of an
/// image with no network fetch (§1/§12). Vendored C# port of Evan Wallace's public-domain reference
/// (github.com/evanw/thumbhash, <c>rgbaToThumbHash</c>); decode happens client-side in TypeScript.
/// Input is a small RGBA bitmap (≤100×100) — the derivative service downsizes before calling this.
/// </summary>
public static class ThumbHash
{
    /// <summary>Encode a ≤100×100 RGBA image (row-major, 4 bytes/pixel) to a ThumbHash byte array.</summary>
    public static byte[] Encode(int w, int h, ReadOnlySpan<byte> rgba)
    {
        if (w > 100 || h > 100)
            throw new ArgumentException($"ThumbHash input {w}x{h} exceeds 100x100.");
        if (rgba.Length < w * h * 4)
            throw new ArgumentException("RGBA buffer too small for the given dimensions.");

        // Average color, alpha-weighted.
        double avgR = 0, avgG = 0, avgB = 0, avgA = 0;
        for (int i = 0, j = 0; i < w * h; i++, j += 4)
        {
            double alpha = rgba[j + 3] / 255.0;
            avgR += alpha / 255.0 * rgba[j];
            avgG += alpha / 255.0 * rgba[j + 1];
            avgB += alpha / 255.0 * rgba[j + 2];
            avgA += alpha;
        }
        if (avgA > 0) { avgR /= avgA; avgG /= avgA; avgB /= avgA; }

        bool hasAlpha = avgA < w * h;
        int lLimit = hasAlpha ? 5 : 7;
        int lx = Math.Max(1, Round(lLimit * (double)w / Math.Max(w, h)));
        int ly = Math.Max(1, Round(lLimit * (double)h / Math.Max(w, h)));

        var l = new double[w * h];
        var p = new double[w * h];
        var q = new double[w * h];
        var a = new double[w * h];

        // RGBA → LPQA, compositing over the average color.
        for (int i = 0, j = 0; i < w * h; i++, j += 4)
        {
            double alpha = rgba[j + 3] / 255.0;
            double r = avgR * (1 - alpha) + alpha / 255.0 * rgba[j];
            double g = avgG * (1 - alpha) + alpha / 255.0 * rgba[j + 1];
            double b = avgB * (1 - alpha) + alpha / 255.0 * rgba[j + 2];
            l[i] = (r + g + b) / 3;
            p[i] = (r + g) / 2 - b;
            q[i] = r - g;
            a[i] = alpha;
        }

        var (lDc, lAc, lScale) = EncodeChannel(l, w, h, Math.Max(3, lx), Math.Max(3, ly));
        var (pDc, pAc, pScale) = EncodeChannel(p, w, h, 3, 3);
        var (qDc, qAc, qScale) = EncodeChannel(q, w, h, 3, 3);
        var (aDc, aAc, aScale) = hasAlpha ? EncodeChannel(a, w, h, 5, 5) : (1.0, new List<double>(), 1.0);

        bool isLandscape = w > h;
        int header24 = Round(63 * lDc)
            | (Round(31.5 + 31.5 * pDc) << 6)
            | (Round(31.5 + 31.5 * qDc) << 12)
            | (Round(31 * lScale) << 18)
            | ((hasAlpha ? 1 : 0) << 23);
        int header16 = (isLandscape ? ly : lx)
            | (Round(63 * pScale) << 3)
            | (Round(63 * qScale) << 9)
            | ((isLandscape ? 1 : 0) << 15);

        var hash = new List<byte>
        {
            (byte)(header24 & 255),
            (byte)((header24 >> 8) & 255),
            (byte)(header24 >> 16),
            (byte)(header16 & 255),
            (byte)(header16 >> 8),
        };
        if (hasAlpha)
            hash.Add((byte)(Round(15 * aDc) | (Round(15 * aScale) << 4)));

        int acStart = hasAlpha ? 6 : 5;
        int acIndex = 0;
        var channels = hasAlpha
            ? new[] { lAc, pAc, qAc, aAc }
            : new[] { lAc, pAc, qAc };
        foreach (var ac in channels)
            foreach (var f in ac)
            {
                int idx = acStart + (acIndex >> 1);
                while (hash.Count <= idx) hash.Add(0);
                hash[idx] |= (byte)(Round(15 * f) << ((acIndex & 1) << 2));
                acIndex++;
            }

        return hash.ToArray();
    }

    // DCT of one channel into a DC term + normalized AC terms (0.5-centered).
    private static (double dc, List<double> ac, double scale) EncodeChannel(
        double[] channel, int w, int h, int nx, int ny)
    {
        double dc = 0, scale = 0;
        var ac = new List<double>();
        var fx = new double[w];

        for (int cy = 0; cy < ny; cy++)
        {
            for (int cx = 0; cx * ny < nx * (ny - cy); cx++)
            {
                double f = 0;
                for (int x = 0; x < w; x++)
                    fx[x] = Math.Cos(Math.PI / w * cx * (x + 0.5));
                for (int y = 0; y < h; y++)
                {
                    double fy = Math.Cos(Math.PI / h * cy * (y + 0.5));
                    for (int x = 0; x < w; x++)
                        f += channel[x + y * w] * fx[x] * fy;
                }
                f /= w * h;
                if (cx > 0 || cy > 0)
                {
                    ac.Add(f);
                    scale = Math.Max(scale, Math.Abs(f));
                }
                else
                {
                    dc = f;
                }
            }
        }

        if (scale > 0)
            for (int i = 0; i < ac.Count; i++)
                ac[i] = 0.5 + 0.5 / scale * ac[i];

        return (dc, ac, scale);
    }

    // JS Math.round semantics (round half toward +infinity), which the reference relies on.
    private static int Round(double x) => (int)Math.Floor(x + 0.5);
}
