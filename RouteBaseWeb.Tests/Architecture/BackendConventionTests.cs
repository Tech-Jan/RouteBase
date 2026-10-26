using RouteBaseWeb.Constants;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace RouteBaseWeb.Tests.Architecture;

/// <summary>
/// Two backend conventions that used to live only in prose (the deleted BACKEND-*.md docs) and were
/// enforced by nobody, so they could drift silently. Each caused a real bug once; each has a crisp,
/// greppable definition, so it belongs in a gate rather than a document a human has to remember to read.
///
/// Sibling of <see cref="LayeringTests"/> — same shape (walk the product tree, vacuity-guard the walk,
/// no exemption list). The first exemption added to either is the moment the gate stops meaning anything.
/// </summary>
public class BackendConventionTests
{
    private static bool IsComment(string trimmed) => ProductTree.IsComment(trimmed);

    /// <summary>
    /// Async all the way — no sync-over-async in product code. The convention forbids <c>.Wait()</c> /
    /// <c>.GetAwaiter().GetResult()</c> because it caused a real bug (a blocking call dropped SQLite
    /// temp tables mid-analysis), and until now the rule was enforced by nobody.
    ///
    /// Matches only the two UNAMBIGUOUS blocking markers. Plain <c>.Result</c> is deliberately NOT
    /// matched: MVC's <c>ActionContext.Result</c> / <c>ModelBindingResult.Result</c> and
    /// <c>GeoTimeZone</c>'s <c>TimeZoneResult.Result</c> (a string property, not <c>Task.Result</c>)
    /// are all legitimate and would drown the signal in false positives. Blocking on a Task shows up
    /// as <c>.Wait()</c> or <c>.GetAwaiter().GetResult()</c>; those are what this catches.
    /// Product code only — a blocking call inside a synchronous test fixture is fine.
    /// </summary>
    [Fact]
    public void Product_code_never_blocks_on_async()
    {
        var files = ProductCsFiles(out var productRoot);

        Assert.True(
            files.Count > 100,
            $"async-blocking walk found only {files.Count} product files under {productRoot} — the "
                + "project root is wrong. Fix the walk, do not weaken the test.");

        var violations = new List<string>();
        foreach (var file in files)
        {
            var n = 0;
            foreach (var line in File.ReadLines(file))
            {
                n++;
                var t = line.TrimStart();
                if (IsComment(t))
                {
                    continue;
                }

                if (t.Contains(".GetAwaiter().GetResult()", StringComparison.Ordinal)
                    || t.Contains(".Wait()", StringComparison.Ordinal))
                {
                    violations.Add($"{Path.GetFileName(file)}:{n}: {t}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "SYNC-OVER-ASYNC — product code blocks on a Task. Make the call chain async all the way "
                + "(a blocking wait once dropped SQLite temp tables mid-analysis). Do NOT add an "
                + "exemption:\n" + string.Join("\n", violations));
    }

    /// <summary>
    /// Every API response body is a NAMED DTO, never an anonymous <c>new { ... }</c>. An unnamed
    /// contract can't be grepped, typed, or pinned by a codegen drift gate — and the one untyped
    /// contract in a slice was the one that silently lost data (LV-01).
    ///
    /// Matches only the unambiguous body positions — the value handed to a result producer:
    /// <c>Ok(new {</c>, <c>Json(new {</c>, <c>StatusCode(nnn, new {</c>,
    /// <c>new ObjectResult(new {</c>, <c>new JsonResult(new {</c>. Deliberately does NOT flag
    /// <c>CreatedAtAction/AcceptedAtAction(..., new { id }, ...)</c> (that <c>new {}</c> is the
    /// route-values arg, not the body) or <c>.Select(x => new {})</c> (an internal projection) —
    /// both are legitimate and both exist in Api/ today.
    /// </summary>
    [Fact]
    public void Api_response_bodies_are_named_DTOs_not_anonymous_objects()
    {
        // Whole product tree since 2026-08-01 (was Api/ only): Hubs/, Pages/ handlers and any future
        // dir are response producers too, and the pin was verified 0 across the tree before widening.
        var files = ProductCsFiles(out var productRoot);

        Assert.True(
            files.Count > 100,
            $"named-DTO walk found only {files.Count} files under {productRoot} — the walk is wrong. "
                + "Fix the walk, do not weaken the test.");

        // Anonymous object in a response-BODY position (not route-values, not a projection).
        // `\bCreated\s*\(` cannot match `CreatedAtAction(` — the route-values carve-out survives.
        var anonBody = new Regex(
            @"(\bOk|\bJson|\bBadRequest|\bNotFound|\bConflict|\bAccepted|\bCreated|\bUnauthorized"
                + @"|\bUnprocessableEntity|\bProblem|new ObjectResult|new JsonResult)\s*\(\s*new\s*\{"
                + @"|\bStatusCode\s*\(\s*[0-9A-Za-z_.]+\s*,\s*new\s*\{",
            RegexOptions.Compiled, RegexDefaults.MatchTimeout);

        var violations = new List<string>();
        foreach (var file in files)
        {
            var n = 0;
            foreach (var line in File.ReadLines(file))
            {
                n++;
                var t = line.TrimStart();
                if (IsComment(t))
                {
                    continue;
                }

                if (anonBody.IsMatch(t))
                {
                    violations.Add($"{Path.GetFileName(file)}:{n}: {t}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "ANONYMOUS RESPONSE BODY — an endpoint returns an unnamed `new { ... }`. Give it a named "
                + "record DTO so it is greppable, typed, and pinned by the codegen drift gate (an "
                + "untyped body silently lost data once, LV-01):\n" + string.Join("\n", violations));
    }

    // ── Type-quality pass sweeps (2026-08-01). Each pin is PARITY: count must equal the pin, both
    // directions — grew means fix the new site, shrank means lower the pin in the same commit. ──

    /// <summary>
    /// Closed-set vocabulary literals, counted per vocabulary with a path filter that keeps
    /// colliding spellings apart ("pending" is heatmap AND friendship — different files). Each
    /// count shrinks as its D-slice swaps literals for the const home. cshtml counted separately:
    /// Razor-side literals fail visibly in UI, so they migrate last (D3).
    /// </summary>
    [Fact]
    public void Closed_set_string_literal_counts_match_their_pins()
    {
        // (vocabulary, value regex, extension, path-must-contain (null = anywhere), pinned count)
        var table = new (string Name, string Rx, string Ext, string? PathFilter, int Pin)[]
        {
            ("visibility-cs", "\"(private|friends|public)\"", ".cs", null, 7),    // D3 floor: 3 Visibilities defs, [Column("friends")] (a column NAME), a trailing comment, 2 RaceEditPolicy defs (its own vocabulary)
            ("visibility-cshtml", "\"(private|friends|public)\"", ".cshtml", null, 0),   // D3: all 45 swapped to consts/@-interpolation
            ("recording-status-cshtml", "\"(recording|paused|stopped|finalized|finalize_failed|abandoned)\"", ".cshtml", null, 11),
            ("heatmap-status", "\"(pending|running|done|stale|failed)\"", ".cs", "Heatmap", 5),   // D1: the 5 const definitions
            ("friendship-status", "\"(pending|accepted|declined|blocked)\"", ".cs", "Friend", 4),   // D2: the 4 const definitions
            ("segment-kind", "\"(polyline|shapes?)\"", ".cs", "Segment", 4),    // D2/F2b: 2 const defs + the [HttpPost("shapes")] ROUTE + the JsonPropertyName("shapes") KEY — different vocabularies, same spelling
            ("edit-session-status", "\"(active|saved)\"", ".cs", "EditSession", 2),   // D1: the 2 const definitions
            ("photo-source", "\"(exif|manual|interpolated)\"", ".cs", "Photo", 4),   // D2: the const definitions (incl. Assignments.Manual)
            ("import-outcome", "\"(imported|importing|skipped|in-trash|in-work)\"", ".cs", "Import", 10),
        };

        var failures = new List<string>();
        foreach (var (name, rx, ext, pathFilter, pin) in table)
        {
            var re = new Regex(rx, RegexOptions.Compiled, RegexDefaults.MatchTimeout);
            var sites = new List<string>();
            var files = ext == ".cs" ? ProductCsFiles(out _) : ProductTree.ProductCshtmlFiles(out _);
            foreach (var file in files)
            {
                if (pathFilter is not null && !file.Contains(pathFilter, StringComparison.Ordinal)) continue;
                var n = 0;
                foreach (var line in File.ReadLines(file))
                {
                    n++;
                    var t = line.TrimStart();
                    if (ext == ".cs" && IsComment(t)) continue;
                    for (var i = 0; i < re.Matches(line).Count; i++)
                        sites.Add($"{Path.GetFileName(file)}:{n}");
                }
            }
            if (sites.Count != pin)
                failures.Add($"{name}: {sites.Count} literals, pin says {pin}. " +
                    (sites.Count > pin
                        ? "New raw literal of a closed set — use the const home instead:\n    "
                        : "Shrunk — lower the pin in this commit:\n    ")
                    + string.Join("\n    ", sites));
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// Unit-suffix identifier inventory. `…Metres` is the minority spelling (new identifiers use
    /// `Meters`; ~50 WIRE properties are frozen on their current spelling and are being unified
    /// internally by H1/J1). `…Ms` is the dangerous one: on the wire it always means METRES PER
    /// SECOND (SpeedAvgMs…), while ElapsedMs-style names mean milliseconds — a new `Ms` identifier
    /// must be added here with a unit note, or named unambiguously (`Millis`, `Mps`).
    /// </summary>
    [Fact]
    public void Unit_suffix_identifier_inventory_is_pinned()
    {
        // H1 renamed every INTERNAL identifier to Meters; what remains is exactly the wire-frozen
        // spellings (serialized DTO members — J1 is the wire rename that will empty this).
        // Empty and terminal since J1: H1 unified every internal identifier, J1 renamed the four
        // remaining WIRE props (trash/version/race-replay DTOs, tsc-guided regen — D32 precedent).
        string[] metresPin = [];
        string[] msPin = [
            "AvgSpeedMs",           // m/s (wire, frozen)
            "KmhToMs",              // km/h → m/s conversion factor
            "MaxPlausibleSpeedMs",  // m/s
            "ParseSubsecondMs",     // milliseconds (parser internals)
            "SpeedAvgMs",           // m/s (wire, frozen)
            "SpeedMaxMs",           // m/s (wire, frozen)
            "avgSpeedMs",           // m/s (local)
        ];

        var metres = new SortedSet<string>(StringComparer.Ordinal);
        var ms = new SortedSet<string>(StringComparer.Ordinal);
        var rxMetres = new Regex(@"\b([A-Za-z_]\w*Metres)\b", RegexOptions.Compiled, RegexDefaults.MatchTimeout);
        var rxMs = new Regex(@"\b([A-Za-z_]\w*[a-z0-9]Ms)\b", RegexOptions.Compiled, RegexDefaults.MatchTimeout);
        foreach (var file in ProductCsFiles(out _))
        {
            var s = File.ReadAllText(file);
            foreach (Match m in rxMetres.Matches(s)) metres.Add(m.Groups[1].Value);
            foreach (Match m in rxMs.Matches(s)) ms.Add(m.Groups[1].Value);
        }

        Assert.True(metres.SetEquals(metresPin),
            "`…Metres` identifier set changed — new identifiers use `Meters`; update the pin only for "
            + "renames/removals:\n  now: " + string.Join(", ", metres));
        Assert.True(ms.SetEquals(msPin),
            "`…Ms` identifier set changed — the suffix is ambiguous (m/s vs milliseconds); prefer "
            + "`Mps`/`Millis` for new names, or add here WITH a unit note:\n  now: " + string.Join(", ", ms));
    }

    /// <summary>
    /// Signatures with ≥4 CONSECUTIVE floating-point parameters on one line — the transposition
    /// surface named-argument construction or a grouped record removes. Line-based by design;
    /// multi-line signatures dodge it, which is acceptable: the pin tracks the measured majority,
    /// not a proof. H1 verdict (2026-08-01): 25 is the honest floor — the set is coordinate pairs
    /// (lat/lng geometry, domain-idiomatic) and shape-frozen wire DTOs; the one ≥6 internal run
    /// (SensorStats) is constructed with named arguments, and ComputedTrackStats (multi-line, the
    /// survey's "worst 13") already was. Splitting geometry pairs into sub-records would churn the
    /// hot matching path for no transposition-risk gain.
    /// </summary>
    [Fact]
    public void Signatures_with_four_consecutive_floating_params_match_the_pin()
    {
        const int Pin = 25;
        var rxParam = new Regex(@"\b(double\??|float\??|int\??|string\??|bool\??|long\??|decimal\??)\s+\w+\s*[,)]",
            RegexOptions.Compiled, RegexDefaults.MatchTimeout);
        var sites = new List<string>();
        foreach (var file in ProductCsFiles(out _))
        {
            var n = 0;
            foreach (var line in File.ReadLines(file))
            {
                n++;
                var t = line.TrimStart();
                if (IsComment(t)) continue;
                int run = 0, best = 0;
                foreach (Match m in rxParam.Matches(line))
                {
                    if (m.Groups[1].Value.StartsWith("double", StringComparison.Ordinal)
                        || m.Groups[1].Value.StartsWith("float", StringComparison.Ordinal)
                        || m.Groups[1].Value.StartsWith("decimal", StringComparison.Ordinal))
                        best = Math.Max(best, ++run);
                    else run = 0;
                }
                if (best >= 4) sites.Add($"{Path.GetFileName(file)}:{n}");
            }
        }
        Assert.True(sites.Count == Pin,
            $"{sites.Count} signatures with ≥4 consecutive floating params (pin {Pin}) — group them into "
            + "a small record or construct with named arguments; lower the pin when one is fixed:\n  "
            + string.Join("\n  ", sites));
    }

    /// <summary>K1 progress ratchet (S6): signatures carrying ≥2 adjacent `int`-typed *Id params —
    /// the transposition surface strong ids exist to close. Every family slice (S7-S10) retypes
    /// some of these to their id structs, so the count only FALLS; lower the pin in that commit.
    /// The same-type pair (RaceCostCurve's two TrackIds) will remain — types cannot fix it.</summary>
    [Fact]
    public void Adjacent_int_id_params_match_the_pin()
    {
        const int Pin = 16;   // 41 → 35 (S7 VersionId) → 25 (S8 TrackId) → 22 (S9 SegmentId) → 16 (S10 Races)
        var rx = new Regex(@"\bint\??\s+(\w*[iI]d)\s*[,)]", RegexOptions.Compiled, RegexDefaults.MatchTimeout);
        var sites = new List<string>();
        foreach (var file in ProductCsFiles(out _))
        {
            var n = 0;
            foreach (var line in File.ReadLines(file))
            {
                n++;
                if (IsComment(line.TrimStart())) continue;
                if (rx.Matches(line).Count >= 2) sites.Add($"{Path.GetFileName(file)}:{n}");
            }
        }
        Assert.True(sites.Count == Pin,
            $"{sites.Count} signatures with adjacent int id params (pin {Pin}) — K1 slices lower "
            + "this by retyping id families; new confusable pairs need strong ids or a param object:\n  "
            + string.Join("\n  ", sites));
    }

    /// <summary>Postfix null-forgiving `!` per top-level directory. H2 (2026-08-01) swept the five
    /// top-offender files to ZERO (GpsNormalization tuple-captures, RaceCourseGrid/.Value-with-guard,
    /// EditSessionService ??-throw + OkEmpty, EffortPerformanceBuilder guard-captures + a latent
    /// exit-null NRE closed, ShareLinkService/EffortBuilder EF projections keep documented `!` — the one genuinely-right case:
    /// .Value and casts warn CS8629 inside expression trees); Services 125→99. The rest is
    /// the stated honest remainder — largely EF expression trees and guard-adjacent value-type reads —
    /// reviewed opportunistically as L touches their files. New `!` still needs a null-safe pattern.
    /// K2b (S2, 2026-08-01) retired 5 more in its opened files (StravaAuthService token! via
    /// pattern-match, CleanupService ghost UserId! via client-side guard, ShareLinkAccess user! via
    /// null-check narrowing, FriendshipService nav ! ×2 via ??-throw); Services 99→94.
    /// FriendshipService:GetPendingRequestsAsync keeps 1 commented expression-tree `!`.
    /// K1a (S6, 2026-08-01) retired 5 in its opened files (CommitStore Deserialize + RevertService
    /// blockedBy via ??-throw, [NotNullWhen(true)] on the two IsValid guards, lifecycle owner via
    /// OfType); Services 94→90, Api 16→15.
    /// K1d (S9, 2026-08-01) retired 6 in its opened files (SegmentEfforts ×4 + ShareSegmentReader ×2:
    /// ResolveGpsRange `entry!.Value/exit!.Value` after a HasValue filter → positional pattern
    /// `is (int entry, int exit)` narrows both at once); Services 90→84.
    /// K1e (S10, 2026-08-01) retired 8 (PhotoQueryService ×4: lat/lng + ContentHash guards → patterns;
    /// RaceCheckpointCrud ×3: MaxBy! → ??-throw naming the Start+Finish invariant, IsEndpoint MinBy/
    /// MaxBy! → `is { }` captures; RaceCrud ×1: NotNullWhen(false) already proves sourceName);
    /// Services 84→76. RaceCrud DuplicateRace expression-tree `!` stays (EF floor).
    /// M1 (S11, 2026-08-01) swept the no-K-slice files: 32 retired (cache TryGetValue null-checks,
    /// OfType over null-filtered Selects, NotNullWhen/MemberNotNullWhen annotations on IsWellFormed/
    /// HasTime, ??-throw on Deserialize/GetDirectoryName naming the invariant, XElement null content,
    /// SingleFlight late-init → guarded backing field, SegmentEffortProjection property-pattern
    /// capture, RaceLegAccumulator double[] after the All() gate, GeoUtils/StatisticsService local
    /// captures incl. one latent NRE closed); Services 76→44. The 44 remainder: ~14 EF expression-tree
    /// projections (ShareLinkService ×6, ShareRaceReader ×2, RaceCrud, SegmentCrud, PerformanceBuilder,
    /// FriendshipService, ActivityQueryService, StravaImportOrchestrator) + guard-adjacent value reads
    /// in hot-path track/codec/matching internals (ContentTrackReader/TrackBlobCodec/TrackChannel
    /// channel invariants, EditReplayEngine schema-validated ops, elevation/heatmap tile presence)
    /// — reviewed opportunistically as later slices open those files (H2 standing rule).
    /// M2 (S12, 2026-08-01) zeroed the other six pins (36 retired): OfType over null-filtered owner/
    /// token Selects, request-null folded into the Validate pattern, ??-throw naming Ok-result/
    /// reflection/BCL invariants, [NotNullWhen] on Visibilities.IsValid, IsWellFormed's annotation
    /// (S11) already proves the Share/View tokens, CreateLogger(Type) overload, ValidationErrorBody
    /// tuple loop, Details.cshtml RenderedView ??-throw. New `!` anywhere needs a null-safe pattern;
    /// Services' 43 honest remainder is the only non-zero pin.
    ///
    /// 44 → 43 (2026-08-03): RaceCrud's `course.TotalDistance!.Value` is gone with the metric it read.
    /// A race's Finish is seeded from the COURSE GRID now, which the create path already has in hand as a
    /// non-null local — so the guard that made the `!` safe became the value itself.</summary>
    [Fact]
    public void Postfix_null_forgiving_counts_match_their_pins()
    {
        var pins = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Services"] = 43,
        };
        var counts = CountPostfixBangs(out var root);
        var diffs = pins.Keys.Union(counts.Keys, StringComparer.Ordinal)
            .Select(k => (k, pin: pins.GetValueOrDefault(k), now: counts.GetValueOrDefault(k)))
            .Where(x => x.pin != x.now)
            .Select(x => $"{x.k}: {x.now} (pin {x.pin})")
            .ToList();
        Assert.True(diffs.Count == 0,
            "postfix-`!` counts moved — new `!` needs a null-safe pattern instead; lowered counts "
            + $"lower the pin in this commit (root {root}):\n  " + string.Join("\n  ", diffs));
    }

    // The bang counter is regex-subtle enough to test on its own — table below pins its semantics.
    internal static readonly Regex PostfixBang = new(@"[\w\)\]]!(?![=.]?=)", RegexOptions.Compiled, RegexDefaults.MatchTimeout);
    private static readonly Regex StringLiteral = new("\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.Compiled, RegexDefaults.MatchTimeout);

    [Theory]
    [InlineData("var x = a!.Foo;", 1)]
    [InlineData("if (a != b) return;", 0)]
    [InlineData("Use(list[0]!);", 1)]
    [InlineData("Use(Get()!);", 1)]
    [InlineData("var s = \"hello!\";", 0)]
    [InlineData("if (a !== b) { }", 0)]
    [InlineData("x = y! + z!;", 2)]
    public void Postfix_bang_counter_semantics(string line, int expected)
    {
        var stripped = StringLiteral.Replace(line, "\"\"");
        Assert.Equal(expected, PostfixBang.Matches(stripped).Count);
    }

    private static Dictionary<string, int> CountPostfixBangs(out string productRoot)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in ProductCsFiles(out productRoot))
        {
            var rel = Path.GetRelativePath(productRoot, file);
            var top = rel.Split(Path.DirectorySeparatorChar)[0];
            foreach (var line in File.ReadLines(file))
            {
                var t = line.TrimStart();
                if (IsComment(t)) continue;
                var stripped = StringLiteral.Replace(line, "\"\"");
                var n = PostfixBang.Matches(stripped).Count;
                if (n > 0) counts[top] = counts.GetValueOrDefault(top) + n;
            }
        }
        return counts;
    }

    private static bool NotBuildOutput(string f) => ProductTree.NotBuildOutput(f);

    private static List<string> ProductCsFiles(out string productRoot) =>
        ProductTree.ProductCsFiles(out productRoot);

    private static string ProductRoot() => ProductTree.ProductRoot();
}
