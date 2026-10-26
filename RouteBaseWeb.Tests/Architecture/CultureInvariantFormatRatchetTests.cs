using RouteBaseWeb.Constants;
using System.Text.RegularExpressions;
using Xunit;

namespace RouteBaseWeb.Tests.Architecture;

/// <summary>
/// Culture and clock gates (AR-03 A1-A3). Nothing anywhere sets the process culture (no
/// .editorconfig, no InvariantGlobalization, no RequestLocalization, no LANG in the Dockerfile), so
/// any format call without a named provider follows whatever culture the host has — under th-TH a
/// backup filename gained 543 years and its ordinal retention sort pruned the NEWEST snapshots.
///
/// The rule is "NAME your provider", not "use invariant": <c>CultureInfo.CurrentCulture</c> is a
/// legal, greppable, self-documenting answer for display strings. That is what lets this gate need
/// zero judgement — the author states the choice in source and review sees it.
///
/// Scope boundaries, deliberate: interpolated <c>$"{x:N0}"</c> is out (3 sites; the one that
/// matters is caught by the clock leg); zero-arg <c>.ToString()</c> is out; <c>ILogger</c> templates
/// are already invariant via LogValuesFormatter and must never be flagged.
///
/// K6 (2026-08-01) SUPERSEDED this file's old "repo-wide CA1305-as-error: never re-proposed"
/// stance — the owner promoted the three culture rules (CA1304/1305/1310) to error in the root
/// .editorconfig after the blast radius was measured (16 product sites, zeroed in S4; tests swept
/// in K6b). The error is the pin; the three hand-gate legs at the bottom of this class are the
/// zero-build-cost backstop so plain `dotnet test` catches regressions without a dragnet build.
/// Still unchanged: <c>StravaApiClient.cs:46</c> (implicit DateTimeOffset cast — safe, the sole
/// caller sends Z; the construct DateTimeIso warns about, kept visible here).
/// </summary>
public class CultureInvariantFormatRatchetTests
{
    /// <summary>"file|format" keys (line numbers drift, file+format doesn't). Measured 2026-08-01 at
    /// 13 keys; B1 named the provider at 11, B3 retyped the two date-only wire producers to
    /// ToWallClockIso. Permanent at zero — every format call names its provider.</summary>
    private static readonly IReadOnlySet<string> ProviderlessFormatPin = new HashSet<string>(StringComparer.Ordinal);

    [Fact]
    public void Product_code_names_its_format_provider()
    {
        var rx = new Regex(@"\.ToString\(\s*""([^""]*)""\s*\)", RegexOptions.Compiled, RegexDefaults.MatchTimeout);
        var files = ProductTree.ProductCsFiles(out var root);
        Assert.True(files.Count > 400, $"walk found only {files.Count} files under {root} — fix the walk");

        var found = new SortedSet<string>(StringComparer.Ordinal);
        var totalToStringCalls = 0;
        foreach (var file in files)
        {
            foreach (var line in File.ReadLines(file))
            {
                if (ProductTree.IsComment(line.TrimStart())) continue;
                if (line.Contains(".ToString(", StringComparison.Ordinal)) totalToStringCalls++;
                foreach (Match m in rx.Matches(line))
                    found.Add($"{Path.GetFileName(file)}|{m.Groups[1].Value}");
            }
        }
        Assert.True(totalToStringCalls >= 50, $"only {totalToStringCalls} ToString calls seen — walk broken");

        var added = found.Except(ProviderlessFormatPin, StringComparer.Ordinal).ToList();
        var stale = ProviderlessFormatPin.Except(found, StringComparer.Ordinal).ToList();
        Assert.True(added.Count == 0,
            "format call WITHOUT a named provider — pass CultureInfo.InvariantCulture (machine) or "
            + "CultureInfo.CurrentCulture (display); the pin only shrinks:\n  " + string.Join("\n  ", added));
        Assert.True(stale.Count == 0,
            "pinned provider-less formats no longer found — delete the entries in this commit:\n  "
            + string.Join("\n  ", stale));
    }

    /// <summary>Year-first is machine; anything else is display. A machine-readable date built in a
    /// VIEW round-trips asymmetrically (view writes CurrentCulture, QueryStringValueProvider reads
    /// back Invariant — 543 years apart under th-TH). B1 zeroed the 3 measured sites: the two
    /// Activities.cshtml hidden value= round-trips became PageModel *Param properties (where the
    /// provider gate covers them) and the Segments/Create option label became display dd.MM.yyyy.
    /// Permanent at zero — ISO formatting belongs in the PageModel, never the view.</summary>
    [Fact]
    public void Razor_views_do_not_build_machine_readable_dates()
    {
        const int Pin = 0;
        var rx = new Regex(@"ToString\(""[^""]*yyyy-?MM", RegexOptions.Compiled, RegexDefaults.MatchTimeout);
        var sites = new List<string>();
        foreach (var file in ProductTree.ProductCshtmlFiles(out _))
        {
            var n = 0;
            foreach (var line in File.ReadLines(file))
            {
                n++;
                if (rx.IsMatch(line)) sites.Add($"{Path.GetFileName(file)}:{n}");
            }
        }
        Assert.True(sites.Count == Pin,
            $"{sites.Count} machine-date builds in Razor (pin {Pin}) — move ISO formatting into the "
            + "PageModel (where the provider gate covers it); human formats follow the reader's culture:\n  "
            + string.Join("\n  ", sites));
    }

    // ── K6 hand-gate backstops. The real pin is CA1304/1305/1310 at ERROR in .editorconfig;
    // analyzers only run in dragnet builds (+27s), so these three legs re-check the same families
    // at zero build cost in plain `dotnet test`. Textual and slightly narrower than the analyzers
    // on purpose — they must never false-positive, the dragnet catches the remainder. ──

    /// <summary>Numeric/date Parse must name its provider (the th-TH 543-year class). Statement-
    /// scoped: the provider often sits on the continuation line, so the check window spans the
    /// call, not the line. NumberStyles/DateTimeStyles count as evidence (their overloads carry
    /// the provider). Permanent at zero.</summary>
    [Fact]
    public void Product_code_does_not_parse_without_a_provider()
    {
        var rx = new Regex(
            @"\b(?:int|long|short|byte|uint|ulong|double|float|decimal|DateTime|DateTimeOffset|TimeSpan)\.Parse\(",
            RegexOptions.Compiled, RegexDefaults.MatchTimeout);
        var evidence = new Regex("CultureInfo|Invariant|NumberStyles|DateTimeStyles", RegexOptions.Compiled, RegexDefaults.MatchTimeout);
        var files = ProductTree.ProductCsFiles(out var root);
        Assert.True(files.Count > 400, $"walk found only {files.Count} files under {root} — fix the walk");

        var sites = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match m in rx.Matches(text))
            {
                var window = text.Substring(m.Index, Math.Min(220, text.Length - m.Index));
                if (!evidence.IsMatch(window))
                    sites.Add($"{Path.GetFileName(file)}:{text.AsSpan(0, m.Index).Count('\n') + 1}");
            }
        }
        Assert.True(sites.Count == 0,
            "Parse without a provider — pass CultureInfo.InvariantCulture (machine) or "
            + "CultureInfo.CurrentCulture (display):\n  " + string.Join("\n  ", sites));
    }

    /// <summary>String-literal StartsWith/EndsWith/IndexOf must name a StringComparison — the
    /// providerless overloads are CURRENT-culture (and "i" ≠ "I" under tr-TR). Permanent at zero.</summary>
    [Fact]
    public void Product_code_names_its_string_comparison()
    {
        var rx = new Regex(@"\.(?:StartsWith|EndsWith|IndexOf)\(\s*""", RegexOptions.Compiled, RegexDefaults.MatchTimeout);
        var sites = new List<string>();
        foreach (var file in ProductTree.ProductCsFiles(out _))
        {
            var n = 0;
            foreach (var line in File.ReadLines(file))
            {
                n++;
                if (ProductTree.IsComment(line.TrimStart())) continue;
                if (rx.IsMatch(line) && !line.Contains("StringComparison", StringComparison.Ordinal))
                    sites.Add($"{Path.GetFileName(file)}:{n}");
            }
        }
        Assert.True(sites.Count == 0,
            "StartsWith/EndsWith/IndexOf without a StringComparison — say Ordinal (machine) or "
            + "CurrentCulture (display):\n  " + string.Join("\n  ", sites));
    }

    /// <summary>Culture-blind ToLower()/ToUpper() pinned at exactly the 2 sanctioned sites:
    /// UserService.SearchUsersAsync's EF-Where pair, which translates to SQL LOWER() and never
    /// consults a .NET culture (#pragma'd CA1304 there says the same). Anything else says
    /// Invariant, or names its culture.</summary>
    [Fact]
    public void Product_code_culture_blind_case_mapping_is_pinned()
    {
        const int Pin = 2;
        var rx = new Regex(@"\.To(?:Lower|Upper)\(\)", RegexOptions.Compiled, RegexDefaults.MatchTimeout);
        var sites = new List<string>();
        foreach (var file in ProductTree.ProductCsFiles(out _))
        {
            var n = 0;
            foreach (var line in File.ReadLines(file))
            {
                n++;
                if (ProductTree.IsComment(line.TrimStart())) continue;
                if (rx.IsMatch(line)) sites.Add($"{Path.GetFileName(file)}:{n}");
            }
        }
        Assert.True(sites.Count == Pin,
            $"{sites.Count} bare ToLower/ToUpper (pin {Pin} — UserService's EF LOWER() pair): use "
            + "ToLowerInvariant/ToUpperInvariant or name the culture; lowered counts lower the pin:\n  "
            + string.Join("\n  ", sites));
    }

    /// <summary>Server-local clock reads. Permanent at zero since B1: the three byte-identical
    /// daily-schedule copies became Helpers/DailyRunSchedule (DateTimeOffset.Now says "server-local
    /// wall clock" in the type — the ONLY sanctioned local-clock read), and the backup filename moved
    /// to UtcNow. No allowed-site list on purpose.
    /// Vacuity canary: clock reads must stay visible to the walk. K2 replaced the DateTime.UtcNow
    /// tokens this used to count with the TimeProvider seam, so the proxy is now GetUtcNow( —
    /// same intent (a broken walk must not pass as "no local clock"), same walk.</summary>
    [Fact]
    public void Product_code_has_no_server_local_clock()
    {
        string[] pin = [];
        var rx = new Regex(@"\bDateTime\.(Now|Today)\b", RegexOptions.Compiled, RegexDefaults.MatchTimeout);
        var files = new SortedSet<string>(StringComparer.Ordinal);
        var clockReads = 0;
        foreach (var file in ProductTree.ProductCsFiles(out _))
        {
            foreach (var line in File.ReadLines(file))
            {
                if (ProductTree.IsComment(line.TrimStart())) continue;
                if (rx.IsMatch(line)) files.Add(Path.GetFileName(file));
                if (line.Contains("GetUtcNow(", StringComparison.Ordinal)) clockReads++;
            }
        }
        Assert.True(clockReads >= 40, $"only {clockReads} GetUtcNow( seen — the walk stopped seeing clock reads at all");
        Assert.True(files.SetEquals(pin),
            "DateTime.Now/Today set changed — UTC instants use UtcNow; a deliberate server-local wall "
            + "clock uses DateTimeOffset.Now (says so in the type). Now:\n  " + string.Join("\n  ", files));
    }
}
