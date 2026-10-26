using RouteBaseWeb.Services.Races;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Races;

/// <summary>
/// The canonical alias table — the replacement for six short hint lists that lived in the browser.
///
/// It exists because there is no standard: research across ~27 platforms found only Name, Time and Place
/// to be near-universal, and ChronoTrack alone documents 24 accepted spellings of "bib". Single-sourced
/// here so the guesser, the picker labels and the template writer cannot drift apart.
/// </summary>
public class RaceCsvVocabularyTests
{
    [Theory]
    // The two staged files, verbatim.
    [InlineData(RaceCsvFields.Name, new[] { "rank", "bib", "name", "year", "gender" }, "name")]
    [InlineData(RaceCsvFields.Bib, new[] { "rank", "bib", "name" }, "bib")]
    [InlineData(RaceCsvFields.Status, new[] { "name", "status", "finish" }, "status")]
    [InlineData(RaceCsvFields.Club, new[] { "name", "club_loc", "nation" }, "club_loc")]
    [InlineData(RaceCsvFields.Wave, new[] { "name", "start_block", "status" }, "start_block")]
    [InlineData(RaceCsvFields.Discipline, new[] { "name", "discipline", "status" }, "discipline")]
    [InlineData(RaceCsvFields.Rank, new[] { "rank_overall", "rank_discipline", "bib" }, "rank_overall")]
    [InlineData(RaceCsvFields.StartOffset, new[] { "name", "start_offset" }, "start_offset")]
    // German exports, which is most of what arrives here.
    [InlineData(RaceCsvFields.Bib, new[] { "Startnr.", "Name", "Zeit" }, "Startnr.")]
    [InlineData(RaceCsvFields.Finish, new[] { "Startnr.", "Name", "Zeit" }, "Zeit")]
    [InlineData(RaceCsvFields.Category, new[] { "Name", "AK", "Zeit" }, "AK")]
    [InlineData(RaceCsvFields.Gender, new[] { "Name", "Geschlecht" }, "Geschlecht")]
    [InlineData(RaceCsvFields.NetTime, new[] { "Name", "Brutto", "Netto" }, "Netto")]
    [InlineData(RaceCsvFields.GunTime, new[] { "Name", "Brutto", "Netto" }, "Brutto")]
    // EN chip/clock, the other half of the gun-vs-net pair.
    [InlineData(RaceCsvFields.NetTime, new[] { "Name", "CLOCK TIME", "CHIP TIME" }, "CHIP TIME")]
    [InlineData(RaceCsvFields.GunTime, new[] { "Name", "CLOCK TIME", "CHIP TIME" }, "CLOCK TIME")]
    public void Guess_ReadsTheHeadersRealExportsUse(string field, string[] header, string expected)
    {
        Assert.Equal(expected, RaceCsvVocabulary.Guess(header, field));
    }

    [Fact]
    public void Guess_PrefersAnExactAliasOverASubstringOne()
    {
        // `finish_time` and `finish_seconds` both contain "finish". Reading raw seconds as a formatted
        // time (or the reverse) is silent and systematic, so the exact hint must win regardless of the
        // order the columns happen to appear in.
        string[] header = ["bib", "name", "finish_seconds", "finish_time"];

        Assert.Equal("finish_time", RaceCsvVocabulary.Guess(header, RaceCsvFields.Finish));
    }

    [Fact]
    public void Guess_IgnoresCaseAndCollapsedWhitespace()
    {
        Assert.Equal("Start  Nr", RaceCsvVocabulary.Guess(["Start  Nr", "Name"], RaceCsvFields.Bib));
        Assert.Equal("BIB", RaceCsvVocabulary.Guess(["BIB", "Name"], RaceCsvFields.Bib));
    }

    [Fact]
    public void Guess_ReturnsNullRatherThanAWrongColumn()
    {
        // An unguessed picker is a question; a confidently wrong one is a silent corruption.
        Assert.Null(RaceCsvVocabulary.Guess(["a", "b", "c"], RaceCsvFields.Name));
        Assert.Null(RaceCsvVocabulary.Guess(["name", "time"], RaceCsvFields.Discipline));
    }

    [Fact]
    public void Guess_DoesNotSubstringMatchOnATwoLetterAlias()
    {
        // `no` is an alias for bib, and `nation`, `nome` and `kanton` all contain it. Two characters is
        // not a hint, it is a coincidence — so short aliases only ever match exactly.
        Assert.Null(RaceCsvVocabulary.Guess(["nation", "kanton"], RaceCsvFields.Bib));
        Assert.Equal("No", RaceCsvVocabulary.Guess(["No", "nation"], RaceCsvFields.Bib));
    }

    [Fact]
    public void Guess_AnUnknownField_IsNull_NotAThrow()
    {
        Assert.Null(RaceCsvVocabulary.Guess(["name"], "notAField"));
    }

    [Fact]
    public void Fields_AreDistinct_AndNameTheTwoThatAreRequired()
    {
        var ids = RaceCsvVocabulary.Fields.Select(f => f.Field).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        // Only Name and Finish. Everything else is optional because real files omit it — and the two that
        // are required are the two near-universal columns the research found.
        Assert.Equal(
            [RaceCsvFields.Name, RaceCsvFields.Finish],
            RaceCsvVocabulary.Fields.Where(f => f.Required).Select(f => f.Field));
    }

    [Fact]
    public void Fields_EveryOneCarriesALabelAndAHint()
    {
        // Both reach the UI — the label captions the picker, the hint is its tooltip — so a blank one is
        // an empty control rather than a missing sentence.
        Assert.All(RaceCsvVocabulary.Fields, f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Label), f.Field);
            Assert.False(string.IsNullOrWhiteSpace(f.Hint), f.Field);
            Assert.NotEmpty(f.Aliases);
        });
    }

    [Fact]
    public void Fields_AliasesAreLowercase_BecauseTheMatchIsCaseInsensitive()
    {
        // A mixed-case alias would still match, but it would read as though case mattered somewhere.
        Assert.All(RaceCsvVocabulary.Fields, f =>
            Assert.All(f.Aliases, a => Assert.Equal(a.ToLowerInvariant(), a)));
    }
}
