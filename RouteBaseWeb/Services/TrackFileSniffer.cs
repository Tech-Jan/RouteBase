using System.Text;

namespace RouteBaseWeb.Services;

/// <summary>Recognised activity-file container formats (decided by content, not extension).</summary>
public enum TrackFileFormat { Unknown, Gpx, Tcx, Fit }

/// <summary>
/// Pure magic-byte / root-element detection for uploaded activity files. Content-sniffing (not
/// extension trust) both routes the parser and validates the upload: a spoofed <c>evil.exe</c>
/// renamed <c>.gpx</c> resolves to <see cref="TrackFileFormat.Unknown"/> and is rejected before parsing.
/// Stateless + span-based so it is unit-testable without any I/O.
/// </summary>
public static class TrackFileSniffer
{
    // FIT files carry the ASCII signature ".FIT" at bytes 8–11 of the 12/14-byte header.
    private static readonly byte[] FitSignature = ".FIT"u8.ToArray();

    private static readonly char[] XmlLeadingWhitespace = { ' ', '\t', '\r', '\n' };

    /// <summary>
    /// Inspects the leading bytes of a file and returns its format. The caller (see
    /// <see cref="TrackFileParserRouter"/>, <see cref="TrackUploadPolicy"/>) decides how many bytes
    /// to peek — wider windows catch root elements sitting deeper past a long prolog/comment.
    /// </summary>
    public static TrackFileFormat Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 12 && header.Slice(8, 4).SequenceEqual(FitSignature))
            return TrackFileFormat.Fit;

        if (LooksLikeXml(header, out var text))
        {
            // Root element may sit after an <?xml …?> prolog and namespace decls, so scan the
            // decoded head rather than only the first tag.
            if (text.Contains("<TrainingCenterDatabase", StringComparison.Ordinal))
                return TrackFileFormat.Tcx;
            if (text.Contains("<gpx", StringComparison.Ordinal))
                return TrackFileFormat.Gpx;
        }

        return TrackFileFormat.Unknown;
    }

    /// <summary>
    /// True if the bytes decode (per BOM, or a UTF-16 no-BOM heuristic) to text that begins,
    /// after leading whitespace, with '&lt;' — i.e. XML. Emits the decoded text so the caller can
    /// scan for the root element.
    /// </summary>
    private static bool LooksLikeXml(ReadOnlySpan<byte> header, out string text)
    {
        if (TryDecodeUtf16(header, littleEndian: true, out text)) return true;
        if (TryDecodeUtf16(header, littleEndian: false, out text)) return true;

        var bytes = header;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            bytes = bytes[3..]; // strip UTF-8 BOM

        text = Encoding.UTF8.GetString(bytes).TrimStart(XmlLeadingWhitespace);
        return text.Length > 0 && text[0] == '<';
    }

    /// <summary>
    /// Detects UTF-16 (BOM'd, or — per XML 1.0 Appendix F — the ASCII-range null-byte-alternation
    /// pattern when no BOM is present) and decodes the header for root-element scanning.
    /// </summary>
    private static bool TryDecodeUtf16(ReadOnlySpan<byte> header, bool littleEndian, out string text)
    {
        text = "";
        bool hasBom = littleEndian
            ? header.Length >= 2 && header[0] == 0xFF && header[1] == 0xFE
            : header.Length >= 2 && header[0] == 0xFE && header[1] == 0xFF;

        if (!hasBom && !LooksLikeNoBomUtf16(header, littleEndian))
            return false;

        var body = hasBom ? header[2..] : header;
        text = (littleEndian ? Encoding.Unicode : Encoding.BigEndianUnicode)
            .GetString(body).TrimStart(XmlLeadingWhitespace);
        return text.Length > 0 && text[0] == '<';
    }

    private static bool LooksLikeNoBomUtf16(ReadOnlySpan<byte> header, bool littleEndian)
    {
        int sampleLength = Math.Min(header.Length, 32) / 2 * 2;
        if (sampleLength < 8) return false;

        int zeroAt = littleEndian ? 1 : 0;
        for (int i = zeroAt; i < sampleLength; i += 2)
            if (header[i] != 0x00) return false;
        return true;
    }
}
