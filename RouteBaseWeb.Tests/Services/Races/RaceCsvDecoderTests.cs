using System.Text;
using RouteBaseWeb.Services.Races;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Races;

/// <summary>
/// Bytes → text. The reason the upload became multipart: <c>Blob.text()</c> in the browser is
/// unconditionally UTF-8, so a Windows-1252 export — which is most of what European timing software
/// emits — arrived with U+FFFD in every accented name and nothing downstream could tell.
/// </summary>
public class RaceCsvDecoderTests
{
    /// <summary>The bytes a Windows-1252 encoder would produce, built by hand so the test needs no
    /// CodePages provider either. Latin-1 covers everything except the 0x80–0x9F block.</summary>
    private static byte[] Cp1252(string text) => Encoding.Latin1.GetBytes(text);

    [Fact]
    public void Decode_PlainAscii_IsUtf8()
    {
        var result = RaceCsvDecoder.Decode("name,finish\nA,1:00:00\n"u8.ToArray());

        Assert.Equal(RaceCsvDecoder.Utf8, result.Encoding);
        Assert.StartsWith("name,finish", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_Utf8WithAccents_KeepsThem()
    {
        var bytes = new UTF8Encoding(false).GetBytes("name\nMÜLLER Jürgen\n");

        var result = RaceCsvDecoder.Decode(bytes);

        Assert.Equal(RaceCsvDecoder.Utf8, result.Encoding);
        Assert.Contains("MÜLLER Jürgen", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_StripsAUtf8Bom_AndSaysSo()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat("bib,name\n1,A\n"u8.ToArray()).ToArray();

        var result = RaceCsvDecoder.Decode(bytes);

        Assert.Equal(RaceCsvDecoder.Utf8Bom, result.Encoding);
        // Not glued to the first header name, where it would leave `bib` present but unmappable.
        Assert.StartsWith("bib,name", result.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The live case. `MÜLLER` in CP1252 is a single 0xDC byte, which is not valid UTF-8 — strict UTF-8
    /// decoding throws, and the fallback is what saves the name. Decoded as UTF-8 with replacement (which
    /// is what the browser did) it became `M?LLER`, and the rider's name was corrupted in the database.
    /// </summary>
    [Fact]
    public void Decode_Windows1252Accents_AreRecovered_NotReplaced()
    {
        var result = RaceCsvDecoder.Decode(Cp1252("name\nMÜLLER Jürgen\n"));

        Assert.Equal(RaceCsvDecoder.Windows1252, result.Encoding);
        Assert.Contains("MÜLLER Jürgen", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('�', result.Text);
    }

    /// <summary>
    /// CP1252's 0x80–0x9F block is where it differs from Latin-1, and it is not an academic difference:
    /// 0x92 is the curly apostrophe in `O’Brien`, which every Word-exported results list is full of.
    /// Latin-1 would decode it as an unprintable control character.
    /// </summary>
    [Theory]
    [InlineData(0x92, '’')]
    [InlineData(0x93, '“')]
    [InlineData(0x94, '”')]
    [InlineData(0x96, '–')]
    [InlineData(0x80, '€')]
    public void Decode_Windows1252HighRange_MapsToTheRightPunctuation(byte raw, char expected)
    {
        // A 0xFF byte forces the strict-UTF-8 decode to fail, so the file takes the CP1252 path.
        var bytes = new byte[] { (byte)'n', (byte)'\n', raw, 0xFF, (byte)'\n' };

        var result = RaceCsvDecoder.Decode(bytes);

        Assert.Equal(RaceCsvDecoder.Windows1252, result.Encoding);
        Assert.Contains(expected, result.Text);
    }

    [Fact]
    public void Decode_PrefersUtf8WhenTheBytesAreValidUtf8()
    {
        // A file that decodes cleanly as UTF-8 IS UTF-8: the sequence 0xC3 0x9C is `Ü` in UTF-8 and
        // `Ãœ` in CP1252, and guessing the second would corrupt a correct file to fix a broken one.
        var result = RaceCsvDecoder.Decode(new UTF8Encoding(false).GetBytes("Ü"));

        Assert.Equal(RaceCsvDecoder.Utf8, result.Encoding);
        Assert.Equal("Ü", result.Text);
    }

    [Fact]
    public void Decode_EmptyInput_IsEmpty_NotAThrow()
    {
        // The endpoint rejects a missing file before this; an empty one must not blow up the decoder.
        Assert.Equal(string.Empty, RaceCsvDecoder.Decode([]).Text);
    }
}
