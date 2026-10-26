namespace RouteBaseWeb.Services.Heatmaps;

/// <summary>
/// Separable Gaussian blur of one tile with cross-tile neighbor assembly:
/// the 3×3 neighborhood is copied onto a (256 + 2·margin)² working array so
/// blurred tiles join seamlessly at borders.
/// </summary>
public static class GaussianBlur
{
    public const double DefaultSigma = 0.86;

    /// <summary>3σ Gaussian footprint — margin of neighbor context assembled around the tile.</summary>
    public static int MarginFor(double sigma) => (int)Math.Ceiling(sigma * 3);

    /// <summary>Post-blur peak of a 1px-wide line of 1s — the 1D kernel's center weight.</summary>
    public static double LinePeak(double sigma)
    {
        float[] kernel = BuildKernel(sigma, out int radius);
        return kernel[radius];
    }

    /// <summary>
    /// Reusable scratch buffers for <see cref="BlurTile(SparseTileGrid,int,int,double,BlurScratch)"/>.
    /// All three arrays are LOH-sized (~800 KB together) — one instance per pyramid build replaces
    /// a fresh set per tile.
    /// </summary>
    public sealed class BlurScratch
    {
        internal readonly float[] Assembled;
        internal readonly float[] Temp;
        internal readonly float[] Center;

        public BlurScratch(double sigma = DefaultSigma)
        {
            int exp = WebMercator.TileSize + 2 * MarginFor(sigma);
            Assembled = new float[exp * exp];
            Temp      = new float[exp * exp];
            Center    = new float[WebMercator.TileSize * WebMercator.TileSize];
        }
    }

    /// <summary>Blur tile (tx, ty) with neighbor context; returns the row-major 256×256 center.</summary>
    public static float[] BlurTile(SparseTileGrid grid, int tx, int ty, double sigma = DefaultSigma)
        => BlurTile(grid, tx, ty, sigma, new BlurScratch(sigma));

    /// <summary>
    /// Scratch variant: identical output, zero per-call array allocation. Returns
    /// <paramref name="scratch"/>.Center — valid only until the next call with the same scratch.
    /// </summary>
    public static float[] BlurTile(SparseTileGrid grid, int tx, int ty, double sigma, BlurScratch scratch)
    {
        int margin = MarginFor(sigma);
        int exp = WebMercator.TileSize + 2 * margin;
        AssembleWithNeighbors(grid, tx, ty, margin, scratch.Assembled);
        Blur(scratch.Assembled, exp, exp, sigma, scratch.Temp);

        var center = scratch.Center;
        for (int y = 0; y < WebMercator.TileSize; y++)
            Array.Copy(scratch.Assembled, (y + margin) * exp + margin, center, y * WebMercator.TileSize, WebMercator.TileSize);
        return center;
    }

    /// <summary>Copy the 3×3 tile neighborhood onto a (256 + 2·margin)² grid centered on (tx, ty).</summary>
    internal static float[] AssembleWithNeighbors(SparseTileGrid grid, int tx, int ty, int margin)
    {
        int exp = WebMercator.TileSize + 2 * margin;
        var outGrid = new float[exp * exp];
        AssembleWithNeighbors(grid, tx, ty, margin, outGrid);
        return outGrid;
    }

    private static void AssembleWithNeighbors(SparseTileGrid grid, int tx, int ty, int margin, float[] outGrid)
    {
        int size = WebMercator.TileSize;
        int exp = size + 2 * margin;
        Array.Clear(outGrid);   // missing neighbors must read as 0, exactly like a fresh array
        for (int dtx = -1; dtx <= 1; dtx++)
        {
            for (int dty = -1; dty <= 1; dty++)
            {
                var nb = grid.GetTile(tx + dtx, ty + dty);
                if (nb is null)
                    continue;
                int ax0 = margin + dtx * size;
                int ay0 = margin + dty * size;
                int axLo = Math.Max(0, ax0);
                int ayLo = Math.Max(0, ay0);
                int axHi = Math.Min(exp, ax0 + size);
                int ayHi = Math.Min(exp, ay0 + size);
                for (int ay = ayLo; ay < ayHi; ay++)
                {
                    int sy = ay - ay0;
                    for (int ax = axLo; ax < axHi; ax++)
                        outGrid[ay * exp + ax] = nb[sy * size + (ax - ax0)];
                }
            }
        }
    }

    /// <summary>In-place separable Gaussian blur with symmetric-reflect boundaries (scipy default).</summary>
    internal static void Blur(float[] data, int width, int height, double sigma)
        => Blur(data, width, height, sigma, new float[data.Length]);

    private static void Blur(float[] data, int width, int height, double sigma, float[] temp)
    {
        float[] kernel = BuildKernel(sigma, out int radius);

        // Horizontal pass: data -> temp
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                double acc = 0;
                for (int k = -radius; k <= radius; k++)
                    acc += kernel[k + radius] * data[row + Reflect(x + k, width)];
                temp[row + x] = (float)acc;
            }
        }

        // Vertical pass: temp -> data
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                double acc = 0;
                for (int k = -radius; k <= radius; k++)
                    acc += kernel[k + radius] * temp[Reflect(y + k, height) * width + x];
                data[y * width + x] = (float)acc;
            }
        }
    }

    // Kernel memo: sigma is constant across a build, so thousands of BlurTile calls reuse one
    // kernel instead of re-running the exp() loop per tile. Single volatile ref → safe to race.
    private static volatile Tuple<double, int, float[]>? _kernelCache;

    /// <summary>Normalized 1D Gaussian kernel; radius matches scipy's truncate=4.0 default.</summary>
    private static float[] BuildKernel(double sigma, out int radius)
    {
        var cached = _kernelCache;
        if (cached != null && cached.Item1 == sigma)
        {
            radius = cached.Item2;
            return cached.Item3;
        }

        radius = (int)(4.0 * sigma + 0.5);
        var kernel = new float[2 * radius + 1];
        double sum = 0;
        for (int i = -radius; i <= radius; i++)
        {
            double v = Math.Exp(-(i * (double)i) / (2 * sigma * sigma));
            kernel[i + radius] = (float)v;
            sum += v;
        }
        for (int i = 0; i < kernel.Length; i++)
            kernel[i] = (float)(kernel[i] / sum);

        _kernelCache = Tuple.Create(sigma, radius, kernel);
        return kernel;
    }

    /// <summary>Symmetric reflection: (d c b a | a b c d | d c b a).</summary>
    private static int Reflect(int idx, int n)
    {
        while (idx < 0 || idx >= n)
            idx = idx < 0 ? -idx - 1 : 2 * n - idx - 1;
        return idx;
    }
}
