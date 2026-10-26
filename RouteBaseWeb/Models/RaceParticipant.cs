using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace RouteBaseWeb.Models;

/// <summary>The two participant kinds. Constants, never string literals at call sites (grep-ability).</summary>
public static class RaceParticipantKind
{
    /// <summary>Backed by a real <see cref="Activity"/> (own or a friend's). Rendered from ITS OWN GPS.</summary>
    public const string Real = "real";

    /// <summary>Backed only by imported split times. Rendered by interpolating the COURSE at a modelled chainage.</summary>
    public const string Simulated = "simulated";
}

/// <summary>
/// One entrant in a race.
///
/// The load-bearing distinction is where the marker's POSITION comes from:
///
///   real      → the participant's own track content. His marker rides HIS line, so a wider turn, a
///               wrong turn or a detour is visible. Chainage is DERIVED (SegmentChainage DTW) and used
///               only as a scalar for ranking / gaps / checkpoint crossings — it never moves the marker.
///               Projecting a real rider onto the course would erase exactly what he was added to show;
///               SegmentChainage is actively hostile to it (it re-interpolates off-course spans so they
///               "meet back up smoothly" — a wrong turn is the first casualty).
///
///   simulated → no geometry of its own. The pacing model produces chainage-over-time from the split
///               times, and position is the course interpolated at that chainage.
///
/// This is also what makes 500 entrants affordable: real participants are few and rich (own point
/// arrays), simulated are many and thin (a shared course grid + one time column each).
///
/// <see cref="TrackId"/> is re-authorized against IVisibilityService on every read, never trusted from
/// the row — a revoked friendship must drop the participant even though the stored id is unchanged
/// (the match_owner_ids precedent on shape segments).
/// </summary>
[Table("Race_Participant")]
public class RaceParticipant
{
    [Key]
    [Column("participant_ID")]
    public ParticipantId ParticipantId { get; set; }

    [Column("race_ID")]
    public RaceId RaceId { get; set; }

    /// <summary>One of <see cref="RaceParticipantKind"/>.</summary>
    [Column("kind")]
    [MaxLength(20)]
    public string Kind { get; set; } = RaceParticipantKind.Simulated;

    [Column("display_name")]
    [MaxLength(255)]
    public string? DisplayName { get; set; }

    [Column("bib")]
    [MaxLength(50)]
    public string? Bib { get; set; }

    [Column("category")]
    [MaxLength(100)]
    public string? Category { get; set; }

    [Column("gender")]
    [MaxLength(20)]
    public string? Gender { get; set; }

    [Column("club")]
    [MaxLength(255)]
    public string? Club { get; set; }

    /// <summary>Set iff <see cref="Kind"/> is real. Cascades: no track ⇒ no real participant.</summary>
    [Column("track_id")]
    public TrackId? TrackId { get; set; }

    /// <summary>
    /// The rider's marker colour, or null for the default (real = yellow, simulated = blue).
    ///
    /// Persisted rather than session-local because painting a group IS the race's presentation, not one
    /// viewer's scratch state: the results table filters a group (gender, division, club — filters
    /// compose) and paints it, and everyone who opens the race sees it.
    ///
    /// A validated `#rrggbb` string, never free text — it reaches a MapLibre <c>['get','color']</c>
    /// expression and a CSS swatch. See <see cref="Services.Races.RaceColors"/>.
    /// </summary>
    [Column("color")]
    [MaxLength(32)]
    public string? Color { get; set; }

    /// <summary>
    /// Draw this rider bigger, and in FRONT of the field. Null means no — the same nullable-means-off
    /// shape as <see cref="StartOffsetSeconds"/>, and for the same schema reason (see
    /// <c>DbSchemaManager.EnsureRaceTablesAsync</c>: SQLite cannot tighten an ALTER-added column).
    ///
    /// A SECOND presentation channel beside <see cref="Color"/>, not a variant of it, because colour
    /// alone stops working at 508 entrants: a painted group is still the same 4px dot in the same
    /// haystack, and it can be covered by any rider drawn after it. Size plus draw order is what makes
    /// "watch THESE three" legible — see race-markers.ts, where both read off this one flag.
    ///
    /// Persisted for the same reason the colour is: emphasis IS the race's presentation, not one
    /// viewer's scratch state.
    /// </summary>
    [Column("emphasised")]
    public bool? Emphasised { get; set; }

    /// <summary>
    /// The user hid this rider's CALL-OUT: no name label on the map, no pinned row in the follow
    /// sidebar, no line in the speed chart. His dot still rides the course.
    ///
    /// Written in the negative because only the non-default state is ever stored — a real rider is
    /// called out by definition (his line is the true one), so "called out" is null and switching it
    /// off is the fact worth persisting. Same nullable-means-default shape as
    /// <see cref="Emphasised"/>, and for the same schema reason.
    ///
    /// Deliberately NOT the marker's size or stroke: those say "this position is a fact, not a model",
    /// which is a claim about the data and not a claim about how interesting the rider is.
    /// </summary>
    [Column("callout_off")]
    public bool? CalloutOff { get; set; }

    /// <summary>Per-rider PRNG seed, so the model's jitter is deterministic across replays.</summary>
    [Column("seed")]
    public int Seed { get; set; }

    /// <summary>
    /// The finish time the results file PUBLISHED, as imported. Not what the rider is replayed on: under
    /// per-leg times the accumulated leg sum is the finish (see <see cref="RaceLegAccumulator"/>), and the
    /// two disagree whenever a forced start overlapped two legs.
    ///
    /// Stored so the disagreement stays inspectable after import — the results table shows both and
    /// colours the gap. Without it the published number is lost at import and the discrepancy becomes
    /// unauditable. Null when the file carried no total.
    /// </summary>
    [Column("official_finish_seconds")]
    public double? OfficialFinishSeconds { get; set; }

    /// <summary>
    /// What the results file PUBLISHED about his race, normalized — one of
    /// <see cref="Services.Races.RaceFinishStatus"/>. NULL means the file said nothing, and that is a
    /// distinct third state, not a default: the derived rule ("has a split at the finish checkpoint")
    /// still governs, which is why the races imported before this column existed need no backfill.
    ///
    /// A published status OVERRIDES the derived one. Black Forest publishes four riders as DNF who also
    /// carry a finish-mat time; without this column the replay called all four finishers.
    /// </summary>
    [Column("finish_status")]
    [MaxLength(20)]
    public string? FinishStatus { get; set; }

    /// <summary>
    /// The file's original token, verbatim — <c>Aufg.</c>, <c>DNS/DNF</c>, <c>hors délai</c>.
    ///
    /// Kept because normalization is lossy exactly where it matters: Schauinsland publishes the merged
    /// token <c>DNS/DNF</c>, which normalizes to <c>Unknown</c> and would otherwise be unrecoverable. It
    /// is also the only way to audit a normalization mistake without re-importing the file.
    /// </summary>
    [Column("finish_status_raw")]
    [MaxLength(64)]
    public string? FinishStatusRaw { get; set; }

    /// <summary>
    /// Seconds after the gun that this entrant actually started. NULL means 0 — a mass start.
    ///
    /// Staggered starts are the norm, not an edge case: Schauinsland is an individual time trial with five
    /// blocks spanning 10:00–14:00, Black Forest publishes 47 distinct chip-vs-gun offsets, and Ragnar UK
    /// 2019 has 19 distinct team start times. Replaying 1,274 riders leaving together is a picture of a
    /// race that did not happen.
    ///
    /// His SPLITS stay measured from his OWN start — that is what a results file publishes and what the
    /// classification is decided on. This column is what lets the replay put him on the road at the right
    /// moment (see race-build.ts's gun-time basis) without rewriting a single time.
    /// </summary>
    [Column("start_offset_seconds")]
    public double? StartOffsetSeconds { get; set; }

    /// <summary>
    /// The start block's own label, as the file published it — <c>Startblock 1: 10 Uhr bis 11 Uhr</c>.
    ///
    /// Stored SEPARATELY from the offset because it usually carries no machine-readable time: the label is
    /// what the user recognises when typing one offset per wave, and it is the only thing linking the
    /// riders that share it.
    /// </summary>
    [Column("wave")]
    [MaxLength(100)]
    public string? Wave { get; set; }

    /// <summary>
    /// Which race within the event this entrant rode — the Schauinsland file runs ELEVEN off one start
    /// list, and its rank restarts per discipline.
    ///
    /// A participant-level field, not a <c>Race.type</c>: the discipline is a property of the ENTRANT, and
    /// a race-level one would be decoration that could not express the file we actually have. Behaves
    /// exactly like <see cref="Category"/> — a facet to filter and paint by, stored verbatim.
    /// </summary>
    [Column("discipline")]
    [MaxLength(100)]
    public string? Discipline { get; set; }

    /// <summary>Back-reference — [JsonIgnore] breaks the Race → Participants → Race serialization cycle
    /// that EF's relationship fix-up otherwise creates. See RaceCheckpoint.Race.</summary>
    [JsonIgnore]
    public virtual Race? Race { get; set; }
    public virtual ICollection<RaceSplitTime> SplitTimes { get; set; } = new List<RaceSplitTime>();
}
