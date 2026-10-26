using RouteBaseWeb.Constants;
using System.Reflection;
using System.Text.RegularExpressions;
using RouteBaseWeb.Models;
using Xunit;

namespace RouteBaseWeb.Tests.Architecture;

/// <summary>
/// Gate (d). The account/device split is a documented RULE, and this is the half of it a machine can hold:
///
///   ACCOUNT if the same user on a new device would be annoyed to LOSE it — taste, or a physical fact
///   about the user. DEVICE if they would be annoyed to INHERIT it — position, size, open/closed, ordering
///   on one screen. Device values never reach the server.
///
/// So a screen-chrome preference must never become a <see cref="SettingKeys"/> constant. The failure this
/// stops is quiet and one-way: a drawer height or a collapsed-panel flag lands in the settings table
/// because that is where "preferences" go, and now one screen's pixels follow the user onto a phone. Once
/// a row exists it is a stored contract, so the mistake is expensive to reverse — which is why the gate
/// belongs at the moment the key is DECLARED.
///
/// ⚠ The pattern is chrome NOUNS and state SUFFIXES, deliberately NOT the bare words "Width"/"Height".
/// <c>ChartBorderWidth</c> and <c>TrackLineWidth</c> are stroke widths of a drawn line — taste, correctly
/// account-level — and a naive geometry-word denylist would reject them. What makes a dimension a DEVICE
/// value is the thing being measured (a panel, a column, a drawer), not the unit. Widening this to bare
/// geometry words would force an exemption list, and an architecture gate with exemptions has stopped
/// meaning anything.
/// </summary>
public class SettingsTierGateTests
{
    /// <summary>
    /// Screen chrome. Anything sized, ordered, or toggled on one of these is a property of the window the
    /// user happens to be looking at.
    /// </summary>
    private const string ChromeNouns =
        "Panel|Sidebar|Drawer|Column|Nav|Toolbar|Splitter|Accordion|Modal|Dialog|Popup|Tooltip|Legend|Tab";

    /// <summary>
    /// Chrome STATE. A key ending in one of these is describing what is open, docked or scrolled right now.
    /// </summary>
    private const string ChromeStateSuffixes =
        "Collapsed|Expanded|Opened|Open|Closed|Pinned|Docked|Hidden|Minimized|Maximized|ScrollTop|ScrollLeft|ScrollPosition";

    [Fact]
    public void NoSettingKey_IsADeviceLevelPreference()
    {
        var keys = typeof(SettingKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => f.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // Vacuity guard: a reflection walk that found nothing would pass while permitting anything.
        Assert.True(keys.Length >= 15, $"Found only {keys.Length} SettingKeys constants — this gate is vacuous.");

        var chrome = new Regex($"({ChromeNouns})", RegexOptions.CultureInvariant, RegexDefaults.MatchTimeout);
        var state = new Regex($"({ChromeStateSuffixes})$", RegexOptions.CultureInvariant, RegexDefaults.MatchTimeout);

        var offenders = keys.Where(k => chrome.IsMatch(k) || state.IsMatch(k)).ToArray();

        Assert.True(
            offenders.Length == 0,
            "These look like DEVICE preferences but are declared as account settings, so one screen's "
            + "layout would follow the user onto every other device — and, being stored rows, would be "
            + "expensive to take back. Screen chrome belongs in core/device-prefs.ts (localStorage, `rb1.` "
            + "prefix); the settings table is for taste and for physical facts about the user:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Guards the guard. The rule above is a denylist, and a denylist that has quietly stopped matching
    /// reads exactly like a clean codebase — so prove both halves still fire on names that must be rejected,
    /// and prove the two live account-level stroke widths are still accepted (the false positive this
    /// pattern was shaped to avoid).
    /// </summary>
    [Fact]
    public void TheDeviceDenylist_RejectsChrome_AndAcceptsTaste()
    {
        var chrome = new Regex($"({ChromeNouns})", RegexOptions.CultureInvariant, RegexDefaults.MatchTimeout);
        var state = new Regex($"({ChromeStateSuffixes})$", RegexOptions.CultureInvariant, RegexDefaults.MatchTimeout);
        bool Rejected(string name) => chrome.IsMatch(name) || state.IsMatch(name);

        Assert.True(Rejected("SegmentPanelOpen"));
        Assert.True(Rejected("GridColumnWidth"));
        Assert.True(Rejected("RaceSidebarWidth"));
        Assert.True(Rejected("ReplayDrawerHeight"));
        Assert.True(Rejected("ActivityCardCollapsed"));
        Assert.True(Rejected("NavExpanded"));

        // Taste, not geometry — these are the reason the pattern is noun-anchored. A stroke width is a
        // property of a drawn line, not of the window; the unit says nothing about which tier it belongs to.
        Assert.False(Rejected(nameof(SettingKeys.ChartBorderWidth)));
        Assert.False(Rejected(nameof(SettingKeys.RiderWeightKg)));
        Assert.False(Rejected(nameof(SettingKeys.DefaultColorMode)));
        Assert.False(Rejected("MarkerRadiusPx"));
    }
}
