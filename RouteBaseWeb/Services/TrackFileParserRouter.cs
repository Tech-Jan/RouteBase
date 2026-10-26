using RouteBaseWeb.Models.Gpx;
using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Services;

/// <summary>
/// Content-sniffing <see cref="ITrackFileParser"/>: peeks the leading bytes (see
/// <see cref="TrackFileSniffer"/>), then delegates to the matching format parser. Everything
/// downstream of the orchestrator (dedup, timezone, persist) is format-agnostic and unchanged.
/// </summary>
public class TrackFileParserRouter : ITrackFileParser
{
    // Enough to clear an XML prolog + namespace decls/comments and reach the root element, even
    // for a UTF-16-encoded file (2 bytes/char roughly halves the effective text budget).
    private const int SniffBytes = 4096;

    private readonly IGpxParsingService _gpx;
    private readonly ITcxParsingService _tcx;
    private readonly IFitParsingService _fit;

    public TrackFileParserRouter(IGpxParsingService gpx, ITcxParsingService tcx, IFitParsingService fit)
    {
        _gpx = gpx;
        _tcx = tcx;
        _fit = fit;
    }

    public IReadOnlyList<GpxParseResult> Parse(Stream stream, string fileName, bool useFilenameAsId = false)
    {
        // Staged files are seekable; buffer defensively if a caller hands us a forward-only stream
        // so we can both sniff the head and hand a fresh-positioned stream to the parser.
        Stream seekable = stream;
        if (!stream.CanSeek)
        {
            var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;
            seekable = ms;
        }

        var format = SniffFormat(seekable);

        IReadOnlyList<GpxParseResult> results = format switch
        {
            TrackFileFormat.Gpx => _gpx.Parse(seekable, fileName, useFilenameAsId),
            TrackFileFormat.Tcx => _tcx.Parse(seekable, fileName, useFilenameAsId),
            TrackFileFormat.Fit => _fit.Parse(seekable, fileName, useFilenameAsId),
            _                   => [new GpxParseResult { Success = false, ErrorMessage = "Unrecognized file format." }],
        };

        // A1 (2026-07-20) + BUG-1/OBS-1 (2026-07-22): format-agnostic guard, applied per result (a chained
        // FIT yields several) — sanitize the name and reject non-finite / out-of-range stats, per activity,
        // so a guard failure fails only the offending activity, not its chained siblings.
        return results.Select(GuardResult).ToList();
    }

    private static GpxParseResult GuardResult(GpxParseResult result)
    {
        if (result.Success && result.Summary != null)
        {
            // BUG-1 (2026-07-22): the finalized name arrives here from every parser — a GPX <name>
            // (already sanitized), or the raw upload FILENAME (GPX <name>-less fallback, all TCX/FIT).
            // Sanitize once at this single seam so a hostile filename can't smuggle control chars or
            // an unbounded length into the DB; idempotent on already-clean <name> values.
            result.Summary.Name = ImportNameSanitizer.Sanitize(result.Summary.Name);

            // A1 (2026-07-20): format-agnostic aggregate guard. Parsers finite-check each value, but a
            // sum can overflow to Infinity during aggregation — reject here so a non-finite stat is a
            // failed import for every format, never a DB row.
            if (TrackStatsCalculator.FindNonFiniteAggregate(result.Summary) is { } nonFinite)
            {
                return new GpxParseResult
                {
                    Success = false,
                    ErrorMessage = $"Computed statistic {nonFinite} is not finite; the file contains corrupt or extreme values.",
                };
            }

            // OBS-1 (2026-07-22): device/lap/extension totals are only finite-checked, so an
            // impossible-but-finite value (double.MaxValue distance) or a negative one (negative
            // ascent/calories/lap totals) still persists. Reject out-of-range stats here too, same
            // contract as the non-finite path.
            if (TrackStatsCalculator.FindOutOfRangeStat(result.Summary) is { } outOfRange)
            {
                return new GpxParseResult
                {
                    Success = false,
                    ErrorMessage = $"Imported statistic {outOfRange} is outside the accepted range; the file contains corrupt or extreme values.",
                };
            }
        }

        return result;
    }

    private static TrackFileFormat SniffFormat(Stream seekable)
    {
        var header = new byte[SniffBytes];
        long pos = seekable.Position;
        int n = seekable.Read(header, 0, header.Length);
        seekable.Position = pos;
        return TrackFileSniffer.Detect(header.AsSpan(0, n));
    }
}
