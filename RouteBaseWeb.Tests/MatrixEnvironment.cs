using System.Globalization;
using System.Runtime.CompilerServices;

namespace RouteBaseWeb.Tests;

/// <summary>
/// Hostile-environment matrix hook (gates.sh --matrix). When ROUTEBASE_TEST_CULTURE is set,
/// every test thread runs under that culture — the matrix uses th-TH (Buddhist calendar,
/// 2026 CE = 2569), so a culture-sensitive format/parse anywhere in the suite goes red
/// without any per-test opt-in. Timezone needs no hook: TimeZoneInfo.Local honors TZ on
/// Linux, so gates.sh exports it directly. Unset ⇒ zero effect on the normal run.
/// </summary>
internal static class MatrixEnvironment
{
    [ModuleInitializer]
    internal static void Apply()
    {
        var culture = Environment.GetEnvironmentVariable("ROUTEBASE_TEST_CULTURE");
        if (string.IsNullOrEmpty(culture)) return;

        var ci = CultureInfo.GetCultureInfo(culture);
        CultureInfo.DefaultThreadCurrentCulture = ci;
        CultureInfo.DefaultThreadCurrentUICulture = ci;
    }
}
