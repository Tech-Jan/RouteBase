using System.Collections.Concurrent;

namespace RouteBaseWeb.Services.Queue;

/// <summary>
/// Shared per-user progress state for background operations (CR5-01 part 2). Owns the state map
/// and the lifecycle skeleton the four progress classes used to copy by hand:
/// TryStart/ForceStart/Complete/Fail/Acknowledge/IsRunning/SetQueueInfo over
/// Phase/Error/StartedAt/CompletedAt/QueuePosition/QueueTotal. A feature progress class composes
/// one instance and keeps its counters/results in <typeparamref name="TExtra"/>; Mutate/Read run
/// under the same per-user lock, so feature snapshots stay consistent with the lifecycle fields.
/// </summary>
public sealed class UserProgressCore<TExtra> where TExtra : class, new()
{
    /// <summary>Per-user state: shared lifecycle fields + the feature's extension state.
    /// Only ever touched under the per-user lock held by the core's methods.</summary>
    public sealed class Entry
    {
        internal readonly Lock Lock = new();
        public bool IsRunning;
        public string Phase = "";
        public string? Error;
        public DateTimeOffset? StartedAt;
        public DateTimeOffset? CompletedAt;
        public int QueuePosition;
        public int QueueTotal;
        public TExtra Extra { get; } = new();
    }

    private readonly ConcurrentDictionary<string, Entry> _states = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public UserProgressCore(TimeProvider clock) => _clock = clock;

    private Entry Get(string userId) => _states.GetOrAdd(userId, _ => new Entry());

    /// <summary>Start unless already running. Queue fields are deliberately NOT reset —
    /// the worker stamps them per dequeued item (per-user burst position/total, QW-05).</summary>
    public bool TryStart(string userId, string startPhase, Action<TExtra> resetExtra)
    {
        var s = Get(userId);
        lock (s.Lock)
        {
            if (s.IsRunning) return false;
            StartLocked(s, startPhase, resetExtra);
            return true;
        }
    }

    public void ForceStart(string userId, string startPhase, Action<TExtra> resetExtra)
    {
        var s = Get(userId);
        lock (s.Lock) { StartLocked(s, startPhase, resetExtra); }
    }

    private void StartLocked(Entry s, string startPhase, Action<TExtra> resetExtra)
    {
        s.IsRunning = true;
        s.Phase = startPhase;
        s.Error = null;
        s.StartedAt = _clock.GetUtcNow();
        s.CompletedAt = null;
        resetExtra(s.Extra);
    }

    public void SetQueueInfo(string userId, int position, int total)
    {
        var s = Get(userId);
        lock (s.Lock)
        {
            s.QueuePosition = position;
            s.QueueTotal    = total;
        }
    }

    /// <summary>Mutate feature state (and, where the feature needs it, Phase) under the lock.</summary>
    public void Mutate(string userId, Action<Entry> mutate)
    {
        var s = Get(userId);
        lock (s.Lock) { mutate(s); }
    }

    /// <summary>Finish successfully; <paramref name="mutateExtra"/> lets a feature stamp final
    /// counters in the same locked transition (e.g. import's imported/skipped/failed).</summary>
    public void Complete(string userId, Action<TExtra>? mutateExtra = null)
    {
        var s = Get(userId);
        lock (s.Lock)
        {
            s.IsRunning = false;
            s.Phase = "Completed";
            s.CompletedAt = _clock.GetUtcNow();
            mutateExtra?.Invoke(s.Extra);
        }
    }

    public void Fail(string userId, string error)
    {
        var s = Get(userId);
        lock (s.Lock)
        {
            s.IsRunning = false;
            s.Phase = "Failed";
            s.Error = error;
            s.CompletedAt = _clock.GetUtcNow();
        }
    }

    /// <summary>Clear a finished state so the toast stops re-showing it on the next poll/page load.</summary>
    public void Acknowledge(string userId)
    {
        if (!_states.TryGetValue(userId, out var s)) return;
        lock (s.Lock)
        {
            if (!s.IsRunning && (s.Phase == "Completed" || s.Phase == "Failed"))
                s.Phase = "";
        }
    }

    public bool IsRunning(string userId)
    {
        if (!_states.TryGetValue(userId, out var s)) return false;
        lock (s.Lock) { return s.IsRunning; }
    }

    /// <summary>Snapshot under the lock. Unknown users get <paramref name="idle"/> without
    /// allocating state (GetStatus must not create entries for arbitrary user ids).</summary>
    public TSnapshot Read<TSnapshot>(string userId, Func<Entry, TSnapshot> snapshot, TSnapshot idle)
    {
        if (!_states.TryGetValue(userId, out var s)) return idle;
        lock (s.Lock) { return snapshot(s); }
    }
}
