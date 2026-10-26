using RouteBaseWeb.Services.TrackEdit;
using RouteBaseWeb.Services.Tracks;
using Xunit;

namespace RouteBaseWeb.Tests.Services.TrackEdit;

/// <summary>
/// The C# half of the TS↔C# parity proof (invariant 4). Every fixture in TestData/TrackEdit runs here and
/// in replay-parity.test.ts; both re-quantize their output to the codec grid and assert exact integer
/// equality — no epsilons. A geometry op without a fixture case does not merge.
/// </summary>
public class ReplayParityTests
{
    // Codec channel order + scale, indexed by name (mirror of TrackBlobCodec).
    private static readonly (string Name, double Scale)[] Channels =
    {
        ("time", TrackBlobCodec.TimeScale), ("lat", TrackBlobCodec.LatScale), ("lng", TrackBlobCodec.LngScale),
        ("altitude", TrackBlobCodec.AltitudeScale), ("distance", TrackBlobCodec.DistanceScale),
        ("speed", TrackBlobCodec.SpeedScale), ("heartRate", TrackBlobCodec.HeartRateScale),
        ("gradeSmooth", TrackBlobCodec.GradeSmoothScale), ("cadence", TrackBlobCodec.CadenceScale),
        ("power", TrackBlobCodec.PowerScale), ("pressure", TrackBlobCodec.PressureScale),
    };

    public static IEnumerable<object[]> AllFixtures() =>
        FixtureLoader.FileNames().Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void AllFixtures_ReplayMatchesExpected(string fileName)
    {
        var fx = FixtureLoader.Load(fileName);
        var outcome = EditReplayEngine.ReplayScript(fx.Script, BuildSources(fx));

        if (fx.ExpectError is not null)
        {
            Assert.NotNull(outcome.Error);
            Assert.Equal(fx.ExpectError.OpIndex, outcome.Error!.OpIndex);
            Assert.Equal(fx.ExpectError.Code, outcome.Error.Code);
            return;
        }

        Assert.Null(outcome.Error);
        Assert.Equal(fx.ExpectSlots!.Count, outcome.Slots.Count);
        for (int i = 0; i < fx.ExpectSlots.Count; i++)
        {
            var exp = fx.ExpectSlots[i];
            var act = outcome.Slots[i];
            Assert.Equal(exp.SlotId, act.SlotId);
            Assert.Equal(exp.Origin.ActivityId, act.Origin.ActivityId);
            Assert.Equal(exp.Origin.SplitFromSlotId, act.Origin.SplitFromSlotId);
            AssertChannels(exp, act.Track);
        }
    }

    /// <summary>TE-01's safety net: the count-only replay must agree with the full geometric replay on EVERY
    /// fixture — the same slot ids, the same point counts, the same error code at the same op index. A drift in
    /// either validation order shows up here before it can reach a read path.</summary>
    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void AllFixtures_CountSlotsMatchesFullReplay(string fileName)
    {
        var fx = FixtureLoader.Load(fileName);
        var full = EditReplayEngine.ReplayScript(fx.Script, BuildSources(fx));
        var (error, counts) = EditReplayEngine.CountSlots(fx.Script, SourceCounts(fx));

        if (full.Error is not null)
        {
            Assert.NotNull(error);
            Assert.Equal(full.Error.OpIndex, error!.OpIndex);
            Assert.Equal(full.Error.Code, error.Code);
            Assert.Empty(counts);
            return;
        }

        Assert.Null(error);
        Assert.Equal(
            full.Slots.Select(s => (s.SlotId, s.Track.Count)),
            counts.Select(c => (c.SlotId, c.PointCount)));
    }

    private static Dictionary<int, int> SourceCounts(FixtureCase fx) =>
        fx.Sources.ToDictionary(kv => kv.Key, kv => kv.Value["time"].Length);

    [Fact]
    public void FixtureDiscovery_FindsAllCases() =>
        Assert.True(FixtureLoader.FileNames().Count >= 22, "expected >= 22 fixtures (empty-dir silent-pass guard)");

    private static Dictionary<int, DecodedTrack> BuildSources(FixtureCase fx)
    {
        var sources = new Dictionary<int, DecodedTrack>();
        foreach (var (id, ch) in fx.Sources)
        {
            int n = ch["time"].Length;
            sources[id] = DecodedTrackExtensions.FromDoubleChannels(
                ToDoubles(ch, "time", TrackBlobCodec.TimeScale, n)!,
                ToDoubles(ch, "lat", TrackBlobCodec.LatScale, n)!,
                ToDoubles(ch, "lng", TrackBlobCodec.LngScale, n)!,
                ToDoubles(ch, "altitude", TrackBlobCodec.AltitudeScale, n),
                ToDoubles(ch, "distance", TrackBlobCodec.DistanceScale, n),
                ToDoubles(ch, "speed", TrackBlobCodec.SpeedScale, n),
                ToDoubles(ch, "heartRate", TrackBlobCodec.HeartRateScale, n),
                ToDoubles(ch, "gradeSmooth", TrackBlobCodec.GradeSmoothScale, n),
                ToDoubles(ch, "cadence", TrackBlobCodec.CadenceScale, n),
                ToDoubles(ch, "power", TrackBlobCodec.PowerScale, n),
                ToDoubles(ch, "pressure", TrackBlobCodec.PressureScale, n));
        }
        return sources;
    }

    // Quantized int → double via q * scale (deterministic IEEE-754, identical across languages — see §2.9).
    private static double[]? ToDoubles(Dictionary<string, long?[]> ch, string name, double scale, int n)
    {
        if (!ch.TryGetValue(name, out var vals)) return null; // whole-NULL channel
        var arr = new double[n];
        for (int i = 0; i < n; i++) arr[i] = vals[i].HasValue ? vals[i]!.Value * scale : double.NaN;
        return arr;
    }

    private static void AssertChannels(FixtureSlot exp, DecodedTrack track)
    {
        foreach (var (name, scale) in Channels)
        {
            var actual = GetChannel(track, name);
            if (exp.Channels.TryGetValue(name, out var expVals))
            {
                Assert.Equal(expVals.Length, track.Count);
                Assert.False(actual.IsNull, $"{exp.SlotId}/{name}: expected present, got whole-NULL");
                for (int i = 0; i < expVals.Length; i++)
                {
                    double v = actual[i];
                    if (expVals[i] is null)
                        Assert.True(double.IsNaN(v), $"{exp.SlotId}/{name}[{i}]: expected null, got {v}");
                    else
                        Assert.Equal(expVals[i]!.Value, TrackBlobCodec.Quantize(v, scale));
                }
            }
            else
            {
                Assert.True(actual.IsNull, $"{exp.SlotId}/{name}: expected absent (whole-NULL)");
            }
        }
    }

    private static TrackChannel GetChannel(DecodedTrack t, string name) => name switch
    {
        "time" => t.TimeEpochS,
        "lat" => t.Lat,
        "lng" => t.Lng,
        "altitude" => t.Altitude,
        "distance" => t.Distance,
        "speed" => t.Speed,
        "heartRate" => t.HeartRate,
        "gradeSmooth" => t.GradeSmooth,
        "cadence" => t.Cadence,
        "power" => t.Power,
        "pressure" => t.Pressure,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
