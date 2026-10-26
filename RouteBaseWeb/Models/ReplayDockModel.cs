namespace RouteBaseWeb.Models;

/// <summary>
/// One replay's bottom dock: [grab handle][transport bar][chart strip], rendered by
/// Pages/Shared/_ReplayDock.cshtml. The activity, segment and race replays each hand-rolled this markup
/// and drifted apart — the race's grip was not even attached to its bar, and its seek slider quietly lost
/// the focus outline the other two have.
///
/// Every id is passed WHOLE from the call site (never assembled from a prefix), so `raceReplaySeek` still
/// greps back to Pages/Races/_RaceHero.cshtml and its TypeScript in one search. That is the reason this is
/// a dozen string properties rather than one prefix.
/// </summary>
public record ReplayDockModel(
    string DockId,
    string HandleId,
    string StripId,
    string ControlsId,
    string PlayPauseId,
    string SeekId,
    string CurrentTimeId,
    string SpeedSlowerId,
    string SpeedLabelId,
    string SpeedFasterId,
    string SpeedSelectId)
{
    /// <summary>Extra class on the strip — the race stacks two canvases inside its own flex column.</summary>
    public string StripClass { get; init; } = "";

    /// <summary>
    /// Single-canvas strip: the partial draws one <c>.replay-dock-canvas</c> itself. Leave null when
    /// <see cref="StripPartial"/> supplies the content instead; exactly one of the two applies.
    /// </summary>
    public string? CanvasId { get; init; }

    public string? CanvasAriaLabel { get; init; }

    /// <summary>Absolute path of a partial rendering the strip's contents, for a strip that is not one canvas.</summary>
    public string? StripPartial { get; init; }

    /// <summary>Model handed to <see cref="StripPartial"/> (a nested partial does not inherit ours).</summary>
    public object? StripPartialModel { get; init; }

    /// <summary>The segment replay's time ↔ % sync toggle, left of the speed stepper. Null renders no button.</summary>
    public string? ModeButtonId { get; init; }

    /// <summary>The segment replay's full-res sharpening progress text. Null renders nothing.</summary>
    public string? LoadingStatusId { get; init; }

    /// <summary>
    /// What the speed stepper reads before its ladder is built. "1x" is the truth for the replays that
    /// start there; the race scales its ladder to the event's duration, so it starts on a placeholder
    /// rather than claim a speed it is not about to run at.
    /// </summary>
    public string SpeedLabelText { get; init; } = "1x";

    /// <summary>
    /// The replay is already running when the dock appears (activity, segment), so the button starts on
    /// the pause icon. The race replay opens paused and starts on play.
    /// </summary>
    public bool StartsPlaying { get; init; }

    /// <summary>
    /// Hide the dock and the transport until JS shows them — the activity replay's dock exists only during
    /// playback. It is inline `display:none` rather than a class because activity-replay-ui.ts drives both
    /// elements' `style.display` directly.
    /// </summary>
    public bool HiddenUntilPlayback { get; init; }

    /// <summary>
    /// Hide the grip until a profile actually mounts (activity, segment): a ride with no relief gets no
    /// strip, and a grip that pulls up nothing is worse than no grip at all.
    /// </summary>
    public bool HandleHiddenUntilMounted { get; init; }
}
