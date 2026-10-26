using RouteBaseWeb.Constants;
using System.Reflection;
using System.Text.RegularExpressions;
using RouteBaseWeb.Services.TrackEdit;
using Xunit;

namespace RouteBaseWeb.Tests.Parity;

/// <summary>
/// Pins the C#↔TS error-code mirror: every <see cref="TrackEditApiErrors"/> constant must appear in
/// TRACK_EDIT_API_ERRORS (edit-session-api.ts) and vice versa. The C# header claims the mirror; it went
/// stale once (inputsPurged missing → revert failures shown as a generic retry message, data-model
/// audit 2026-07-17 DTO-02) — this test makes the claim enforced instead of aspirational.
/// </summary>
public class TrackEditErrorCodeParityTests
{
    [Fact]
    public void ErrorCodes_CSharpAndTypeScript_MirrorExactly()
    {
        var csharpCodes = typeof(TrackEditApiErrors)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(csharpCodes);

        string ts = File.ReadAllText(FindTsFile());
        var block = Regex.Match(ts, @"TRACK_EDIT_API_ERRORS\s*=\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline, RegexDefaults.MatchTimeout);
        Assert.True(block.Success, "TRACK_EDIT_API_ERRORS object literal not found in edit-session-api.ts");

        var tsCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(block.Groups["body"].Value, @"(\w+)\s*:\s*'([^']+)'", RegexOptions.None, RegexDefaults.MatchTimeout))
        {
            Assert.Equal(m.Groups[1].Value, m.Groups[2].Value); // key must equal its wire value
            tsCodes.Add(m.Groups[2].Value);
        }

        Assert.Empty(csharpCodes.Except(tsCodes, StringComparer.Ordinal)); // C# code missing from the TS mirror
        Assert.Empty(tsCodes.Except(csharpCodes, StringComparer.Ordinal)); // TS code the server never emits
    }

    private static string FindTsFile()
    {
        const string rel = "RouteBaseWeb/src/js/track-edit/edit-session-api.ts";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, rel);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"Could not locate {rel} above {AppContext.BaseDirectory}");
    }
}
