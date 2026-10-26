using RouteBaseWeb.Services.TrackEdit;
using RouteBaseWeb.Services.Tracks;
using Xunit;

namespace RouteBaseWeb.Tests.Services.TrackEdit;

/// <summary>
/// Pure slot→activity mapping (A2 §4.1). T-M1 pins the genealogy walk against the engine's live ids/origins
/// on every success fixture so the walk can never drift from the replay; the rest exercise the Replace/Copy
/// naming rule and the nothingToSave / null-name / future-op edge cases.
/// </summary>
public class SaveMapperTests
{
    private const int A = 101, B = 202;

    public static IEnumerable<object[]> SuccessFixtures() =>
        FixtureLoader.FileNames()
            .Where(f => FixtureLoader.Load(f).ExpectSlots is not null)
            .Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(SuccessFixtures))]
    public void GenealogyWalk_MatchesEngineSlotIdsAndOrigins_OnAllFixtures(string fileName)
    {
        var fx = FixtureLoader.Load(fileName);
        var outcome = EditReplayEngine.ReplayScript(fx.Script, BuildSources(fx));
        Assert.Null(outcome.Error);

        var walk = SaveMapper.Walk(fx.Script.Ops);

        Assert.Equal(outcome.Slots.Count, walk.LiveSlots.Count);
        for (int i = 0; i < outcome.Slots.Count; i++)
        {
            Assert.Equal(outcome.Slots[i].SlotId, walk.LiveSlots[i].SlotId);
            Assert.Equal(outcome.Slots[i].Origin.ActivityId, walk.LiveSlots[i].Origin.ActivityId);
            Assert.Equal(outcome.Slots[i].Origin.SplitFromSlotId, walk.LiveSlots[i].Origin.SplitFromSlotId);
        }
    }

    /// <summary>
    /// OPTION B. A merge is not an edit of the base. Appending Beta onto Alpha yields geometry that is neither
    /// Alpha nor Beta — different distance, different duration — so calling the result "Alpha, edited" would be
    /// a lie that also destroys Alpha. It becomes a NEW activity, and BOTH inputs go to the trash, recoverable
    /// and able to un-merge. (Before Option B, Alpha was updated in place and its pre-merge geometry was gone.)
    /// </summary>
    [Fact]
    public void Replace_AppendIntoARootedSlot_CreatesANewRide_AndTrashesBothInputs()
    {
        var ops = new List<EditOp> { new LoadActivityOp(A), new LoadActivityOp(B), new AppendOp(0, 1, TimeMode.Keep, null) };

        var plan = SaveMapper.Map(ops, TrackEditSaveModes.Replace, Names((A, "Alpha"), (B, "Beta")));

        Assert.Empty(plan.Updated);                                    // nothing is edited in place
        var created = Assert.Single(plan.Created);
        Assert.Equal((0, A, "Alpha"), (created.SlotId, created.RootActivityId, created.Name));
        Assert.Equal(new[] { A, B }, created.Parents);                 // lineage: merged from both
        Assert.Equal(new[] { (B, "Beta"), (A, "Alpha") }, plan.Trashed.Select(t => (t.ActivityId, t.Name)));
        Assert.Null(plan.BlockedBy);
    }

    [Fact]
    public void Replace_SplitOffs_CreatedWithNumberedNames()
    {
        var ops = new List<EditOp> { new LoadActivityOp(A), new SplitAtOp(0, 3), new SplitAtOp(0, 2) };

        var plan = SaveMapper.Map(ops, TrackEditSaveModes.Replace, Names((A, "X")));

        Assert.Equal(new[] { "X (1)", "X (2)" }, plan.Created.Select(c => c.Name), StringComparer.Ordinal);  // slot-id order
        Assert.Equal(new[] { (0, A) }, plan.Updated.Select(u => (u.SlotId, u.ActivityId)));
        Assert.Empty(plan.Trashed);
    }

    [Fact]
    public void Replace_AppendIntoSlotOne_MergesIntoBeta_SameRule()
    {
        // G24: append target = the B slot, source = the A slot. The direction of the merge picks the name and
        // the root, not the outcome — it is still a new ride, and both inputs still go to the trash.
        var ops = new List<EditOp> { new LoadActivityOp(A), new LoadActivityOp(B), new AppendOp(1, 0, TimeMode.Keep, null) };

        var plan = SaveMapper.Map(ops, TrackEditSaveModes.Replace, Names((A, "Alpha"), (B, "Beta")));

        Assert.Empty(plan.Updated);
        var created = Assert.Single(plan.Created);
        Assert.Equal((1, B, "Beta"), (created.SlotId, created.RootActivityId, created.Name));
        Assert.Equal(new[] { B, A }, created.Parents);
        Assert.Equal(new[] { (A, "Alpha"), (B, "Beta") }, plan.Trashed.Select(t => (t.ActivityId, t.Name)));
        Assert.Empty(plan.Updated);
    }

    /// <summary>A crop/cut still keeps its activity id — only a merge mints a new one. Rotating the id on every
    /// crop would drag photos, efforts, Strava links and bookmarked URLs with it.</summary>
    [Fact]
    public void Replace_CropWithoutAppend_StillUpdatesInPlace()
    {
        var ops = new List<EditOp> { new LoadActivityOp(A), new CropToOp(0, 1, 5) };

        var plan = SaveMapper.Map(ops, TrackEditSaveModes.Replace, Names((A, "Alpha")));

        Assert.Equal(new[] { (0, A, "Alpha") }, plan.Updated.Select(u => (u.SlotId, u.ActivityId, u.Name)));
        Assert.Empty(plan.Created);
        Assert.Empty(plan.Trashed);
    }

    /// <summary>A chained merge names every ride inside the result, so all of them are trashed and all of them
    /// are recorded as parents — otherwise an un-merge would silently drop one.</summary>
    [Fact]
    public void Replace_ChainedAppends_FoldEveryInputIntoTheProduct()
    {
        const int C = 303;
        var ops = new List<EditOp>
        {
            new LoadActivityOp(A), new LoadActivityOp(B), new LoadActivityOp(C),
            new AppendOp(0, 1, TimeMode.Keep, null),   // A ← B
            new AppendOp(0, 2, TimeMode.Keep, null),   // A ← C
        };

        var plan = SaveMapper.Map(ops, TrackEditSaveModes.Replace,
            Names((A, "Alpha"), (B, "Beta"), (C, "Gamma")));

        var created = Assert.Single(plan.Created);
        Assert.Equal(new[] { A, B, C }, created.Parents);
        Assert.Equal(new[] { B, C, A }, plan.Trashed.Select(t => t.ActivityId));
    }

    [Fact]
    public void Copy_AllLiveSlotsCreated_NothingElse()
    {
        var ops = new List<EditOp> { new LoadActivityOp(A), new SplitAtOp(0, 3) };

        var plan = SaveMapper.Map(ops, TrackEditSaveModes.Copy, Names((A, "X")));

        Assert.Equal(new[] { "X (edited)", "X (1)" }, plan.Created.Select(c => c.Name), StringComparer.Ordinal);
        Assert.Empty(plan.Updated);
        Assert.Empty(plan.Trashed);
    }

    [Fact]
    public void LoadsOnlyScript_ReportsNothingToSave()
    {
        var ops = new List<EditOp> { new LoadActivityOp(A), new LoadActivityOp(B) };

        var plan = SaveMapper.Map(ops, TrackEditSaveModes.Replace, Names((A, "Alpha"), (B, "Beta")));

        Assert.Equal(TrackEditApiErrors.NothingToSave, plan.BlockedBy);
    }

    [Fact]
    public void NullRootName_FallsBackToActivityId()
    {
        var ops = new List<EditOp> { new LoadActivityOp(4711), new SplitAtOp(0, 2) };

        var plan = SaveMapper.Map(ops, TrackEditSaveModes.Replace, new Dictionary<int, string?> { [4711] = null });

        Assert.Equal("Activity 4711 (1)", Assert.Single(plan.Created).Name);
    }

    [Fact]
    public void UnknownFutureOp_TreatedAsNoSlotEffect()
    {
        var ops = new List<EditOp> { new LoadActivityOp(A), new TestStubOp() };

        var walk = SaveMapper.Walk(ops);

        var slot = Assert.Single(walk.LiveSlots);
        Assert.Equal(0, slot.SlotId);
        Assert.Equal(A, slot.Origin.ActivityId);
    }

    private sealed record TestStubOp : EditOp;   // a future op the walk must default-case as no-slot-effect

    private static IReadOnlyDictionary<int, string?> Names(params (int Id, string? Name)[] entries) =>
        entries.ToDictionary(e => e.Id, e => e.Name);

    private static Dictionary<int, DecodedTrack> BuildSources(FixtureCase fx)
    {
        var sources = new Dictionary<int, DecodedTrack>();
        foreach (var (id, ch) in fx.Sources)
        {
            int n = ch["time"].Length;
            double[]? Col(string name, double scale)
            {
                if (!ch.TryGetValue(name, out var vals)) return null;
                var arr = new double[n];
                for (int i = 0; i < n; i++) arr[i] = vals[i].HasValue ? vals[i]!.Value * scale : double.NaN;
                return arr;
            }
            sources[id] = DecodedTrackExtensions.FromDoubleChannels(
                Col("time", TrackBlobCodec.TimeScale)!, Col("lat", TrackBlobCodec.LatScale)!,
                Col("lng", TrackBlobCodec.LngScale)!, Col("altitude", TrackBlobCodec.AltitudeScale),
                Col("distance", TrackBlobCodec.DistanceScale), Col("speed", TrackBlobCodec.SpeedScale),
                Col("heartRate", TrackBlobCodec.HeartRateScale), Col("gradeSmooth", TrackBlobCodec.GradeSmoothScale),
                Col("cadence", TrackBlobCodec.CadenceScale), Col("power", TrackBlobCodec.PowerScale),
                Col("pressure", TrackBlobCodec.PressureScale));
        }
        return sources;
    }
}
