namespace RouteBaseWeb.Services.Elevation;

/// <summary>
/// DEM + barometer fusion as one banded least-squares solve — the C# port of the research harness's
/// <c>pipeline/fusion.py</c>, which is itself validated against surveyed DGM1 LiDAR.
///
/// <code>
/// min_x  Σ α_i (x_i − dem_i)²  +  Σ β_i (Δx_i − Δbaro_i)²  +  Σ λ_i (D²x)_i²
/// </code>
///
/// Three terms, three jobs:
/// <list type="bullet">
/// <item><b>α — the DEM anchor.</b> Absolute and drift-free, but wrong by ~7.6 m wherever GPS slipped,
/// and that error stays correlated for ~390 m, so it does NOT average away.</item>
/// <item><b>β — the barometric slope.</b> The measurement that replaces the smoothness PRIOR with data.
/// It never looks a position up, so GPS slip cannot touch it; it drifts slowly, which α pins down.</item>
/// <item><b>λ — the curvature prior.</b> In the DEM-only pipeline this carries the whole burden of noise
/// suppression, and it is why peaks die: |D²x| is maximal exactly at a summit, so the penalty bites
/// hardest where the terrain is most real. Once β supplies the shape, λ collapses toward zero (10 m
/// here vs 70 m there) — that is the mechanism by which fusion wins, not "adding detail".</item>
/// </list>
///
/// Only per-node weights can carry two clocks: DEM error lives in SPACE (arc length), barometric error
/// in TIME (drift per hour, noise per sample). A 5-minute stop advances no arc length but accrues drift;
/// a fast descent covers a lot of arc length in little time. <see cref="BetaWeights"/> encodes that per
/// node, so a stop drives β→0 with no special case.
///
/// The assembled matrix is <c>diag(α) + D1ᵀdiag(β)D1 + D2ᵀdiag(λ)D2</c> — still symmetric PENTADIAGONAL,
/// so this reuses <see cref="ElevationProfileSmoother.SolveSymmetricPentadiagonal"/> rather than
/// rewriting the banded Cholesky.
///
/// <b>Sell this as a PROFILE fix, not a gain fix.</b> Peak F1 0.25 → 0.78, spurious peaks 19 → 3, band
/// correlation ~2.7×. The ascent-accuracy win belongs to <see cref="ElevationProfileSmoother.GainSmoothLengthMeters"/>,
/// which needs no barometer and applies to every activity.
///
/// Pure and unit-testable (no I/O).
/// </summary>
public static class ElevationFusion
{
    /// <summary>
    /// The curvature prior's nominal length on the FUSED path — the single fitted parameter here;
    /// everything else is a measured constant. Swept on native points (a 5 m-grid sweep is optimistic
    /// by ~1.2 % and puts the optimum at 12 m). L = 8..12 is a plateau (bias +6.5 / +0.8 / −2.4 %,
    /// F1 flat at 0.78), so the choice inside it is weakly determined; 10 zeroes the bias.
    /// λ = 0 is NOT an option: with no prior at all, the 2 m deadband turns barometer noise straight
    /// into gain (+36 %).
    /// </summary>
    public const double FusedSmoothLengthMeters = 10.0;

    /// <summary>DEM error scale — the variogram sill over 95 activities.</summary>
    public const double SigmaDemMeters = 7.65;

    /// <summary>
    /// DEM error correlation length, from the same variogram. α is DECIMATED to one anchor per
    /// correlation length, never one per node — see <see cref="AlphaWeights"/> for why.
    /// </summary>
    public const double DemCorrelationLengthMeters = 390.0;

    /// <summary>Per-delivered-sample barometric increment noise — median over 22 stationary windows.
    /// It varies ~70× between recordings; this is the corpus median, not a per-recording estimate.</summary>
    public const double SigmaBaroIncrementMeters = 0.16;

    /// <summary>Barometric drift, treated as a random walk. Measured by loop closure.</summary>
    public const double BaroDriftMetersPerHour = 4.2;

    /// <summary>
    /// Below this fraction of points carrying a finite pressure, the track counts as having no
    /// barometer at all. Partial coverage is common (a sensor that dies mid-ride, a phone swap) and the
    /// threshold decides fuse-vs-fallback for the WHOLE track rather than switching formula halfway
    /// through, which would leave a visible seam in the profile.
    /// </summary>
    public const double MinPressureCoverage = 0.5;

    /// <summary>How many delivered samples span one node — the LATCH correction (the phone reads the
    /// barometer once per location batch and stamps that value on every point in it, so with a run of
    /// 10 an increment carries a tenth of the information). Production does not estimate it: every
    /// number in the validated sweep was produced at 1.0, and shipping an unvalidated weighting to
    /// chase a 4× change in β is not a port.</summary>
    public const double DefaultSamplesPerNode = 1.0;

    // Too few nodes to be worth solving — mirrors the reference's per-segment `continue`, which leaves
    // the DEM-only fallback in place for that stretch.
    private const int MinFusableNodes = 10;
    private const int MinGridNodes = 5;

    // ISA pressure altitude. The ABSOLUTE value is meaningless (sea-level pressure is unknown and
    // drifts) — only differences carry information, and α supplies the absolute level.
    private const double IsaSeaLevelPressureHpa = 1013.25;
    private const double IsaExponent = 0.1902949;
    private const double IsaScaleMeters = 44330.0;

    private const double StandardGravity = 9.80665;

    // Surface pressure anywhere on Earth sits between the Dead Sea (~1085 hPa record) and the summit of
    // Everest (~330 hPa), so anything outside this is not a barometer reading. Deliberately the SAME
    // band `RecordingRequestValidator` already rejects on ingest — this is the second line, not the
    // first. It exists because the kernel is public and reachable from paths that validator never sees
    // (edit replay, sync, a future import that learns to carry pressure), and because a single 0 hPa
    // reads as 44 330 m of ISA altitude, which wrecks the whole solve rather than one point. Not in the
    // reference, which only ever saw real phone data.
    private const double MinPlausiblePressureHpa = 300.0;
    private const double MaxPlausiblePressureHpa = 1100.0;

    /// <summary>A reading the solver may use: finite, and physically possible at Earth's surface.</summary>
    public static bool IsUsableReading(double pressureHpa) =>
        double.IsFinite(pressureHpa)
        && pressureHpa is >= MinPlausiblePressureHpa and <= MaxPlausiblePressureHpa;

    /// <summary>
    /// Is there enough barometer to fuse with? Decide this ONCE for a whole track: a sensor that dies
    /// mid-ride must fall back everywhere, never mid-track.
    /// </summary>
    public static bool HasUsablePressure(double[]? pressureHpa, double minCoverage = MinPressureCoverage)
    {
        if (pressureHpa is null || pressureHpa.Length == 0) return false;
        int usable = 0;
        foreach (var hpa in pressureHpa)
            if (IsUsableReading(hpa)) usable++;
        return (double)usable / pressureHpa.Length >= minCoverage;
    }

    /// <summary>ISA pressure altitude in metres, from pressure in hPa.</summary>
    public static double[] IsaAltitude(double[] pressureHpa)
    {
        var outv = new double[pressureHpa.Length];
        for (int i = 0; i < pressureHpa.Length; i++)
            outv[i] = IsaScaleMeters * (1.0 - Math.Pow(pressureHpa[i] / IsaSeaLevelPressureHpa, IsaExponent));
        return outv;
    }

    /// <summary>
    /// Undo the phone's delivery latch: return a per-point pressure interpolated in TIME between
    /// delivery batches. A batch is a run of bit-identical values, which is exactly what the latch
    /// produces (0.01 hPa storage quantisation is far finer than the sensor's own variation, so genuine
    /// consecutive equality is rare enough to ignore).
    ///
    /// Attribution matters: the phone reads the barometer at DELIVERY time and back-stamps it onto every
    /// point of the batch, so the value describes the END of the run. Each batch is therefore anchored
    /// at its LAST point's timestamp. Feeding the raw staircase into a first-difference operator instead
    /// would put the whole step on one sample and zero on the rest — injecting a zero-order-hold edge
    /// into the output as if it were terrain. This is the single most important correction here.
    ///
    /// Fewer than two anchors ⇒ returned unchanged (nothing to interpolate between).
    /// </summary>
    public static double[] Destaircase(double[] pressureHpa, double[] timeSeconds)
    {
        int n = pressureHpa.Length;
        if (n == 0 || timeSeconds.Length != n) return (double[])pressureHpa.Clone();

        // Pass 1 — one anchor per delivery batch, sliding forward to the batch's LAST point as it grows.
        var anchorTime = new List<double>();
        var anchorPressure = new List<double>();
        for (int i = 0; i < n; i++)
        {
            if (!IsUsableReading(pressureHpa[i]) || !double.IsFinite(timeSeconds[i])) continue;
            // An unusable reading between two equal ones breaks the run, exactly as a NaN does in the
            // reference's batch index.
            bool continuesBatch = anchorPressure.Count > 0
                && i > 0
                && IsUsableReading(pressureHpa[i - 1])
                && pressureHpa[i] == pressureHpa[i - 1];

            if (continuesBatch) anchorTime[^1] = timeSeconds[i];
            else { anchorTime.Add(timeSeconds[i]); anchorPressure.Add(pressureHpa[i]); }
        }

        // Pass 2 — and ONLY now collapse duplicate anchor times, keeping the last reading at an instant.
        // This must be a second pass. The phone stamps several points on the same second often enough
        // (432 of 3776 on one measured commute), and de-duplicating while pass 1 is still sliding an
        // anchor forward compares against a time that is not final yet — which deletes the PRECEDING
        // batch's anchor and quietly bends the profile by ~0.6 m. Caught only by replaying a real
        // recording against the reference; the synthetic fixtures have strictly increasing timestamps.
        //
        // Track timestamps are non-decreasing within a segment, so this leaves a strictly increasing
        // interpolation table; a backwards jump collapses onto its predecessor rather than producing a
        // non-monotonic one.
        int kept = 0;
        for (int i = 0; i < anchorTime.Count; i++)
        {
            int slot = kept > 0 && anchorTime[i] <= anchorTime[kept - 1] ? kept - 1 : kept++;
            anchorTime[slot] = anchorTime[i];
            anchorPressure[slot] = anchorPressure[i];
        }

        if (kept < 2) return (double[])pressureHpa.Clone();
        return ElevationProfileSmoother.InterpLinear(
            [.. anchorTime.Take(kept)], [.. anchorPressure.Take(kept)], timeSeconds);
    }

    /// <summary>
    /// Per-node DEM trust, honest about spatial correlation.
    ///
    /// Treating every node as independent would let the solver believe the DEM
    /// <c>correlationLength/gridStep</c> times more than the data supports — 78× at the measured 390 m.
    /// Instead the constraint is DECIMATED to one anchor per correlation length, each at full strength:
    /// same total information, no double count. The tail is always pinned so no stretch floats.
    /// </summary>
    public static double[] AlphaWeights(
        int nodeCount,
        double gridStepMeters,
        double sigmaDemMeters = SigmaDemMeters,
        double correlationLengthMeters = DemCorrelationLengthMeters)
    {
        var alpha = new double[nodeCount];
        if (nodeCount == 0) return alpha;
        int stride = Math.Max(1, (int)Math.Round(correlationLengthMeters / gridStepMeters));
        double weight = 1.0 / (sigmaDemMeters * sigmaDemMeters);
        for (int i = 0; i < nodeCount; i += stride) alpha[i] = weight;
        alpha[nodeCount - 1] = weight;
        return alpha;
    }

    /// <summary>
    /// Per-increment barometric slope trust — where the TIME clock enters:
    /// <c>β_i = 1 / (2σ_inc²/m + q_b·Δt_i)</c>, with drift as a random walk (<c>q_b</c> = drift² per second).
    ///
    /// A stop or a recording gap inflates Δt, so β collapses on its own. No special case.
    /// </summary>
    public static double[] BetaWeights(
        double[] dtSeconds,
        double samplesPerNode = DefaultSamplesPerNode,
        double sigmaIncrementMeters = SigmaBaroIncrementMeters,
        double driftMetersPerHour = BaroDriftMetersPerHour)
    {
        double randomWalkPerSecond = driftMetersPerHour * driftMetersPerHour / 3600.0;
        double spanned = Math.Max(samplesPerNode, 1e-6);
        var beta = new double[dtSeconds.Length];
        for (int i = 0; i < dtSeconds.Length; i++)
        {
            double denominator = 2.0 * sigmaIncrementMeters * sigmaIncrementMeters / spanned
                + randomWalkPerSecond * Math.Max(dtSeconds[i], 0.0);
            beta[i] = 1.0 / Math.Max(denominator, 1e-12);
        }
        return beta;
    }

    /// <summary>
    /// Solve the banded system. All arrays live on ONE uniform grid: <paramref name="dem"/>,
    /// <paramref name="baro"/> and <paramref name="alpha"/> have length n, <paramref name="beta"/>
    /// weights the n−1 INCREMENTS, <paramref name="lambdaPerRow"/> the n−2 curvature rows.
    ///
    /// A non-finite DEM value or barometric increment zeroes the matching weight — a missing sensor
    /// contributes nothing rather than poisoning the solve.
    /// </summary>
    public static double[] Fuse(double[] dem, double[] baro, double[] alpha, double[] beta, double[] lambdaPerRow)
    {
        int n = dem.Length;
        if (n < 3) return (double[])dem.Clone();

        var y = new double[n];
        var a = new double[n];
        double alphaSum = 0.0;
        for (int i = 0; i < n; i++)
        {
            bool usable = double.IsFinite(dem[i]);
            y[i] = usable ? dem[i] : 0.0;
            a[i] = usable ? alpha[i] : 0.0;
            alphaSum += a[i];
        }
        if (alphaSum <= 0.0)
            throw new ArgumentException(
                "alpha is all zero — the system is singular (no DEM anchor survived)", nameof(alpha));

        var increment = new double[n - 1];
        var b = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            double delta = baro[i + 1] - baro[i];
            bool usable = double.IsFinite(delta);
            increment[i] = usable ? delta : 0.0;
            b[i] = usable ? beta[i] : 0.0;
        }

        // A = diag(α) + D1ᵀ diag(β) D1 + D2ᵀ diag(λ) D2, as its lower band.
        var d = new double[n];
        var e = new double[n];
        var f = new double[n];
        for (int i = 0; i < n; i++) d[i] = a[i];
        for (int i = 0; i < n - 1; i++)
        {
            // Row i of D1 is (−1 at i, +1 at i+1); β_i times its outer product.
            d[i] += b[i];
            d[i + 1] += b[i];
            e[i + 1] += -b[i];
        }
        ElevationProfileSmoother.AddCurvaturePenalty(d, e, f, lambdaPerRow);

        // rhs = α·dem + D1ᵀ(β·Δbaro)
        var rhs = new double[n];
        for (int i = 0; i < n; i++) rhs[i] = a[i] * y[i];
        for (int i = 0; i < n - 1; i++)
        {
            double weighted = b[i] * increment[i];
            rhs[i] -= weighted;
            rhs[i + 1] += weighted;
        }

        return ElevationProfileSmoother.SolveSymmetricPentadiagonal(d, e, f, rhs);
    }

    /// <summary>
    /// Fuse one CONTINUOUS segment's DEM altitude with its barometer, both sampled at cumulative arc
    /// length. Returns a new array aligned to the input points.
    ///
    /// The caller owns two things this deliberately does not:
    /// <list type="number">
    /// <item><b>Segment breaks.</b> <c>GpsNormalizationService.SmoothAltitudes</c> already splits at
    /// &gt;60 s / &gt;150 m and calls per segment, so the barometer cannot bridge a pause. Pressure keeps
    /// changing while you drive to the trailhead, and a fused profile that interpolated through one would
    /// bank the whole altitude change as climbing.</item>
    /// <item><b>The fuse-or-fall-back decision</b>, which is per TRACK, not per segment — see
    /// <see cref="HasUsablePressure"/>.</item>
    /// </list>
    ///
    /// ⚠ <b>The flat regime is deliberately NOT applied here — do not "fix" this by mirroring
    /// <see cref="ElevationProfileSmoother.SmoothByArcLength"/>.</b> Re-smoothing at L=200 m exists to
    /// crush DEM noise on flat ground, but flat ground is exactly where the barometer carries real signal
    /// the DEM cannot resolve. Measured on the one real same-day repeat pair where it fires (same 3 km
    /// route, 3.5 h apart, so true gain is identical): fusion gives 17.1 / 18.9 m, a 10 % cross-pass
    /// spread; adding the flat regime gives 8.1 / 2.0 m — 120 %. On a 3.4 m-relief track nothing survives
    /// L=200 but noise, which is why it varies wildly between identical rides.
    ///
    /// Falls back to the DEM-only smoother at <paramref name="fallbackLengthMeters"/> when the segment
    /// cannot be fused at all (no usable pressure, mismatched channel lengths, or too few nodes).
    /// </summary>
    public static double[] SmoothByArcLength(
        double[] arcLength,
        double[] altitude,
        double[] pressureHpa,
        double[] timeSeconds,
        double fallbackLengthMeters = ElevationProfileSmoother.DefaultSmoothLengthMeters,
        double lengthMeters = FusedSmoothLengthMeters,
        double gridStepMeters = ElevationProfileSmoother.DefaultGridStepMeters,
        double samplesPerNode = DefaultSamplesPerNode,
        double dynamicPressureCoefficient = 0.0,
        double[]? speedMetersPerSecond = null)
    {
        int n = altitude.Length;
        double[] Fallback() => ElevationProfileSmoother.SmoothByArcLength(
            arcLength, altitude, fallbackLengthMeters, gridStepMeters);

        if (arcLength.Length != n || pressureHpa.Length != n || timeSeconds.Length != n) return Fallback();
        if (!HasUsablePressure(pressureHpa)) return Fallback();

        var baro = IsaAltitude(Destaircase(pressureHpa, timeSeconds));
        if (dynamicPressureCoefficient != 0.0 && speedMetersPerSecond is { } speed && speed.Length == n)
        {
            // Bernoulli: moving air reads as a lower altitude, Δh = −c·v²/2g. The shape is
            // parameter-free; only the coupling c is empirical (~0.39, positive in all 5 measured
            // pairs). Left at 0 by default — it is ~1.3 m at the speeds in the corpus, and the regime
            // where it would matter (15 m/s ⇒ ~4.5 m) is untested. Turning it on also means passing
            // a speed channel at the call site.
            for (int i = 0; i < n; i++)
            {
                double v = double.IsFinite(speed[i]) ? speed[i] : 0.0;
                baro[i] += dynamicPressureCoefficient * v * v / (2.0 * StandardGravity);
            }
        }

        // Collapse runs of equal arc length, keeping the LAST sample at a repeated position — the
        // identical rule ElevationProfileSmoother uses, applied to EVERY channel at once. A companion
        // channel collapsed by a different rule (or not at all) silently desynchronises from the DEM.
        // Arc length is cumulative Haversine, so it is non-decreasing by construction.
        var arcMono = new double[n];
        var demMono = new double[n];
        var baroMono = new double[n];
        var timeMono = new double[n];
        int usable = 0, m = 0;
        for (int i = 0; i < n; i++)
        {
            if (!double.IsFinite(altitude[i]) || !double.IsFinite(arcLength[i])
                || !double.IsFinite(baro[i]) || !double.IsFinite(timeSeconds[i]))
                continue;
            usable++;
            if (m > 0 && arcLength[i] <= arcMono[m - 1])
            {
                demMono[m - 1] = altitude[i];
                baroMono[m - 1] = baro[i];
                timeMono[m - 1] = timeSeconds[i];
            }
            else
            {
                arcMono[m] = arcLength[i];
                demMono[m] = altitude[i];
                baroMono[m] = baro[i];
                timeMono[m] = timeSeconds[i];
                m++;
            }
        }
        if (usable < MinFusableNodes || m < MinGridNodes) return Fallback();

        double total = arcMono[m - 1] - arcMono[0];
        int gridN = (int)Math.Ceiling(total / gridStepMeters) + 1;
        if (gridN < MinGridNodes) return Fallback();
        if (m < n) { Array.Resize(ref arcMono, m); Array.Resize(ref demMono, m); Array.Resize(ref baroMono, m); Array.Resize(ref timeMono, m); }

        var grid = new double[gridN];
        for (int i = 0; i < gridN; i++) grid[i] = arcMono[0] + i * gridStepMeters;

        var demGrid = ElevationProfileSmoother.InterpLinear(arcMono, demMono, grid);
        var baroGrid = ElevationProfileSmoother.InterpLinear(arcMono, baroMono, grid);
        var timeGrid = ElevationProfileSmoother.InterpLinear(arcMono, timeMono, grid);

        var dtSeconds = new double[gridN - 1];
        for (int i = 0; i < gridN - 1; i++) dtSeconds[i] = Math.Max(timeGrid[i + 1] - timeGrid[i], 1e-3);

        var alpha = AlphaWeights(gridN, gridStepMeters);
        var beta = BetaWeights(dtSeconds, samplesPerNode);
        var lambdaPerRow = new double[gridN - 2];
        Array.Fill(lambdaPerRow, ElevationProfileSmoother.Lambda(lengthMeters, gridStepMeters));

        var fused = Fuse(demGrid, baroGrid, alpha, beta, lambdaPerRow);
        return ElevationProfileSmoother.InterpLinear(grid, fused, arcLength);
    }
}
