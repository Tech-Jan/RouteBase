using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.TrackEdit;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.TrackEdit;

/// <summary>
/// The post-save/revert derive chain (A2 §5.3 as amended by §14.4 → segments + photos only). Fakes record
/// the ordered seam calls; the service owns bucket routing, remove-before-match ordering, threshold parity,
/// and per-track failure isolation.
/// </summary>
public class TrackEditDeriveServiceTests
{
    private const string User = "u1";

    private static (TrackEditDeriveService Svc, FakeSegmentAnalysis Seg, FakePhotoCommand Photo) Build()
    {
        var seg = new FakeSegmentAnalysis();
        var photo = new FakePhotoCommand();
        var svc = new TrackEditDeriveService(
            seg, photo, new UserCacheInvalidator(), NullLogger<TrackEditDeriveService>.Instance);
        return (svc, seg, photo);
    }

    private static TrackEditDerivePayload Payload(
        IReadOnlyList<int>? full = null, IReadOnlyList<int>? match = null, IReadOnlyList<int>? remove = null) =>
        new(User, 1, full ?? [], match ?? [], remove ?? []);

    [Fact]
    public async Task FullDerive_CallsRemoveThenMatch_InOrder_WithEffectiveThreshold()
    {
        var (svc, seg, _) = Build();
        seg.Threshold = 55;

        await svc.RunAsync(Payload(full: [7]), default);

        Assert.Equal(new[] { "remove:7:u1", "match:7:u1:55" }, seg.Calls, StringComparer.Ordinal);
    }

    [Fact]
    public async Task MatchOnly_SkipsStatsPhotosRemove()
    {
        var (svc, seg, photo) = Build();

        await svc.RunAsync(Payload(match: [8]), default);

        Assert.Equal(new[] { "match:8:u1:40" }, seg.Calls, StringComparer.Ordinal);
        Assert.Empty(photo.Rematched);
    }

    [Fact]
    public async Task RemoveOnly_OnlyRemovesFromSegments()
    {
        var (svc, seg, photo) = Build();

        await svc.RunAsync(Payload(remove: [9]), default);

        Assert.Equal(new[] { "remove:9:u1" }, seg.Calls, StringComparer.Ordinal);
        Assert.Empty(photo.Rematched);
    }

    [Fact]
    public async Task FullDerive_RematchesPhotos()
    {
        var (svc, _, photo) = Build();

        await svc.RunAsync(Payload(full: [7]), default);

        Assert.Equal(new[] { 7 }, photo.Rematched);
    }

    [Fact]
    public async Task MissingTrack_SkipsWithWarning_ChainContinues()
    {
        var (svc, seg, photo) = Build();
        seg.ThrowRemoveForTrack = new TrackId(7);   // first full-derive id blows up in remove

        await svc.RunAsync(Payload(full: [7, 8]), default);

        Assert.DoesNotContain("match:7:u1:40", seg.Calls, StringComparer.Ordinal);       // 7 aborted after the throw
        Assert.Contains("match:8:u1:40", seg.Calls, StringComparer.Ordinal);             // 8 still fully processed
        Assert.Equal(new[] { 8 }, photo.Rematched);
    }
}
