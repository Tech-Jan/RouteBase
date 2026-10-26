namespace RouteBaseWeb.Services.Races;

/// <summary>
/// What the results file PUBLISHED about an entrant's race — an authoritative fact, orthogonal to his
/// splits, not something to be re-derived from geometry.
///
/// The old model was a boolean derived from "has a split at the finish checkpoint", and the real data
/// inverts that rule in both directions. Black Forest bibs 2423, 2843, 2713 and 2569 are published DNF
/// AND carry a `cp_ziel` finish-mat time — map that column to Finish (the guesser encourages it; *Ziel*
/// is German for finish) and four DNFs are reported as finishers. Schauinsland publishes 113 entrants
/// with a status and no time at all, which the importer used to drop entirely.
///
/// Ten values, aligned with IOF XML v3's status enumeration (which mandates a status on EVERY result and
/// lists 15). The five we do not model are absorbed by <see cref="Unknown"/> plus the verbatim raw token,
/// which is honest; inventing a value we cannot populate is not. Every serious standard separates "did
/// not reach the line" from "reached it but does not count" (OTL, NotCompeting, unofficial finish, hors
/// classement), and that distinction is the reason a boolean could never be enough.
///
/// These are WIRE values: <see cref="RaceParticipantDto.FinishStatus"/> carries
/// <c>[WireValues(typeof(RaceFinishStatus))]</c>, so the OpenAPI document stamps them as an enum and
/// openapi-typescript renders a TS literal union. Adding a value here is therefore a compile error at
/// every consumer, not a filter that silently matches nothing.
///
/// ONLY <c>public const string</c> members may be added to this class — the schema transformer reflects
/// over exactly those to build the enum, so a stray const becomes a wire value.
/// </summary>
public static class RaceFinishStatus
{
    /// <summary>Reached the line and counts. The default reading of a rider with a finish split.</summary>
    public const string Finished = "finished";

    /// <summary>Started, did not reach the line. Held at his last anchor by the pacing model.</summary>
    public const string Dnf = "dnf";

    /// <summary>Entered, never started. NOT in the replay field at all — he is on the start list, not the course.</summary>
    public const string Dns = "dns";

    /// <summary>Reached the line, then removed from the results. Rides, does not rank.</summary>
    public const string Dsq = "dsq";

    /// <summary>Over the time limit — reached the line (or was stopped) outside the cutoff. Rides, does not rank.</summary>
    public const string Otl = "otl";

    /// <summary>Orienteering's mispunch: the course was not completed as set. Held like a DNF.</summary>
    public const string MissingPunch = "missingPunch";

    /// <summary>Rode the event but was never in the classification — hors classement, ausser Konkurrenz, a guest.</summary>
    public const string NotCompeting = "notCompeting";

    /// <summary>A result published subject to review. Counts until it does not.</summary>
    public const string Provisional = "provisional";

    /// <summary>Moved to another class/discipline after the fact. Counts, in whatever class he ended up in.</summary>
    public const string Moved = "moved";

    /// <summary>
    /// The file published a token we do not recognise — Schauinsland's merged <c>DNS/DNF</c> is the live
    /// case. Ranking and placement fall back to the derived rule, and the UI shows the RAW token rather
    /// than a label we would be making up.
    /// </summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// Reading a status token out of a results file. Separate from <see cref="RaceFinishStatus"/> because
/// that class's members are reflected over to build the wire enum — nothing but the values may live there.
/// </summary>
public static class RaceFinishStatusVocabulary
{
    /// <summary>Every legal wire value, in the order the UI should offer them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        RaceFinishStatus.Finished, RaceFinishStatus.Dnf, RaceFinishStatus.Dns, RaceFinishStatus.Dsq,
        RaceFinishStatus.Otl, RaceFinishStatus.MissingPunch, RaceFinishStatus.NotCompeting,
        RaceFinishStatus.Provisional, RaceFinishStatus.Moved, RaceFinishStatus.Unknown,
    ];

    public static bool IsValid(string? status) => status != null && All.Contains(status, StringComparer.Ordinal);

    /// <summary>
    /// The accepted spellings, across the languages these exports actually arrive in (DE/EN/FR/IT/NL/
    /// Nordic). ChronoTrack alone documents 24 accepted spellings of "bib"; a status column is no tidier.
    ///
    /// Ordinal-ignore-case throughout, never <c>ToUpper()</c> or a culture-sensitive compare: under a
    /// Turkish or Thai culture those change which strings are equal, and this table decides whether a
    /// rider's race counted.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // Finished
        ["finisher"] = RaceFinishStatus.Finished,
        ["finished"] = RaceFinishStatus.Finished,
        ["finish"] = RaceFinishStatus.Finished,
        ["fin"] = RaceFinishStatus.Finished,
        ["f"] = RaceFinishStatus.Finished,
        ["ok"] = RaceFinishStatus.Finished,
        ["classified"] = RaceFinishStatus.Finished,
        ["gewertet"] = RaceFinishStatus.Finished,
        ["im ziel"] = RaceFinishStatus.Finished,
        ["ziel"] = RaceFinishStatus.Finished,
        ["angekommen"] = RaceFinishStatus.Finished,
        ["arrive"] = RaceFinishStatus.Finished,
        ["arrivé"] = RaceFinishStatus.Finished,
        ["arrivato"] = RaceFinishStatus.Finished,
        ["s.t."] = RaceFinishStatus.Finished,      // "stesso tempo" — same time as the rider ahead
        ["st"] = RaceFinishStatus.Finished,
        ["fullfort"] = RaceFinishStatus.Finished,
        ["fullført"] = RaceFinishStatus.Finished,

        // Did not finish
        ["dnf"] = RaceFinishStatus.Dnf,
        ["did not finish"] = RaceFinishStatus.Dnf,
        ["aufg"] = RaceFinishStatus.Dnf,
        ["aufg."] = RaceFinishStatus.Dnf,
        ["aufgegeben"] = RaceFinishStatus.Dnf,
        ["abbruch"] = RaceFinishStatus.Dnf,
        ["abandon"] = RaceFinishStatus.Dnf,
        ["abandoned"] = RaceFinishStatus.Dnf,
        ["ritirato"] = RaceFinishStatus.Dnf,
        ["retired"] = RaceFinishStatus.Dnf,
        ["ret"] = RaceFinishStatus.Dnf,
        ["opgave"] = RaceFinishStatus.Dnf,
        ["brutet"] = RaceFinishStatus.Dnf,
        ["brutt"] = RaceFinishStatus.Dnf,
        ["utgatt"] = RaceFinishStatus.Dnf,

        // Did not start
        ["dns"] = RaceFinishStatus.Dns,
        ["did not start"] = RaceFinishStatus.Dns,
        ["ns"] = RaceFinishStatus.Dns,
        ["np"] = RaceFinishStatus.Dns,
        ["nicht gestartet"] = RaceFinishStatus.Dns,
        ["nicht angetreten"] = RaceFinishStatus.Dns,
        ["n.a."] = RaceFinishStatus.Dns,
        ["non partito"] = RaceFinishStatus.Dns,
        ["non partant"] = RaceFinishStatus.Dns,
        ["non-partant"] = RaceFinishStatus.Dns,
        ["niet gestart"] = RaceFinishStatus.Dns,
        ["ej start"] = RaceFinishStatus.Dns,
        ["ikke startet"] = RaceFinishStatus.Dns,

        // Disqualified
        ["dsq"] = RaceFinishStatus.Dsq,
        ["dq"] = RaceFinishStatus.Dsq,
        ["disq"] = RaceFinishStatus.Dsq,
        ["disqualified"] = RaceFinishStatus.Dsq,
        ["disqualifiziert"] = RaceFinishStatus.Dsq,
        ["disqualifié"] = RaceFinishStatus.Dsq,
        ["squalificato"] = RaceFinishStatus.Dsq,
        ["diskad"] = RaceFinishStatus.Dsq,

        // Over the time limit
        ["otl"] = RaceFinishStatus.Otl,
        ["over time limit"] = RaceFinishStatus.Otl,
        ["time limit"] = RaceFinishStatus.Otl,
        ["cutoff"] = RaceFinishStatus.Otl,
        ["cut off"] = RaceFinishStatus.Otl,
        ["zeitlimit"] = RaceFinishStatus.Otl,
        ["karenz"] = RaceFinishStatus.Otl,
        ["hd"] = RaceFinishStatus.Otl,             // hors délai
        ["hors delai"] = RaceFinishStatus.Otl,
        ["hors délai"] = RaceFinishStatus.Otl,
        ["ftm"] = RaceFinishStatus.Otl,            // fuori tempo massimo
        ["fuori tempo massimo"] = RaceFinishStatus.Otl,

        // Missing punch (orienteering / SportIdent)
        ["mp"] = RaceFinishStatus.MissingPunch,
        ["missing punch"] = RaceFinishStatus.MissingPunch,
        ["mispunch"] = RaceFinishStatus.MissingPunch,
        ["fehlstempel"] = RaceFinishStatus.MissingPunch,
        ["postenfehler"] = RaceFinishStatus.MissingPunch,
        ["felst"] = RaceFinishStatus.MissingPunch,

        // Not competing
        ["nc"] = RaceFinishStatus.NotCompeting,
        ["not competing"] = RaceFinishStatus.NotCompeting,
        ["hors classement"] = RaceFinishStatus.NotCompeting,
        ["ausser konkurrenz"] = RaceFinishStatus.NotCompeting,
        ["außer konkurrenz"] = RaceFinishStatus.NotCompeting,
        ["a.k."] = RaceFinishStatus.NotCompeting,
        ["fuori classifica"] = RaceFinishStatus.NotCompeting,
        ["guest"] = RaceFinishStatus.NotCompeting,
        ["gast"] = RaceFinishStatus.NotCompeting,

        // Provisional / unofficial
        ["provisional"] = RaceFinishStatus.Provisional,
        ["prov"] = RaceFinishStatus.Provisional,
        ["unofficial"] = RaceFinishStatus.Provisional,
        ["vorlaufig"] = RaceFinishStatus.Provisional,
        ["vorläufig"] = RaceFinishStatus.Provisional,

        // Moved up / transferred class
        ["moved"] = RaceFinishStatus.Moved,
        ["moved up"] = RaceFinishStatus.Moved,
        ["mu"] = RaceFinishStatus.Moved,
        ["transferred"] = RaceFinishStatus.Moved,
        ["umgemeldet"] = RaceFinishStatus.Moved,
    };

    /// <summary>
    /// A file's status token → a wire value.
    ///
    /// NULL for a blank cell, and that is a third answer, not a synonym for <see cref="RaceFinishStatus.Unknown"/>:
    /// blank means the file published no status, so the derived rule (has a split at the finish) still
    /// applies and the 4 races imported before this column existed need no backfill. <c>Unknown</c> means
    /// the file DID publish something and we could not read it — a different fact, and the reason the raw
    /// token is stored alongside.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var key = Collapse(raw);
        return Aliases.TryGetValue(key, out var status) ? status : RaceFinishStatus.Unknown;
    }

    /// <summary>Trimmed, with internal whitespace collapsed — <c>"  Nicht   gestartet "</c> is one token.</summary>
    private static string Collapse(string raw) =>
        string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
