using RouteBaseWeb.Services.Races;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Races;

/// <summary>
/// CSV reading. The case that matters is the one the real file is full of: European timing exports use
/// a DECIMAL COMMA inside a comma-separated file (<c>"1:16:59,4"</c>), so quote handling is load-bearing,
/// not paranoia.
/// </summary>
public class RaceCsvParserTests
{
    [Fact]
    public void Parse_KeepsCommasInsideQuotedFields()
    {
        var csv = "name,finish\nKURZ Felix,\"1:16:59,4\"\n";

        var table = RaceCsvParser.Parse(csv);

        Assert.Equal(new[] { "name", "finish" }, table.Header);
        Assert.Single(table.Rows);
        Assert.Equal("KURZ Felix", table.Rows[0][0]);
        Assert.Equal("1:16:59,4", table.Rows[0][1]);   // NOT split into two fields
    }

    [Fact]
    public void Parse_HandlesEscapedQuotes()
    {
        var csv = "name,club\nX,\"The \"\"Fast\"\" Team\"\n";

        var table = RaceCsvParser.Parse(csv);

        Assert.Equal("The \"Fast\" Team", table.Rows[0][1]);
    }

    [Fact]
    public void Parse_SkipsBlankLinesAndHandlesCrLf()
    {
        var csv = "name,finish\r\nA,1:00:00\r\n\r\nB,2:00:00\r\n";

        var table = RaceCsvParser.Parse(csv);

        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("B", table.Rows[1][0]);
    }

    [Fact]
    public void Parse_HeaderOnly_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => RaceCsvParser.Parse("name,finish\n"));
    }

    // ── delimiter ──────────────────────────────────────────────────────────────────────────────────
    //
    // Semicolon is the NORM in DE/Nordic timing exports (SportIdent OE CSV is semicolon by default) and
    // tab is what a spreadsheet paste produces. Read with a hard-coded comma, such a file is ONE column:
    // every row then has no usable time, every row is skipped — and the import used to delete the whole
    // existing field before discovering it had nobody to put back.

    [Theory]
    [InlineData(';')]
    [InlineData('\t')]
    [InlineData('|')]
    [InlineData(',')]
    public void Parse_SniffsTheDelimiterFromTheHeader(char d)
    {
        var csv = $"bib{d}name{d}finish\n4496{d}KURZ Felix{d}1:16:59.4\n";

        var table = RaceCsvParser.Parse(csv);

        Assert.Equal(new[] { "bib", "name", "finish" }, table.Header);
        Assert.Equal(new[] { "4496", "KURZ Felix", "1:16:59.4" }, table.Rows[0]);
    }

    [Fact]
    public void Parse_Semicolons_LeaveTheDecimalCommaAlone()
    {
        // The reason semicolon exists in these exports at all: a decimal comma in an unquoted time cell.
        var csv = "name;finish\nKURZ Felix;1:16:59,4\n";

        var table = RaceCsvParser.Parse(csv);

        Assert.Equal("1:16:59,4", table.Rows[0][1]);
        Assert.Equal(4619.4, RaceCsvParser.ParseElapsedSeconds(table.Rows[0][1])!.Value, 3);
    }

    [Fact]
    public void Parse_QuotedHeaderField_CannotVoteForADelimiterTheFileDoesNotUse()
    {
        // A comma inside a quoted header cell is not a separator, and counting it as one would split a
        // semicolon file on the wrong character.
        var csv = "name;\"club, town\";finish\nA;\"Bern, RV\";1:00:00\n";

        var table = RaceCsvParser.Parse(csv);

        Assert.Equal(new[] { "name", "club, town", "finish" }, table.Header);
        Assert.Equal("Bern, RV", table.Rows[0][1]);
    }

    [Fact]
    public void Parse_TieGoesToTheComma()
    {
        // One of each: no candidate wins on count, so the default stands rather than a coin toss.
        var csv = "name,club;x\nA,B;C\n";

        Assert.Equal(new[] { "name", "club;x" }, RaceCsvParser.Parse(csv).Header);
    }

    [Fact]
    public void Parse_StripsAUtf8Bom_SoTheFirstColumnIsMappable()
    {
        // U+FEFF survives File.ReadAllText / Blob.text() and would glue itself to the first header name,
        // leaving the user's `bib` column present but unmappable.
        var table = RaceCsvParser.Parse("﻿bib,name\n4496,KURZ Felix\n");

        Assert.Equal("bib", table.Header[0]);
    }

    // ── unbalanced quotes ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_BareQuoteMidField_IsALiteral_NotTheStartOfAQuotedSpan()
    {
        // `29" Wheelers` is a real club name. Treated as an opening quote it swallowed every following
        // row into one cell — the whole rest of the file, silently. Python's csv and Excel both read it
        // as a literal character; being stricter than the standard library was data loss, not rigour.
        var csv = "name,club\nA,29\" Wheelers\nB,Velo Club\nC,RV Bern\n";

        var table = RaceCsvParser.Parse(csv);

        Assert.Equal(3, table.Rows.Count);
        Assert.Equal("29\" Wheelers", table.Rows[0][1]);
        Assert.Equal("Velo Club", table.Rows[1][1]);
        Assert.Equal("RV Bern", table.Rows[2][1]);
    }

    [Fact]
    public void Parse_QuoteAtFieldStart_StillOpensAQuotedField()
    {
        // The fix must not cost the feature it guards: a properly quoted field is still quoted.
        var csv = "name,finish\nA,\"1:16:59,4\"\n";

        Assert.Equal("1:16:59,4", RaceCsvParser.Parse(csv).Rows[0][1]);
    }

    [Theory]
    [InlineData("1:16:59,4", 4619.4)]     // the real format: h:mm:ss with a decimal comma
    [InlineData("1:16:59.4", 4619.4)]     // ...and with a decimal point
    [InlineData("2:29:06", 8946)]         // the reference ride
    [InlineData("59:12,1", 3552.1)]       // mm:ss
    [InlineData("0:00", 0)]
    [InlineData("4619.4", 4619.4)]        // bare number = seconds (our own exporter's finish_seconds)
    public void ParseElapsedSeconds_ReadsEveryFormWeHaveSeen(string input, double expected)
    {
        Assert.Equal(expected, RaceCsvParser.ParseElapsedSeconds(input)!.Value, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    [InlineData("DNF")]
    [InlineData("DNS")]
    [InlineData(null)]
    public void ParseElapsedSeconds_NonTimes_AreNullNotZero(string? input)
    {
        // A DNF is a rider with no time, not a rider who finished in 0 seconds — which would put him
        // first on the results and at the finish line from the gun.
        Assert.Null(RaceCsvParser.ParseElapsedSeconds(input));
    }

    [Theory]
    [InlineData("1:2:3:4")]      // too many parts
    [InlineData("1:75")]         // 75 seconds
    [InlineData("abc")]
    public void ParseElapsedSeconds_Nonsense_IsNull(string input)
    {
        Assert.Null(RaceCsvParser.ParseElapsedSeconds(input));
    }

    /// <summary>
    /// A cell that is not a number must not become one.
    ///
    /// <c>double.TryParse</c> has been IEEE-compliant since .NET Core 3.0, and that is the trap: it parses
    /// "NaN" and "Infinity" SUCCESSFULLY (InvariantCulture's own symbols), and it overflows "1e400" to +∞
    /// rather than failing. "nan" is an ordinary thing to find in a results file — pandas, R and Excel all
    /// write it for a missing value, and the column mapping is the user's to get wrong.
    ///
    /// The seconds guard on the h:mm:ss path cannot reject a NaN either: <c>secs &lt; 0 || secs &gt;= 60</c>
    /// is FALSE for NaN, because every comparison with NaN is false. Same defeated-bounds-check as the
    /// cost-curve bug.
    ///
    /// A non-finite time is not a slow rider — it is NO time. Left in, the import persists it and the next
    /// replay request dies: System.Text.Json refuses to write NaN and throws AFTER the body has begun
    /// streaming, so the browser gets HTTP 200 with a truncated body and reports a bare "Failed to fetch".
    /// See RaceWireContractTests for that half of the chain.
    /// </summary>
    [Theory]
    [InlineData("NaN")]
    [InlineData("nan")]          // how pandas/R/Excel spell a missing value
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e400")]        // overflows to +∞ — and TryParse returns TRUE
    [InlineData("0:00:NaN")]     // defeats `secs < 0 || secs >= 60`
    public void ParseElapsedSeconds_NonFinite_IsNull(string input)
    {
        Assert.Null(RaceCsvParser.ParseElapsedSeconds(input));
    }

    /// <summary>
    /// BE-01: a NEGATIVE time is no time. A rider cannot cross a checkpoint before the start, and a
    /// negative elapsed/leg is the same bug class as the non-finite one — it defeats no existing guard
    /// (the finiteness check passes a finite negative), reaches the wire in default mode, and in per-leg
    /// mode a negative leg makes the accumulated splits run BACKWARDS (a rider "reaching" a later
    /// checkpoint earlier). Rejecting it at this one choke point closes both symptoms.
    /// </summary>
    [Theory]
    [InlineData("-100")]         // bare seconds
    [InlineData("-5:30")]        // mm:ss with a negative minutes place
    [InlineData("-1:00:00")]     // h:mm:ss
    [InlineData("-0.5")]
    public void ParseElapsedSeconds_Negative_IsNull(string input)
    {
        Assert.Null(RaceCsvParser.ParseElapsedSeconds(input));
    }
}
