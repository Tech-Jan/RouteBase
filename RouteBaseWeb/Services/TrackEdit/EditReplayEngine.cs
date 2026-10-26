using RouteBaseWeb.Services.Tracks;

namespace RouteBaseWeb.Services.TrackEdit;

/// <summary>Which activity/slot a replay slot descends from — exactly one field non-null. A loadActivity
/// root carries <c>ActivityId</c>; a split-off carries the id of the slot it split from.</summary>
public sealed record SlotOrigin(int? ActivityId, int? SplitFromSlotId);

/// <summary>A validation/replay failure: the failing op's index (or -1 for script-level checks) + a
/// <see cref="TrackEditErrors"/> code.</summary>
public sealed record ReplayError(int OpIndex, string Code);

public sealed record ReplaySlot(int SlotId, SlotOrigin Origin, DecodedTrack Track);

/// <summary>Success carries <c>Slots</c> (ascending slot id); failure carries <c>Error</c> and no slots.</summary>
public sealed record ReplayOutcome(ReplayError? Error, IReadOnlyList<ReplaySlot> Slots);

/// <summary>
/// Pure, I/O-free replay over <see cref="DecodedTrack"/> sources (A1 §3). Ops apply sequentially, each on
/// the previous op's output (invariant 2 — no index back-translation). The TS twin is apply-script.ts;
/// the two op switches are the parity surface proven by the shared JSON fixtures (invariant 4).
/// </summary>
public static class EditReplayEngine
{
    /// <summary>Validate the whole script (schemaVersion + op cap) then replay its ops.</summary>
    public static ReplayOutcome ReplayScript(EditScript script, IReadOnlyDictionary<int, DecodedTrack> sources)
    {
        if (script.SchemaVersion != 1) return Fail(-1, TrackEditErrors.BadSchemaVersion);
        if (script.Ops.Count > TrackEditLimits.MaxOps) return Fail(-1, TrackEditErrors.ScriptTooLong);
        return Replay(script.Ops, sources);
    }

    /// <summary>Replay an already-cursor-sliced op list. First failure wins, reported with its op index.</summary>
    public static ReplayOutcome Replay(
        IReadOnlyList<EditOp> effectiveOps, IReadOnlyDictionary<int, DecodedTrack> sources)
    {
        var state = new ReplayState(sources);
        for (int i = 0; i < effectiveOps.Count; i++)
        {
            string? error = Apply(state, effectiveOps[i]);
            if (error is not null) return Fail(i, error);
        }
        return new ReplayOutcome(null, state.ToSlots());
    }

    private static ReplayOutcome Fail(int opIndex, string code) =>
        new(new ReplayError(opIndex, code), Array.Empty<ReplaySlot>());

    // ---- count-only replay (TE-01) ----

    /// <summary>
    /// Count-only replay: slot ids + point counts, no channel values, no blob reads, zero per-point work.
    /// Mirrors <see cref="ReplayScript"/>'s validation ORDER for every code the count/index arithmetic can
    /// decide; the two value-derived codes (<c>timeAnchorMissing</c>, <c>valueOutOfRange</c>) are out of reach
    /// by construction — the read paths use this only for scripts the full geometric replay already accepted at
    /// append time (<see cref="EditSessionService"/>.AppendOpsAsync). Counts come from the session's version
    /// stamps, so a source re-versioned under the session yields the STAMPED count (the honest number for a
    /// session pinned to those bytes; the save's stamp check rejects it anyway).
    /// </summary>
    public static (ReplayError? Error, IReadOnlyList<SlotCount> Slots) CountSlots(
        EditScript script, IReadOnlyDictionary<int, int> sourceCounts)
    {
        if (script.SchemaVersion != 1) return CountFail(-1, TrackEditErrors.BadSchemaVersion);
        if (script.Ops.Count > TrackEditLimits.MaxOps) return CountFail(-1, TrackEditErrors.ScriptTooLong);

        var state = new CountState(sourceCounts);
        for (int i = 0; i < script.Ops.Count; i++)
        {
            string? error = CountApply(state, script.Ops[i]);
            if (error is not null) return CountFail(i, error);
        }
        return (null, state.ToSlotCounts());
    }

    private static (ReplayError?, IReadOnlyList<SlotCount>) CountFail(int opIndex, string code) =>
        (new ReplayError(opIndex, code), Array.Empty<SlotCount>());

    private static string? CountApply(CountState state, EditOp op) => op switch
    {
        LoadActivityOp o => CountLoad(state, o),
        CropToOp o => CountCrop(state, o),
        DeleteRangeOp o => CountDelete(state, o),
        SplitAtOp o => CountSplit(state, o),
        AppendOp o => CountAppend(state, o),
        _ => TrackEditErrors.UnknownOp,
    };

    private static string? CountLoad(CountState state, LoadActivityOp op)
    {
        if (!state.SourceCounts.TryGetValue(op.ActivityId, out int count)) return TrackEditErrors.SourceMissing;
        if (!state.MarkLoaded(op.ActivityId)) return TrackEditErrors.DuplicateLoad;
        if (state.LiveCount >= TrackEditLimits.MaxSlots) return TrackEditErrors.SlotCapExceeded;
        state.AddSlot(count);
        return null;
    }

    private static string? CountCrop(CountState state, CropToOp op)
    {
        if (!state.TryGetLive(op.Slot, out int count, out string? notLive)) return notLive;
        string? range = ValidateRange(op.From, op.To, count);
        if (range is not null) return range;
        if (op.To - op.From + 1 < TrackEditLimits.MinSlotPoints) return TrackEditErrors.ResultTooSmall;
        state.Replace(op.Slot, op.To - op.From + 1);
        return null;
    }

    private static string? CountDelete(CountState state, DeleteRangeOp op)
    {
        if (!state.TryGetLive(op.Slot, out int count, out string? notLive)) return notLive;
        string? range = ValidateRange(op.From, op.To, count);
        if (range is not null) return range;
        int remaining = count - (op.To - op.From + 1);
        if (remaining < TrackEditLimits.MinSlotPoints) return TrackEditErrors.ResultTooSmall;
        string? gapError = ValidateTimeMode(op.TimeMode, op.GapSeconds);
        if (gapError is not null) return gapError;
        state.Replace(op.Slot, remaining);
        return null;
    }

    private static string? CountSplit(CountState state, SplitAtOp op)
    {
        if (!state.TryGetLive(op.Slot, out int count, out string? notLive)) return notLive;
        if (op.Index < 1 || op.Index > count - 2) return TrackEditErrors.SplitIndexOutOfBounds;
        if (state.LiveCount >= TrackEditLimits.MaxSlots) return TrackEditErrors.SlotCapExceeded; // +1 net slot
        state.Replace(op.Slot, op.Index + 1);   // head keeps the shared boundary point
        state.AddSlot(count - op.Index);        // tail
        return null;
    }

    private static string? CountAppend(CountState state, AppendOp op)
    {
        if (!state.TryGetLive(op.Target, out int target, out string? tErr)) return tErr;
        if (!state.TryGetLive(op.Source, out int source, out string? sErr)) return sErr;
        if (op.Target == op.Source) return TrackEditErrors.SelfAppend;
        string? gapError = ValidateTimeMode(op.TimeMode, op.GapSeconds);
        if (gapError is not null) return gapError;
        state.Replace(op.Target, target + source);
        state.Consume(op.Source);
        return null;
    }

    /// <summary>The <see cref="ReplayState"/> shape with counts where the buffers were.</summary>
    private sealed class CountState
    {
        private readonly SortedDictionary<int, int> _live = new();
        private readonly HashSet<int> _loaded = new();
        private int _nextId;

        public CountState(IReadOnlyDictionary<int, int> sourceCounts) => SourceCounts = sourceCounts;

        public IReadOnlyDictionary<int, int> SourceCounts { get; }
        public int LiveCount => _live.Count;

        public bool MarkLoaded(int activityId) => _loaded.Add(activityId);

        public void AddSlot(int count) => _live[_nextId++] = count;

        public void Replace(int slotId, int count) => _live[slotId] = count;

        public void Consume(int slotId) => _live.Remove(slotId); // id stays < nextId ⇒ future ops see slotConsumed

        public bool TryGetLive(int slotId, out int count, out string? errorCode)
        {
            if (_live.TryGetValue(slotId, out count)) { errorCode = null; return true; }
            errorCode = slotId >= 0 && slotId < _nextId ? TrackEditErrors.SlotConsumed : TrackEditErrors.SlotNotFound;
            return false;
        }

        public IReadOnlyList<SlotCount> ToSlotCounts()
        {
            var counts = new List<SlotCount>(_live.Count);
            foreach (var (id, count) in _live) counts.Add(new SlotCount(id, count));
            return counts;
        }
    }

    private static string? Apply(ReplayState state, EditOp op) => op switch
    {
        LoadActivityOp o => ApplyLoad(state, o),
        CropToOp o => ApplyCrop(state, o),
        DeleteRangeOp o => ApplyDelete(state, o),
        SplitAtOp o => ApplySplit(state, o),
        AppendOp o => ApplyAppend(state, o),
        _ => TrackEditErrors.UnknownOp, // unreachable for the sealed hierarchy; also covers a future op
    };

    // ---- op handlers (validation order mirrors the A1 §2.1 matrix, shared with apply-script.ts) ----

    private static string? ApplyLoad(ReplayState state, LoadActivityOp op)
    {
        if (!state.Sources.TryGetValue(op.ActivityId, out var track)) return TrackEditErrors.SourceMissing;
        if (!state.MarkLoaded(op.ActivityId)) return TrackEditErrors.DuplicateLoad;
        if (state.LiveCount >= TrackEditLimits.MaxSlots) return TrackEditErrors.SlotCapExceeded;
        state.AddSlot(EditSlotBuffer.FromDecodedTrack(track, new SlotOrigin(op.ActivityId, null)));
        return null;
    }

    private static string? ApplyCrop(ReplayState state, CropToOp op)
    {
        if (!state.TryGetLive(op.Slot, out var slot, out string? notLive)) return notLive;
        string? range = ValidateRange(op.From, op.To, slot.Count);
        if (range is not null) return range;
        if (op.To - op.From + 1 < TrackEditLimits.MinSlotPoints) return TrackEditErrors.ResultTooSmall;

        var result = slot.Slice(op.From, op.To, slot.Origin);
        if (op.From > 0 && !RebaseNewHead(result)) return TrackEditErrors.ValueOutOfRange; // new-head → 0
        state.Replace(op.Slot, result);
        return null;
    }

    private static string? ApplyDelete(ReplayState state, DeleteRangeOp op)
    {
        if (!state.TryGetLive(op.Slot, out var slot, out string? notLive)) return notLive;
        string? range = ValidateRange(op.From, op.To, slot.Count);
        if (range is not null) return range;
        if (slot.Count - (op.To - op.From + 1) < TrackEditLimits.MinSlotPoints) return TrackEditErrors.ResultTooSmall;
        string? gapError = ValidateTimeMode(op.TimeMode, op.GapSeconds);
        if (gapError is not null) return gapError;

        bool tailExists = op.To < slot.Count - 1;
        var result = slot.WithoutRange(op.From, op.To, slot.Origin);
        int tailStart = op.From;        // tail run occupies [from..newCount) in the result
        int newCount = result.Count;

        // Time junction (§2.2): shift the tail run so the junction gap becomes gapSeconds. keep = untouched;
        // tail delete (no next run) = no-op for any mode.
        if (op.TimeMode != TimeMode.Keep && tailExists)
        {
            int anchorIndex = op.From > 0 ? op.From - 1 : 0; // head delete anchors to the original start
            if (!slot.TryTimeQAt(anchorIndex, out int anchorQ)) return TrackEditErrors.TimeAnchorMissing;
            if (!slot.TryTimeQAt(op.To + 1, out int nextQ)) return TrackEditErrors.TimeAnchorMissing;
            long shift = (nextQ - anchorQ) - op.GapSeconds!.Value; // same slot ⇒ same QBias, offsets suffice
            if (!result.ShiftTimePresent(tailStart, newCount, shift)) return TrackEditErrors.ValueOutOfRange;
        }

        // Distance junction (§2.3, all timeModes): keep the channel continuous across the cut.
        if (tailExists && !RebaseDistanceJunction(result, tailStart, newCount, op.From == 0))
            return TrackEditErrors.ValueOutOfRange;
        state.Replace(op.Slot, result);
        return null;
    }

    private static string? ApplySplit(ReplayState state, SplitAtOp op)
    {
        if (!state.TryGetLive(op.Slot, out var slot, out string? notLive)) return notLive;
        if (op.Index < 1 || op.Index > slot.Count - 2) return TrackEditErrors.SplitIndexOutOfBounds;
        if (state.LiveCount >= TrackEditLimits.MaxSlots) return TrackEditErrors.SlotCapExceeded; // +1 net slot

        var head = slot.Slice(0, op.Index, slot.Origin);                        // shared boundary point
        var tail = slot.Slice(op.Index, slot.Count - 1, new SlotOrigin(null, op.Slot));
        if (!RebaseNewHead(tail)) return TrackEditErrors.ValueOutOfRange;       // tail distance → 0; head untouched
        state.Replace(op.Slot, head);
        state.AddSlot(tail);
        return null;
    }

    private static string? ApplyAppend(ReplayState state, AppendOp op)
    {
        if (!state.TryGetLive(op.Target, out var target, out string? tErr)) return tErr;
        if (!state.TryGetLive(op.Source, out var source, out string? sErr)) return sErr;
        if (op.Target == op.Source) return TrackEditErrors.SelfAppend;
        string? gapError = ValidateTimeMode(op.TimeMode, op.GapSeconds);
        if (gapError is not null) return gapError;

        var result = EditSlotBuffer.Concat(target, source, target.Origin);
        int srcStart = target.Count;
        int total = result.Count;

        long shift = 0;
        if (op.TimeMode != TimeMode.Keep)
        {
            if (!target.TryLastPresentTimeAbs(0, target.Count, out long anchorAbs)) return TrackEditErrors.TimeAnchorMissing;
            if (!source.TryFirstPresentTimeAbs(0, source.Count, out long nextAbs)) return TrackEditErrors.TimeAnchorMissing;
            shift = (nextAbs - anchorAbs) - op.GapSeconds!.Value;
        }
        // Rebase source time into target's QBias (q' = qSrc + srcBias − tgtBias) AND apply the gap shift,
        // as one subtract on the source run: q - ((tgtBias − srcBias) + shift). Long math, guarded.
        long timeAmount = (target.TimeQBias - source.TimeQBias) + shift;
        if (!result.ShiftTimePresent(srcStart, total, timeAmount)) return TrackEditErrors.ValueOutOfRange;

        if (!RebaseDistanceJunction(result, srcStart, total, newHead: false)) return TrackEditErrors.ValueOutOfRange;
        state.Replace(op.Target, result);
        state.Consume(op.Source);
        return null;
    }

    // ---- shared validation + distance helpers ----

    private static string? ValidateRange(int from, int to, int count)
    {
        if (from < 0 || from >= count || to < 0 || to >= count) return TrackEditErrors.RangeOutOfBounds;
        if (from > to) return TrackEditErrors.RangeInverted;
        return null;
    }

    private static string? ValidateTimeMode(TimeMode mode, long? gapSeconds)
    {
        if (mode != TimeMode.Keep && mode != TimeMode.Close && mode != TimeMode.Synthesize)
            return TrackEditErrors.TimeModeInvalid;
        if (mode == TimeMode.Keep) return null;
        if (gapSeconds is null) return TrackEditErrors.GapSecondsMissing;
        if (gapSeconds < 0 || gapSeconds > TrackEditLimits.MaxGapSeconds) return TrackEditErrors.GapSecondsOutOfRange;
        return null;
    }

    // New head: rebase the run so its first present distance = 0 (subtract that first value).
    private static bool RebaseNewHead(EditSlotBuffer buffer) =>
        !buffer.TryFirstPresentDistance(0, buffer.Count, out long first)
        || buffer.RebaseDistancePresent(0, buffer.Count, first);

    // Junction: shift the [start..end) run so its distance delta across the junction is 0. new-head form
    // (head delete) rebases to 0; interior/append form rebases against the preceding run's last present.
    private static bool RebaseDistanceJunction(EditSlotBuffer buffer, int start, int end, bool newHead)
    {
        if (newHead) return RebaseNewHead(buffer);
        if (!buffer.TryFirstPresentDistance(start, end, out long firstNext)) return true;   // no next present
        if (!buffer.TryLastPresentDistance(0, start, out long lastPrev)) return true;        // no prev present
        return buffer.RebaseDistancePresent(start, end, firstNext - lastPrev);
    }

    /// <summary>Mutable per-replay state: live slots by id, next id, loaded activity ids.</summary>
    private sealed class ReplayState
    {
        private readonly SortedDictionary<int, EditSlotBuffer> _live = new();
        private readonly HashSet<int> _loaded = new();
        private int _nextId;

        public ReplayState(IReadOnlyDictionary<int, DecodedTrack> sources) => Sources = sources;

        public IReadOnlyDictionary<int, DecodedTrack> Sources { get; }
        public int LiveCount => _live.Count;

        public bool MarkLoaded(int activityId) => _loaded.Add(activityId);

        public void AddSlot(EditSlotBuffer buffer) => _live[_nextId++] = buffer;

        public void Replace(int slotId, EditSlotBuffer buffer) => _live[slotId] = buffer;

        public void Consume(int slotId) => _live.Remove(slotId); // id stays < nextId ⇒ future ops see slotConsumed

        public bool TryGetLive(int slotId, out EditSlotBuffer buffer, out string? errorCode)
        {
            if (_live.TryGetValue(slotId, out buffer!)) { errorCode = null; return true; }
            errorCode = slotId >= 0 && slotId < _nextId ? TrackEditErrors.SlotConsumed : TrackEditErrors.SlotNotFound;
            return false;
        }

        public IReadOnlyList<ReplaySlot> ToSlots()
        {
            var slots = new List<ReplaySlot>(_live.Count);
            foreach (var (id, buffer) in _live)
                slots.Add(new ReplaySlot(id, buffer.Origin, buffer.ToDecodedTrack()));
            return slots;
        }
    }
}
