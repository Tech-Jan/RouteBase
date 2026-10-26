namespace RouteBaseWeb.Services.Uploads;

/// <summary>
/// Peeks each uploaded file's header (for content-sniffing) and size into upload-guard candidates —
/// the <c>BuildCandidatesAsync</c> twin that lived in both batch controllers (CR5-02).
/// <paramref name="headerBytes"/> is the feature policy's <c>HeaderBytes</c> constant.
/// </summary>
public static class UploadCandidateReader
{
    public static async Task<List<UploadCandidate>> ReadAsync(
        IReadOnlyList<IFormFile> files, int headerBytes, CancellationToken ct)
    {
        var candidates = new List<UploadCandidate>(files.Count);
        foreach (var file in files)
        {
            var header = new byte[headerBytes];
            int read = 0;
            await using (var stream = file.OpenReadStream())
            {
                int r;
                while (read < header.Length && (r = await stream.ReadAsync(header.AsMemory(read), ct)) > 0)
                    read += r;
            }
            candidates.Add(new UploadCandidate(
                file.FileName, file.Length, read == header.Length ? header : header[..read]));
        }
        return candidates;
    }
}
