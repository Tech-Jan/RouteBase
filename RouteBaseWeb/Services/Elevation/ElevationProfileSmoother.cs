namespace RouteBaseWeb.Services.Elevation;

/// <summary>
/// Curvature-penalised elevation smoother — the research-validated replacement for the altitude EMA.
/// It resamples the altitude-vs-distance profile to a uniform arc-length grid, applies a Whittaker
/// 2nd-order smoother of effective length L (penalises curvature, so it strips the terrarium DEM's
/// high-frequency sampling noise while preserving real slopes), then maps the result back to the original
/// points. On the frozen research set this takes elevation-gain error from +43 % (old EMA) to within ~10 %.
///
/// L is per CALL SITE, because the two production callers want different things: gain wants
/// <see cref="GainSmoothLengthMeters"/> (70 m), the race grade profile wants the 105 m default. See each
/// constant for why.
///
/// Dead-flat activities get a second regime (dataset-v2 finding, 196 activities): when the smoothed
/// profile's relief (population std) is under 8 m the real signal is ~metres and what remains is DEM
/// noise, so the profile is re-smoothed at L = 200 m. The threshold is robust (8 and 12 m split the
/// dataset identically); flat-run ascent error dropped 5.6 → 2.8 m vs a ~4 m reference.
///
/// Pure and unit-testable (no I/O). The Whittaker solve is a symmetric-pentadiagonal Cholesky — O(n),
/// no matrix library.
/// </summary>
public static class ElevationProfileSmoother
{
    public const double DefaultGridStepMeters = 5.0;
    public const double DefaultSmoothLengthMeters = 105.0;

    /// <summary>
    /// L for the ASCENT/DESCENT call site (<c>GpsNormalizationService.SmoothSegment</c>) — the only
    /// caller that cares about gain. Measured on 95 activities: production under-reported ascent on 87
    /// of them (mean −8.2 %) because L=105 strips 81 % of peak prominence out of its own input. Dropping
    /// to 70 m moves ascent bias vs Strava −4.7 % → +0.7 %, and vs surveyed DGM1 LiDAR −4.6 % → +0.9 %.
    ///
    /// It is NOT the default on purpose. <c>Races/RaceCourseGrid</c> wants a smooth GRADE profile for
    /// pacing cost-curves, where gain accuracy is irrelevant and a third less smoothing would shred the
    /// ±1.5 % grade band its tests pin. Its cache keys are also content shas of the track blob, not of
    /// this code, so an algorithm change there would leave stale grids behind for up to 30 minutes.
    /// </summary>
    public const double GainSmoothLengthMeters = 70.0;

    // Flat regime (recommendation.md addendum): relief below the threshold ⇒ re-smooth harder.
    public const double FlatReliefThresholdMeters = 8.0;
    public const double FlatSmoothLengthMeters = 200.0;

    /// <summary>λ for a Whittaker-2 smoother of effective length L on a grid of step ds: λ = (L/ds)^4.</summary>
    public static double Lambda(double lengthMeters, double gridStepMeters)
        => Math.Pow(lengthMeters / gridStepMeters, 4.0);

    /// <summary>
    /// Smooth <paramref name="altitude"/> sampled at cumulative arc length <paramref name="arcLength"/>
    /// (non-decreasing, same length). Returns a new array aligned to the input points. Tracks too short
    /// to carry a meaningful profile (fewer than 5 grid steps) are returned unchanged.
    /// </summary>
    public static double[] SmoothByArcLength(
        double[] arcLength, double[] altitude,
        double lengthMeters = DefaultSmoothLengthMeters, double gridStepMeters = DefaultGridStepMeters)
    {
        int n = altitude.Length;
        if (n < 3 || arcLength.Length != n) return (double[])altitude.Clone();

        double total = arcLength[n - 1] - arcLength[0];
        // Ceiling so the last grid node reaches (or passes) the final point — no uncovered tail that
        // would otherwise get clamped to the last grid value on the way back.
        int gridN = (int)Math.Ceiling(total / gridStepMeters) + 1;
        if (gridN < 5) return (double[])altitude.Clone();

        // Collapse runs of equal arc length (stationary points) so interpolation is well-defined.
        // Written into pre-sized arrays; Array.Resize only reallocates in the rare run-collapse case.
        var sMono = new double[n];
        var yMono = new double[n];
        int m = 0;
        for (int i = 0; i < n; i++)
        {
            if (m > 0 && arcLength[i] <= sMono[m - 1])
                yMono[m - 1] = altitude[i];           // keep the last sample at a repeated position
            else
            {
                sMono[m] = arcLength[i];
                yMono[m] = altitude[i];
                m++;
            }
        }
        if (m < 3) return (double[])altitude.Clone();
        if (m < n) { Array.Resize(ref sMono, m); Array.Resize(ref yMono, m); }

        var grid = new double[gridN];
        for (int i = 0; i < gridN; i++) grid[i] = arcLength[0] + i * gridStepMeters;

        var yGrid = InterpLinear(sMono, yMono, grid);
        var xGrid = WhittakerSecondOrder(yGrid, Lambda(lengthMeters, gridStepMeters));
        // Flat regime: relief is measured on the smoothed grid profile (a production observable) so
        // DEM noise can't inflate it past the threshold.
        if (Relief(xGrid) < FlatReliefThresholdMeters)
            xGrid = WhittakerSecondOrder(yGrid, Lambda(FlatSmoothLengthMeters, gridStepMeters));
        return InterpLinear(grid, xGrid, arcLength);   // back onto the original point positions
    }

    /// <summary>
    /// Whittaker–Henderson 2nd-order smoother: minimise ‖x−y‖² + λ·‖D²x‖². Solves the symmetric
    /// positive-definite pentadiagonal system (I + λ·D²ᵀD²) x = y via a banded Cholesky (LLᵀ), O(n).
    /// </summary>
    public static double[] WhittakerSecondOrder(double[] y, double lambda)
    {
        int n = y.Length;
        if (n < 3 || lambda <= 0.0) return (double[])y.Clone();

        // A = I + λ·D²ᵀD², symmetric pentadiagonal: d = diag, e = first sub-diag, f = second sub-diag.
        var d = new double[n];
        var e = new double[n];
        var f = new double[n];
        for (int i = 0; i < n; i++) d[i] = 1.0;
        AddCurvaturePenalty(d, e, f, lambda);
        return SolveSymmetricPentadiagonal(d, e, f, y);
    }

    /// <summary>
    /// Accumulate λ·D²ᵀD² into a symmetric pentadiagonal band, one 2nd-difference row at a time
    /// (stencil [1,−2,1] at columns i,i+1,i+2 contributes λ times its outer product).
    /// <paramref name="lambda"/> is either one value for every row, or one per row (length n−2) —
    /// the per-row form is what <c>ElevationFusion</c> needs and is a strict generalisation.
    ///
    /// ⚠ The accumulation ORDER is load-bearing: <c>Whittaker_FlatRegimeLambdaGoldenMatchesScipy</c>
    /// is the conditioning canary at λ = 2.56e6 (already at precision 3, ~5e-7 from scipy) and is the
    /// first thing to go red if these six lines are reordered or refactored into fewer passes.
    /// </summary>
    private static void AddCurvaturePenalty(double[] d, double[] e, double[] f, double lambda)
    {
        int n = d.Length;
        for (int i = 0; i <= n - 3; i++)
        {
            d[i]     += lambda;          // (i,i)      = 1·1
            d[i + 1] += 4.0 * lambda;    // (i+1,i+1)  = (-2)(-2)
            d[i + 2] += lambda;          // (i+2,i+2)  = 1·1
            e[i + 1] += -2.0 * lambda;   // (i+1,i)    = (-2)(1)
            e[i + 2] += -2.0 * lambda;   // (i+2,i+1)  = (1)(-2)
            f[i + 2] += lambda;          // (i+2,i)    = (1)(1)
        }
    }

    /// <inheritdoc cref="AddCurvaturePenalty(double[], double[], double[], double)"/>
    internal static void AddCurvaturePenalty(double[] d, double[] e, double[] f, double[] lambdaPerRow)
    {
        int n = d.Length;
        for (int i = 0; i <= n - 3; i++)
        {
            double lambda = lambdaPerRow[i];
            d[i]     += lambda;
            d[i + 1] += 4.0 * lambda;
            d[i + 2] += lambda;
            e[i + 1] += -2.0 * lambda;
            e[i + 2] += -2.0 * lambda;
            f[i + 2] += lambda;
        }
    }

    /// <summary>
    /// Solve A·x = rhs for a symmetric positive-definite PENTADIAGONAL A, via banded Cholesky (LLᵀ),
    /// O(n) and no matrix library. The band is given by its lower half: <paramref name="d"/> = diagonal,
    /// <paramref name="e"/> = first sub-diagonal (e[i] is A[i,i−1]), <paramref name="f"/> = second
    /// sub-diagonal (f[i] is A[i,i−2]).
    ///
    /// Shared with <c>ElevationFusion</c>, which assembles a different A and a right-hand side that is
    /// not the data vector — the sole reason this is a separate method rather than inlined in
    /// <see cref="WhittakerSecondOrder"/>, which hardcoded <c>rhs = y</c>.
    /// </summary>
    public static double[] SolveSymmetricPentadiagonal(double[] d, double[] e, double[] f, double[] rhs)
    {
        int n = d.Length;

        // Banded Cholesky: L[i,i]=l0, L[i,i-1]=l1, L[i,i-2]=l2.
        var l0 = new double[n];
        var l1 = new double[n];
        var l2 = new double[n];
        for (int i = 0; i < n; i++)
        {
            l2[i] = i >= 2 ? f[i] / l0[i - 2] : 0.0;
            l1[i] = i >= 1 ? (e[i] - l2[i] * (i >= 2 ? l1[i - 1] : 0.0)) / l0[i - 1] : 0.0;
            l0[i] = Math.Sqrt(d[i] - l1[i] * l1[i] - l2[i] * l2[i]);
        }

        // Forward solve L z = rhs.
        var z = new double[n];
        for (int i = 0; i < n; i++)
        {
            double s = rhs[i];
            if (i >= 1) s -= l1[i] * z[i - 1];
            if (i >= 2) s -= l2[i] * z[i - 2];
            z[i] = s / l0[i];
        }
        // Back solve Lᵀ x = z.
        var x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double s = z[i];
            if (i + 1 < n) s -= l1[i + 1] * x[i + 1];
            if (i + 2 < n) s -= l2[i + 2] * x[i + 2];
            x[i] = s / l0[i];
        }
        return x;
    }

    /// <summary>Population standard deviation — the "relief" of a uniform-grid profile (matches the
    /// research harness's np.std, which validated the flat threshold).</summary>
    public static double Relief(double[] values)
    {
        if (values.Length == 0) return 0.0;
        double mean = 0.0;
        foreach (var v in values) mean += v;
        mean /= values.Length;
        double ss = 0.0;
        foreach (var v in values) ss += (v - mean) * (v - mean);
        return Math.Sqrt(ss / values.Length);
    }

    /// <summary>Piecewise-linear interpolation of (xp, fp) at query points xq; clamped to the endpoints.</summary>
    public static double[] InterpLinear(double[] xp, double[] fp, double[] xq)
    {
        var outv = new double[xq.Length];
        int j = 0;
        for (int q = 0; q < xq.Length; q++)
        {
            double x = xq[q];
            if (x <= xp[0]) { outv[q] = fp[0]; continue; }
            if (x >= xp[^1]) { outv[q] = fp[^1]; continue; }
            while (j < xp.Length - 2 && xp[j + 1] < x) j++;
            while (j > 0 && xp[j] > x) j--;
            double t = (x - xp[j]) / (xp[j + 1] - xp[j]);
            outv[q] = fp[j] + t * (fp[j + 1] - fp[j]);
        }
        return outv;
    }
}
