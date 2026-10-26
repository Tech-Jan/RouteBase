using System.Globalization;
using RouteBaseWeb.Services.Races;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Races;

/// <summary>
/// Reading a status token out of a results file.
///
/// The vocabulary exists because the old boolean ("has a split at the finish") is contradicted by real
/// data in BOTH directions — a published DNF carrying a finish-mat time, and a published status with no
/// time at all. These tests pin the tokens the two staged files actually contain, plus the multi-language
/// spellings the research turned up.
/// </summary>
public class RaceFinishStatusTests
{
    [Theory]
    // Black Forest (Datasport) publishes exactly these three.
    [InlineData("Finisher", RaceFinishStatus.Finished)]
    [InlineData("DNF", RaceFinishStatus.Dnf)]
    [InlineData("DNS", RaceFinishStatus.Dns)]
    // German timing exports.
    [InlineData("Aufg.", RaceFinishStatus.Dnf)]
    [InlineData("aufgegeben", RaceFinishStatus.Dnf)]
    [InlineData("Nicht gestartet", RaceFinishStatus.Dns)]
    [InlineData("Disqualifiziert", RaceFinishStatus.Dsq)]
    [InlineData("Fehlstempel", RaceFinishStatus.MissingPunch)]
    [InlineData("Ausser Konkurrenz", RaceFinishStatus.NotCompeting)]
    // FR / IT / NL / Nordic.
    [InlineData("Abandon", RaceFinishStatus.Dnf)]
    [InlineData("Ritirato", RaceFinishStatus.Dnf)]
    [InlineData("hors délai", RaceFinishStatus.Otl)]
    [InlineData("hors classement", RaceFinishStatus.NotCompeting)]
    [InlineData("s.t.", RaceFinishStatus.Finished)]
    [InlineData("ej start", RaceFinishStatus.Dns)]
    // Abbreviations that only mean one thing in a status column.
    [InlineData("OK", RaceFinishStatus.Finished)]
    [InlineData("MP", RaceFinishStatus.MissingPunch)]
    [InlineData("NC", RaceFinishStatus.NotCompeting)]
    [InlineData("DQ", RaceFinishStatus.Dsq)]
    [InlineData("OTL", RaceFinishStatus.Otl)]
    [InlineData("HD", RaceFinishStatus.Otl)]
    public void Normalize_ReadsTheTokensRealFilesPublish(string raw, string expected)
    {
        Assert.Equal(expected, RaceFinishStatusVocabulary.Normalize(raw));
    }

    [Theory]
    [InlineData("dnf")]
    [InlineData("DNF")]
    [InlineData("Dnf")]
    [InlineData("  DNF  ")]
    public void Normalize_IsCaseAndWhitespaceInsensitive(string raw)
    {
        Assert.Equal(RaceFinishStatus.Dnf, RaceFinishStatusVocabulary.Normalize(raw));
    }

    [Fact]
    public void Normalize_CollapsesInternalWhitespace()
    {
        Assert.Equal(RaceFinishStatus.Dns, RaceFinishStatusVocabulary.Normalize("Nicht   gestartet"));
    }

    /// <summary>
    /// Schauinsland's live token. It is not DNS and it is not DNF, and pretending it is either would put
    /// 113 entrants in a bucket the file never claimed — so it normalizes to Unknown and the RAW token is
    /// stored alongside, which is the only thing that keeps the merged value recoverable.
    /// </summary>
    [Theory]
    [InlineData("DNS/DNF")]
    [InlineData("weird")]
    [InlineData("???")]
    public void Normalize_AnUnreadableToken_IsUnknown_NotAGuess(string raw)
    {
        Assert.Equal(RaceFinishStatus.Unknown, RaceFinishStatusVocabulary.Normalize(raw));
    }

    /// <summary>
    /// Blank is a THIRD answer, not a synonym for Unknown: it means the file published no status, so the
    /// derived rule still governs. That is the whole migration story for the races imported before this
    /// column existed — nothing to backfill.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_Blank_IsNull_SoTheDerivedRuleStillGoverns(string? raw)
    {
        Assert.Null(RaceFinishStatusVocabulary.Normalize(raw));
    }

    /// <summary>
    /// The alias table decides whether a rider's race counted, so it must not change meaning with the
    /// server's culture. Turkish is the classic break (dotless ı ≠ i under an invariant-ignoring compare);
    /// th-TH is the one this repo already has a gotcha for.
    /// </summary>
    [Theory]
    [InlineData("tr-TR")]
    [InlineData("th-TH")]
    [InlineData("de-DE")]
    public void Normalize_IsCultureInvariant(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.Equal(RaceFinishStatus.Finished, RaceFinishStatusVocabulary.Normalize("FINISHER"));
            Assert.Equal(RaceFinishStatus.Dnf, RaceFinishStatusVocabulary.Normalize("dnf"));
            Assert.Equal(RaceFinishStatus.Dns, RaceFinishStatusVocabulary.Normalize("Nicht gestartet"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Normalize_AlwaysProducesALegalWireValue()
    {
        string[] everyTokenSeen =
        [
            "Finisher", "DNF", "DNS", "DNS/DNF", "Aufg.", "s.t.", "NC", "MP", "DQ", "OTL", "nonsense",
        ];

        foreach (var token in everyTokenSeen)
            Assert.True(RaceFinishStatusVocabulary.IsValid(RaceFinishStatusVocabulary.Normalize(token)), token);
    }

    [Fact]
    public void All_MatchesTheWireEnumTheDocumentStamps()
    {
        // The transformer reflects over RaceFinishStatus's public const strings to build the OpenAPI enum
        // (and thus the TS union). `All` is what the server side reads. They are two views of one set, and
        // a value present in one and not the other is a filter that matches nothing at the far end.
        var stamped = RouteBaseWeb.Api.OpenApiContracts.WireValueEnumSchemaTransformer
            .ConstStrings(typeof(RaceFinishStatus));

        Assert.Equal(stamped.OrderBy(v => v, StringComparer.Ordinal), RaceFinishStatusVocabulary.All.OrderBy(v => v, StringComparer.Ordinal));
        Assert.Equal(10, RaceFinishStatusVocabulary.All.Count);
    }

    [Fact]
    public void IsValid_RejectsAnythingOutsideTheVocabulary()
    {
        Assert.False(RaceFinishStatusVocabulary.IsValid(null));
        Assert.False(RaceFinishStatusVocabulary.IsValid("Finished"));   // ordinal: the wire value is lowercase
        Assert.False(RaceFinishStatusVocabulary.IsValid("retired"));
        Assert.True(RaceFinishStatusVocabulary.IsValid(RaceFinishStatus.Otl));
    }
}
