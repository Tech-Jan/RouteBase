using System.Reflection;
using Xunit;

namespace RouteBaseWeb.Tests.Architecture;

/// <summary>
/// AR-02 ratchet: no NEW raw <see cref="DateTime"/> on the response surface.
///
/// A raw DateTime serializes with an offset only if its Kind says so, and the SQLite round-trip strips
/// Kind — so a UTC instant leaves as offset-less ISO and every browser parses it as LOCAL time (the
/// CC2-04 bug class; <c>Helpers/DateTimeIso.cs</c> documents both halves). The type system can say what
/// the value means instead: an instant is a <see cref="DateTimeOffset"/> (always serializes an offset),
/// a wall-clock value is a pre-formatted string (<c>DateTimeIso.ToWallClockIso</c>, FE-12 — the
/// dashboard/races/segments grids already consume that shape as <c>dateIso</c>/<c>createdIso</c>).
///
/// The migration is COMPLETE (2026-08-01, slices 1-6): the pin below is empty and this gate reads
/// "no raw DateTime on any response DTO" — permanently. A new response field is born either
/// DateTimeOffset (instant) or a pre-formatted string (wall-clock); this test failing is the reminder.
/// The pin mechanism is kept (not simplified away) so any future exception would need an explicit,
/// visible entry here rather than a gate rewrite.
///
/// Stale pins FAIL here, unlike the file-size ratchet's parity stance (<c>CSharpFileSizeRatchetTests</c>).
/// That relaxation exists because line counts drift with unrelated edits and pile jobs may not edit the
/// shared table; this set has no noise — membership changes only on a deliberate migration.
///
/// Scope = every component schema a client can RECEIVE: response-reachable schemas plus the registered
/// JSON islands (<c>IslandSchemasDocumentTransformer</c> puts them in the document with no endpoint).
/// Request-ONLY schemas are excluded — they are parsed, not emitted, so their DateTime binding is not
/// this gate's concern. Adjacent non-JSON surfaces (data attributes via <c>ToString("o")</c>, e.g.
/// <c>EditResumeHint</c>) are outside the document and outside this gate.
/// </summary>
public class ResponseDtoDateTimeRatchetTests
{

    /// <summary>
    /// The raw-DateTime response surface as measured 2026-08-01 (AR-02 blast radius). DELETE entries as
    /// their slices migrate — never add. Grouped by the migration treatment each field gets.
    /// </summary>
    private static readonly IReadOnlySet<string> Pinned = new HashSet<string>(StringComparer.Ordinal)
    {
        // EMPTY since 2026-08-01: all 41 fields measured that morning were migrated the same day
        // (AR-02 slices 1-6). This gate now reads "no raw DateTime on any response DTO" — permanently.
    };

    [Fact]
    public void Response_surface_raw_DateTime_set_matches_the_pin_exactly()
    {
        var found = ScanResponseSurface(out var scopedSchemas, out var mappedTypes);

        // Vacuity guards: the scan must keep seeing the real document and resolving real types even
        // after the pin empties out (when the stale leg can no longer catch a broken scan).
        Assert.True(scopedSchemas >= 100,
            $"only {scopedSchemas} response-side schemas in {OpenApiDocumentSurface.DocPath} — the document walk is broken");
        Assert.True(mappedTypes >= 40,
            $"only {mappedTypes} schemas resolved to C# types — the schema-name → type mapping is broken");

        var added = found.Except(Pinned, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var stale = Pinned.Except(found, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(added.Count == 0,
            $"NEW raw DateTime on the response surface ({added.Count}):\n  " + string.Join("\n  ", added) +
            "\nA response field is either an instant — declare it DateTimeOffset — or a wall-clock value — " +
            "send a pre-formatted string (Helpers/DateTimeIso.ToWallClockIso). The pin only shrinks (AR-02).");

        Assert.True(stale.Count == 0,
            $"pinned raw-DateTime fields no longer on the response surface ({stale.Count}):\n  " +
            string.Join("\n  ", stale) +
            "\nMigrated (or renamed/removed) — delete these entries from the pin in the SAME commit, so the " +
            "pin keeps meaning 'exactly what is left to migrate'.");
    }

    /// <summary>Every "Type.Property" on the response surface whose property type is raw DateTime.</summary>
    private static SortedSet<string> ScanResponseSurface(out int scopedSchemas, out int mappedTypes)
    {
        using var doc = OpenApiDocumentSurface.Load();
        var scope = OpenApiDocumentSurface.ResponseScope(doc);
        scopedSchemas = scope.Count;

        var app = typeof(RouteBaseWeb.Models.ActivityRowDto).Assembly;
        var byName = OpenApiDocumentSurface.AppTypesByName(app);

        var roots = new List<Type>();
        mappedTypes = 0;
        foreach (var name in scope)
        {
            var matched = OpenApiDocumentSurface.Resolve(name, byName);
            if (matched.Count == 0) continue;
            mappedTypes++;
            roots.AddRange(matched);
        }

        var found = new SortedSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<Type>();
        foreach (var t in roots) Walk(t, app, visited, found);
        return found;
    }

    /// <summary>
    /// Reflect over a scoped type's public instance properties, recursing into app-assembly property
    /// types so shapes the document inlines (grid bootstrap rows …) are still covered. Keyed by
    /// DECLARING type, so an inherited property pins once, where it is declared.
    /// </summary>
    private static void Walk(Type type, Assembly app, HashSet<Type> visited, SortedSet<string> found)
    {
        if (!visited.Add(type)) return;
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            foreach (var ct in Flatten(p.PropertyType))
            {
                if (ct == typeof(DateTime))
                    found.Add($"{ShortName(p.DeclaringType!)}.{p.Name}");
                else if (ct.Assembly == app && !ct.IsEnum)
                    Walk(ct, app, visited, found);
            }
        }
    }

    /// <summary>Unwrap Nullable/arrays/generics down to the types a serializer would actually write.</summary>
    private static IEnumerable<Type> Flatten(Type t)
    {
        if (t.IsGenericParameter) yield break;
        if (Nullable.GetUnderlyingType(t) is { } u) { foreach (var x in Flatten(u)) yield return x; yield break; }
        if (t.IsArray) { foreach (var x in Flatten(t.GetElementType()!)) yield return x; yield break; }
        if (t.IsGenericType)
        {
            yield return t; // a closed app generic has its own properties; framework ones are filtered by the caller
            foreach (var arg in t.GetGenericArguments())
                foreach (var x in Flatten(arg)) yield return x;
            yield break;
        }
        yield return t;
    }

    private static string ShortName(Type t) =>
        t.IsGenericType ? t.Name[..t.Name.IndexOf('`', StringComparison.Ordinal)] : t.Name;

}
