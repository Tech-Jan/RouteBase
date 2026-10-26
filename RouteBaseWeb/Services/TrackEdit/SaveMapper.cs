namespace RouteBaseWeb.Services.TrackEdit;

/// <summary>
/// One live slot as the genealogy walk sees it: its id, its <see cref="SlotOrigin"/> (rooted vs split-off) and
/// the activity id it ultimately descends from (a split tail inherits its parent's root).
///
/// <paramref name="AbsorbedActivityIds"/> are the rooted activities whose points were appended INTO this slot
/// (chained merges fold through). Non-empty ⇒ this slot is a merge: its geometry is no longer any one of the
/// rides that went into it, which is what makes it a new activity rather than an edit of the base (Option B).
/// </summary>
public sealed record WalkSlot(
    int SlotId, SlotOrigin Origin, int RootActivityId,
    IReadOnlyList<int>? AbsorbedActivityIds = null)
{
    public IReadOnlyList<int> Absorbed => AbsorbedActivityIds ?? [];
    public bool IsMerge => Absorbed.Count > 0;
}

/// <summary>Result of the pure slot-id bookkeeping walk over an effective script (§4.1.1). Pinned against
/// the engine's live ids/origins on the shared fixtures (T-M1) so it can never drift from the replay.</summary>
public sealed record WalkResult(
    IReadOnlyList<WalkSlot> LiveSlots,                 // ascending slot id
    IReadOnlyList<int> ConsumedRootedActivityIds);     // loaded ids whose rooted slot was append-consumed

/// <summary>An activity a save would update in place (Replace): its live slot + activity id + name.</summary>
public sealed record MappedUpdate(int SlotId, int ActivityId, string Name);

/// <summary>
/// An activity a save would create (a split-off, a merge product, or every live slot in Copy).
/// <c>RootActivityId</c> is the activity it descends from — the save reads its type + visibility for the new row.
/// <c>ParentActivityIds</c> is its LINEAGE: the activities whose current content flowed into it, recorded as
/// <c>Version_Parent</c> rows (0 = none · 1 = split/copy · N = a merge of N rides).
/// </summary>
public sealed record MappedCreate(
    int SlotId, int RootActivityId, string Name, IReadOnlyList<int>? ParentActivityIds = null)
{
    public IReadOnlyList<int> Parents => ParentActivityIds ?? [];
}

/// <summary>An activity a save would trash (a Replace-consumed rooted source): its id + name.</summary>
public sealed record MappedTrash(int ActivityId, string Name);

/// <summary>The full slot→activity plan for one save (§4.1). <c>BlockedBy</c> non-null = nothing to save.</summary>
public sealed record SavePlan(
    string Mode, IReadOnlyList<MappedUpdate> Updated,
    IReadOnlyList<MappedCreate> Created, IReadOnlyList<MappedTrash> Trashed, string? BlockedBy);

/// <summary>
/// Pure slot→activity mapping for the save pipeline (A2 §4.1): a genealogy walk over the effective ops that
/// reproduces the engine's live slot ids + origins, then the Replace/Copy mapping + naming rule. No I/O — the
/// caller supplies the root activity names. Save (I2c) uses the walk's live ids to line the plan up with the
/// engine's <see cref="ReplaySlot"/>s; preflight (I2a) uses the plan alone (no engine, no decode).
/// </summary>
public static class SaveMapper
{
    /// <summary>Slot-id bookkeeping only (never geometry): loadActivity/splitAt assign the next id (split
    /// tail's root = parent's root), append consumes its source id, every other op = no slot effect (§10 —
    /// a future sensor-style op touches nothing here). Assumes an already-validated effective script.</summary>
    public static WalkResult Walk(IReadOnlyList<EditOp> ops)
    {
        var live = new SortedDictionary<int, WalkSlot>();
        var consumed = new List<int>();
        int nextId = 0;

        foreach (var op in ops)
        {
            switch (op)
            {
                case LoadActivityOp load:
                    live[nextId] = new WalkSlot(nextId, new SlotOrigin(load.ActivityId, null), load.ActivityId);
                    nextId++;
                    break;

                case SplitAtOp split when live.TryGetValue(split.Slot, out var parent):
                    live[nextId] = new WalkSlot(nextId, new SlotOrigin(null, split.Slot), parent.RootActivityId);
                    nextId++;
                    break;

                case AppendOp append when live.TryGetValue(append.Source, out var source):
                    // The source slot is folded INTO the target and stops existing. Remember, on the target,
                    // which rooted rides it has swallowed — that is what makes the target a merge, and a merge
                    // is a new activity (Option B). A chained merge (A←B, then A←C, or A←(B←C)) carries the
                    // whole accumulated list through, so the final slot names every ride inside it.
                    var absorbed = new List<int>();
                    if (source.Origin.ActivityId is int rootedId)
                    {
                        consumed.Add(rootedId);
                        absorbed.Add(rootedId);
                    }
                    absorbed.AddRange(source.Absorbed);

                    if (live.TryGetValue(append.Target, out var target))
                        live[append.Target] = target with
                        {
                            AbsorbedActivityIds = target.Absorbed.Concat(absorbed).ToList(),
                        };

                    live.Remove(append.Source);
                    break;

                // cropTo / deleteRange / (future ops) / append-or-split on a non-live slot: no slot effect.
            }
        }
        return new WalkResult(live.Values.ToList(), consumed);
    }

    /// <summary>Build the Replace/Copy plan. <paramref name="names"/> maps a root activity id to its name
    /// (null → "Activity {id}" fallback). nothingToSave when the script has no non-loadActivity op OR no live
    /// slot survives (E24).</summary>
    public static SavePlan Map(IReadOnlyList<EditOp> ops, string mode, IReadOnlyDictionary<int, string?> names)
    {
        var walk = Walk(ops);

        bool hasChange = ops.Any(o => o is not LoadActivityOp);
        if (!hasChange || walk.LiveSlots.Count == 0)
            return new SavePlan(mode, [], [], [], TrackEditApiErrors.NothingToSave);

        string RootName(int id) => names.TryGetValue(id, out var n) && n is not null ? n : $"Activity {id}";

        var updated = new List<MappedUpdate>();
        var created = new List<MappedCreate>();
        var trashed = new List<MappedTrash>();
        var splitCounters = new Dictionary<string, int>(StringComparer.Ordinal);   // per root-name numbering, slot-id order

        string NextNumbered(int rootActivityId)
        {
            string root = RootName(rootActivityId);
            int n = splitCounters.TryGetValue(root, out var c) ? c + 1 : 1;
            splitCounters[root] = n;
            return $"{root} ({n})";
        }

        bool copy = mode == TrackEditSaveModes.Copy;
        var mergeBases = new List<int>();

        foreach (var slot in walk.LiveSlots)   // ascending slot id
        {
            if (slot.Origin.ActivityId is int activityId)
            {
                // Lineage of a rooted slot: itself, plus whatever it swallowed.
                var parents = new List<int> { activityId };
                parents.AddRange(slot.Absorbed);

                if (copy)
                {
                    created.Add(new MappedCreate(slot.SlotId, activityId, $"{RootName(activityId)} (edited)", parents));
                }
                else if (slot.IsMerge)
                {
                    // OPTION B. A slot that absorbed appends is NOT an edit of the base — its geometry,
                    // duration and distance differ from every ride that went into it, so calling it "the base,
                    // edited" is a lie that also destroys the base. It becomes a NEW activity, and the base
                    // joins the rides it swallowed in the trash — recoverable, and able to un-merge.
                    //
                    // It does NOT inherit api_id: that identifies the imported FILE and stays with the trashed
                    // base row. Two rows sharing it would break the dedupe key, and keeping it there is exactly
                    // what lets a re-import of the base's GPX report "in trash — merged into 'X'".
                    created.Add(new MappedCreate(slot.SlotId, activityId, RootName(activityId), parents));
                    mergeBases.Add(activityId);
                }
                else
                {
                    // crop / cut / untouched: same activity, the head just moves. Rotating the id here would
                    // drag photos, efforts, Strava links and bookmarked URLs with it.
                    updated.Add(new MappedUpdate(slot.SlotId, activityId, RootName(activityId)));
                }
            }
            else
            {
                // A split-off: descends from its root (and from anything it went on to absorb).
                var parents = new List<int> { slot.RootActivityId };
                parents.AddRange(slot.Absorbed);
                created.Add(new MappedCreate(
                    slot.SlotId, slot.RootActivityId, NextNumbered(slot.RootActivityId), parents));
            }
        }

        // Copy is the non-destructive mode and stays non-destructive: lineage is still recorded (Version_Parent),
        // but nothing is trashed.
        if (!copy)
            foreach (int id in walk.ConsumedRootedActivityIds.Concat(mergeBases))
                trashed.Add(new MappedTrash(id, RootName(id)));

        return new SavePlan(mode, updated, created, trashed, null);
    }
}
