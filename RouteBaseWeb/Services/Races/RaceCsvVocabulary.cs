namespace RouteBaseWeb.Services.Races;

/// <summary>
/// The canonical field ids a results file can be mapped onto. Wire values — the preview DTO names them
/// and the panel keys its pickers on them, so they are constants, never built from fragments.
/// </summary>
public static class RaceCsvFields
{
    public const string Name = "name";
    public const string Bib = "bib";
    public const string Category = "category";
    public const string Gender = "gender";
    public const string Club = "club";
    public const string Status = "status";
    public const string Finish = "finish";
    public const string NetTime = "netTime";
    public const string GunTime = "gunTime";
    public const string StartOffset = "startOffset";
    public const string Wave = "wave";
    public const string Discipline = "discipline";
    public const string Rank = "rank";
}

/// <summary>One canonical field: what to call it, whether it is required, and what files call it.</summary>
public sealed record RaceCsvFieldSpec(
    string Field, string Label, bool Required, string Hint, IReadOnlyList<string> Aliases);

/// <summary>
/// THE alias table — one entry per canonical field, with the spellings real exports use across
/// DE/EN/FR/IT/NL/Nordic.
///
/// It exists because there is no standard. Web research across ~27 platforms found only <c>Name</c>,
/// <c>Time</c> and <c>Place</c> to be near-universal; ChronoTrack alone documents 24 accepted spellings of
/// "bib", 22 of chip time and 16 of division. Six short hint lists hard-coded in the browser were never
/// going to cover that.
///
/// Single-sourced on purpose: the preview DTO serves this table to the panel, so the guesser, the picker
/// labels and (Phase 3) the template writer cannot drift apart. The browser holds no copy.
/// </summary>
public static class RaceCsvVocabulary
{
    public static readonly IReadOnlyList<RaceCsvFieldSpec> Fields =
    [
        new(RaceCsvFields.Name, "Name", true,
            "The rider or team. Required — an entrant with no name is not an entrant.",
            ["name", "athlete", "participant", "rider", "team", "teamname", "team name", "runner",
             "nom", "nome", "naam", "namn", "nachname", "full name", "fullname", "lastname, firstname"]),

        new(RaceCsvFields.Finish, "Finish time", true,
            "Elapsed from the start. Under per-section times this is the PUBLISHED total instead — a cross-check, not the clock.",
            ["finish_time", "finishtime", "finish time", "finish", "time", "total", "totaltime",
             "total time", "gesamtzeit", "endzeit", "zielzeit", "zeit", "laufzeit", "temps", "tempo",
             "tijd", "tid", "sluttid", "elapsed", "elapsed time", "result", "cp_ziel", "ziel"]),

        new(RaceCsvFields.Bib, "Bib", false,
            "Start number. Used as the identity key when present — two riders can share a name, not a bib.",
            ["bib", "bibnumber", "bib number", "bib no", "number", "no", "nr", "startnr", "startnr.",
             "startnummer", "start no", "start nr", "start number", "dossard", "pettorale", "nr.",
             "race number", "racenumber", "id", "chip", "chipnr"]),

        new(RaceCsvFields.Category, "Category", false,
            "Age group / class. Powers the Division filter, so it is what paints a group.",
            ["category", "kategorie", "class", "klasse", "division", "div", "ak", "agegroup",
             "age group", "age_group", "categorie", "catégorie", "categoria", "klass", "group"]),

        new(RaceCsvFields.Gender, "Gender", false,
            "M/F/X as the file spells it — stored verbatim, filtered as a facet.",
            ["gender", "sex", "geschlecht", "geschl", "sexe", "sesso", "geslacht", "kon", "kön", "m/f", "m/w"]),

        new(RaceCsvFields.Club, "Club", false,
            "Club, team or town. A facet, and usually the most useful one to paint by.",
            ["club", "verein", "team", "teamname", "society", "society/club", "association",
             "club_loc", "clublocation", "club/ort", "ort", "city", "hometown", "nation", "country"]),

        new(RaceCsvFields.Status, "Status", false,
            "The published result (Finisher / DNF / DNS / Aufg.). Mapping it keeps rows that have a status and no time, and lets a published DNF outrank a finish-mat time.",
            ["status", "result", "resultat", "résultat", "ergebnis", "wertung", "remark", "remarks",
             "bemerkung", "comment", "state", "finished", "classification", "class_status"]),

        new(RaceCsvFields.NetTime, "Net (chip) time", false,
            "From the rider crossing the start mat. With Gun time mapped too, the difference IS his start offset.",
            ["net", "nettime", "net time", "net_time", "chip", "chiptime", "chip time", "chip_time",
             "netto", "nettozeit", "nettime", "temps net", "tempo netto", "nettotid"]),

        new(RaceCsvFields.GunTime, "Gun (clock) time", false,
            "From the gun. With Net time mapped too, the difference IS his start offset.",
            ["gun", "guntime", "gun time", "gun_time", "clock", "clocktime", "clock time", "clock_time",
             "brutto", "bruttozeit", "temps reel", "temps réel", "tempo lordo", "bruttotid", "official time"]),

        new(RaceCsvFields.StartOffset, "Start offset", false,
            "Seconds (or h:mm:ss) after the gun that this rider started. Wave starts and time trials.",
            ["start_offset", "startoffset", "start offset", "offset", "starttime", "start time",
             "start_time", "startzeit", "startdelay", "delay", "heure de depart", "orario di partenza"]),

        new(RaceCsvFields.Wave, "Wave / start block", false,
            "The block label, when the file names the wave rather than timing it.",
            ["wave", "start_block", "startblock", "start block", "block", "welle", "startwelle",
             "vague", "batch", "corral", "startgroup", "start group"]),

        new(RaceCsvFields.Discipline, "Discipline", false,
            "For an event running several races off one start list — 11 of them in the Schauinsland file.",
            ["discipline", "disziplin", "event", "race", "distance", "strecke", "course", "competition",
             "wettbewerb", "type", "raceclass"]),

        new(RaceCsvFields.Rank, "Rank", false,
            "The published place. Read for reference only — the replay ranks on the clock it races.",
            ["rank", "rank_overall", "rankoverall", "place", "placing", "position", "pos", "platz",
             "rang", "classement", "posizione", "plaats", "plats", "overall"]),
    ];

    /// <summary>
    /// The best column for a field, or null. Exact alias match first across ALL aliases, then a substring
    /// pass — a confidently wrong guess is worse than none, and `finish_time` must beat `finish_seconds`
    /// rather than whichever happens to be scanned first.
    /// </summary>
    public static string? Guess(IReadOnlyList<string> header, string field)
    {
        var spec = Fields.FirstOrDefault(f => f.Field == field);
        if (spec == null) return null;

        foreach (var alias in spec.Aliases)
        {
            var exact = header.FirstOrDefault(h => Collapse(h).Equals(alias, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }
        foreach (var alias in spec.Aliases)
        {
            // Two characters is not a hint, it is a coincidence: `no` would match `nome`, `nation` and
            // `kanton`. Substring matching earns its keep on `Startnr.`, not on abbreviations.
            if (alias.Length < 3) continue;
            var partial = header.FirstOrDefault(h => Collapse(h).Contains(alias, StringComparison.OrdinalIgnoreCase));
            if (partial != null) return partial;
        }
        return null;
    }

    /// <summary>Trimmed with internal whitespace collapsed — <c>"Start  Nr"</c> and <c>"Start Nr"</c> are one name.</summary>
    private static string Collapse(string header) =>
        string.Join(' ', header.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
