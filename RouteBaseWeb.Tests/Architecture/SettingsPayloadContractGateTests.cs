using System.Reflection;
using System.Text.Json;
using Xunit;

namespace RouteBaseWeb.Tests.Architecture;

/// <summary>
/// Gate (e). The settings island payload and <c>PROFILE_SETTINGS_DEFAULTS</c> must describe the SAME set of
/// settings.
///
/// They are two halves of one promise: the payload is what the server delivers, the generated defaults are
/// what the client uses when it was delivered nothing. A setting present in one and absent from the other is
/// the whole bug class this redesign exists to close —
///   • in the payload but not the defaults → nothing to fall back to, so the reader invents a literal, and
///     that literal is what drifts (tension <c>?? 0</c> against the C# 0.2, hillshade <c>?? 0.0</c> against
///     0.2, where 0.0 does not merely look different — it makes the caller skip the layer entirely);
///   • in the defaults but not the payload → a preference the user can save and the page can never see,
///     which is <c>defaultZoom</c>, <c>defaultStyle</c>, <c>chartDetail</c> and <c>defaultColorMode</c> today.
///
/// <see cref="SettingsDefaultsTsGateTests"/> pins the C#-const → generated-TS half. This pins the
/// C#-const → wire-payload half, against the same list, so there is exactly one place a setting is declared.
///
/// ⚠ The payload is ISLAND-ONLY and must never become a request DTO. <c>gen-contracts.mjs</c>'s
/// <c>relaxRequestOnlySchemas</c> step relaxes any schema reachable only from request bodies back to
/// all-optional, so making this a PUT body would silently evaporate the non-nullability below with no test
/// failing. A write endpoint needs its own <c>SettingsUpdateRequest</c>.
/// </summary>
public class SettingsPayloadContractGateTests
{
    /// <summary>
    /// The payload type, found by name in the web assembly rather than referenced directly: it is moving
    /// (nested in <c>ProfileSettingsBootstrap</c> today, its own <c>Models/Settings/SettingsPayload.cs</c>
    /// after step 2) and this gate is about the CONTRACT, not about where the file lives.
    /// </summary>
    private static Type PayloadType()
    {
        var candidates = typeof(RouteBaseWeb.Auth.ShareLinkDefaults).Assembly
            .GetTypes()
            .Where(t => string.Equals(t.Name, "SettingsPayload", StringComparison.Ordinal))
            .ToArray();

        Assert.True(candidates.Length > 0, "No type named SettingsPayload exists in RouteBaseWeb.");
        Assert.True(candidates.Length == 1,
            "More than one SettingsPayload — the island contract must be unambiguous:\n  "
            + string.Join("\n  ", candidates.Select(t => t.FullName)));

        return candidates[0];
    }

    /// <summary>Every (section, property) the payload serializes, in the camelCase the island emits.</summary>
    private static HashSet<string> PayloadFields()
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);

        foreach (var section in Properties(PayloadType()))
        {
            var sectionType = Nullable.GetUnderlyingType(section.PropertyType) ?? section.PropertyType;
            foreach (var leaf in Properties(sectionType))
                fields.Add($"{Camel(section.Name)}.{Camel(leaf.Name)}");
        }

        return fields;
    }

    private static PropertyInfo[] Properties(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
            // `record`'s compiler-generated equality contract is not part of the wire shape.
            .Where(p => !string.Equals(p.Name, "EqualityContract", StringComparison.Ordinal))
            .ToArray();

    // The exact policy BootstrapJson serializes with, so this compares wire names to wire names.
    private static string Camel(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

    [Fact]
    public void ThePayloadFieldSet_MatchesTheGeneratedDefaultsContract()
    {
        var expected = SettingsDefaultsTsGateTests.Contract
            .Select(c => $"{c.Section}.{c.Property}")
            .ToHashSet(StringComparer.Ordinal);

        // Vacuity guard: an empty contract or an unwalkable payload would make the diff below trivially empty.
        Assert.True(expected.Count >= 10, $"The defaults contract lists only {expected.Count} settings — this gate is vacuous.");

        var actual = PayloadFields();
        Assert.True(actual.Count > 0, "Walked the payload and found no fields — the reflection walk is broken.");

        var missingFromPayload = expected.Except(actual, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var missingFromDefaults = actual.Except(expected, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();

        Assert.True(
            missingFromPayload.Length == 0 && missingFromDefaults.Length == 0,
            "The settings payload and PROFILE_SETTINGS_DEFAULTS declare different settings. One list, or "
            + "the readers start inventing fallbacks again.\n"
            + "  has a generated default but is NEVER DELIVERED (the user can save it; no page can read it):\n    "
            + (missingFromPayload.Length == 0 ? "(none)" : string.Join("\n    ", missingFromPayload)) + "\n"
            + "  is delivered but has NO generated default (a reader with nothing to fall back to):\n    "
            + (missingFromDefaults.Length == 0 ? "(none)" : string.Join("\n    ", missingFromDefaults)));
    }

    /// <summary>
    /// The payload's sections must be non-nullable. This is the design's load-bearing claim: it is what lets
    /// ~16 readers drop their fallback and what makes <c>NonNullable&lt;…&gt;</c> in core-types.ts
    /// unnecessary rather than a no-op.
    ///
    /// It is also a live bug. <c>core/smoothing.ts</c> treats an ABSENT payload as smoothing ON but a present
    /// payload with a null <c>charts</c> as OFF; <c>segment-stats-delta.ts</c> uses the opposite convention.
    /// Same saved preference, two different renderings — and the only reason the question exists is that a
    /// section can be null. Removing "absent" as a representable state is the fix.
    /// </summary>
    [Fact]
    public void ThePayloadSections_AreNonNullable()
    {
        var payload = PayloadType();
        var context = new NullabilityInfoContext();

        var nullable = Properties(payload)
            .Where(p => context.Create(p).ReadState == NullabilityState.Nullable)
            .Select(p => $"{payload.Name}.{p.Name}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            nullable.Length == 0,
            "These payload sections are nullable, so \"delivered but empty\" is a state every reader has to "
            + "have an opinion about — and today two of them disagree (smoothing.ts reads a null charts "
            + "section as OFF, segment-stats-delta.ts reads it as ON). The payload is always complete:\n  "
            + string.Join("\n  ", nullable));
    }
}
