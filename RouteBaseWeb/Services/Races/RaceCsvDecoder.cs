using System.Text;

namespace RouteBaseWeb.Services.Races;

/// <summary>The decoded file plus the encoding it turned out to be — the label is shown in the preview.</summary>
public sealed record RaceCsvText(string Text, string Encoding);

/// <summary>
/// Bytes → text. Pure, and the reason the upload became multipart: <c>Blob.text()</c> in the browser is
/// UNCONDITIONALLY UTF-8, so a Windows-1252 export (which is most of what European timing software emits)
/// arrived with U+FFFD in every accented name and nothing downstream could tell. Only the server sees the
/// bytes, so only the server can make this call.
/// </summary>
public static class RaceCsvDecoder
{
    public const string Utf8 = "utf-8";
    public const string Utf8Bom = "utf-8-bom";
    public const string Windows1252 = "windows-1252";

    /// <summary>
    /// Decode, preferring UTF-8 and falling back to Windows-1252.
    ///
    /// The test is STRICT UTF-8 validity, not a byte histogram: a file that decodes cleanly as UTF-8 is
    /// UTF-8 (a Windows-1252 file only decodes cleanly by accident, and only if it is pure ASCII, in which
    /// case the two agree byte for byte anyway). One that does not is Windows-1252 — the practical
    /// alternative in this domain, and a superset of ISO-8859-1 in the range that differs.
    /// </summary>
    public static RaceCsvText Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        // A BOM is the file telling us outright. Consume it here rather than leaving U+FEFF glued to the
        // first header name (RaceCsvParser strips one too — belt and braces, since it also parses text
        // that never came through this path).
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new RaceCsvText(new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3), Utf8Bom);

        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return new RaceCsvText(strict.GetString(bytes), Utf8);
        }
        catch (DecoderFallbackException)
        {
            return new RaceCsvText(FromWindows1252(bytes), Windows1252);
        }
    }

    /// <summary>
    /// Windows-1252 without the CodePages package.
    ///
    /// CP1252 IS Latin-1 except for 0x80–0x9F, where Latin-1 has unused control codes and CP1252 has the
    /// punctuation European exports actually use — the curly apostrophe in <c>O’Brien</c> is 0x92. So:
    /// decode as Latin-1 (built in since .NET 5) and patch those 32 code points. Adding a NuGet dependency
    /// for one 32-entry table would be the more fragile choice, not the safer one.
    /// </summary>
    private static string FromWindows1252(byte[] bytes)
    {
        var chars = Encoding.Latin1.GetString(bytes).ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c is >= '' and <= '') chars[i] = HighRange[c - 0x80];
        }
        return new string(chars);
    }

    /// <summary>CP1252's 0x80–0x9F. <c>�</c> marks the five positions CP1252 leaves undefined.</summary>
    private static readonly char[] HighRange =
    [
        '€', '�', '‚', 'ƒ', '„', '…', '†', '‡',
        'ˆ', '‰', 'Š', '‹', 'Œ', '�', 'Ž', '�',
        '�', '‘', '’', '“', '”', '•', '–', '—',
        '˜', '™', 'š', '›', 'œ', '�', 'ž', 'Ÿ',
    ];
}
