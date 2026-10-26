using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.TrackEdit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services.TrackEdit;

/// <summary>
/// Photos follow the merge. A merged ride that shows none of the photos taken on its legs is wrong, but a
/// merge that strands them on rides now sitting in the trash is worse — and a revert has to be able to send
/// each one home again. The photo is the only thing that knows where home was (<c>merged_from_track_id</c>).
/// </summary>
public class MergePhotosTests
{
    private const string Me = "me";

    private static async Task<SaveResponse> MergeAsync(TrackEditTestDb db, int baseId, int sourceId)
    {
        var svc = db.ServiceFor(Me);
        var state = (await svc.CreateAsync(new CreateSessionRequest([baseId, sourceId]))).Response!;
        state = (await svc.AppendOpsAsync(state.SessionId,
            new AppendOpsRequest(state.Cursor, [new AppendOp(0, 1, TimeMode.Keep, null)]))).Response!;
        var claims = state.SlotCounts.Select(c => new SlotCountClaim(c.SlotId, c.PointCount)).ToList();
        var r = await db.SaveServiceFor(Me).SaveAsync(state.SessionId, "replace", claims);
        Assert.Null(r.ErrorKind);
        return r.Response!;
    }

    private static async Task<int> AddPhotoAsync(TrackEditTestDb db, TrackId trackId)
    {
        var photo = new Photo { TrackId = trackId, UserId = Me, ImportedUtc = DateTime.UtcNow };
        db.Context.Photos.Add(photo);
        await db.Context.SaveChangesAsync();
        db.Context.ChangeTracker.Clear();
        return photo.PhotoId.Value;
    }

    private static async Task<Photo> PhotoAsync(TrackEditTestDb db, int photoId) =>
        await db.Context.Photos.AsNoTracking().FirstAsync(p => p.PhotoId == new PhotoId(photoId));

    private static async Task<TrackEditTestDb> TwoRidesAsync()
    {
        var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me, name: "Base");
        await db.SeedRampAsync(new TrackId(101), 10);
        await db.AddActivityAsync(new TrackId(202), Me, name: "Leg 2");
        await db.SeedRampAsync(new TrackId(202), 6);
        return db;
    }

    [Fact]
    public async Task Merge_MovesEveryInputsPhotos_OntoTheMergedRide()
    {
        await using var db = await TwoRidesAsync();
        int onBase = await AddPhotoAsync(db, new TrackId(101));
        int onSource = await AddPhotoAsync(db, new TrackId(202));

        var save = await MergeAsync(db, 101, 202);
        int mergedId = Assert.Single(save.Slots).TrackId;

        foreach (var (photoId, from) in new[] { (onBase, 101), (onSource, 202) })
        {
            var p = await PhotoAsync(db, photoId);
            Assert.Equal(new TrackId(mergedId), p.TrackId);        // shows on the merged ride…
            Assert.Equal(new TrackId(from), p.MergedFromTrackId);  // …and remembers where it came from
        }
    }

    [Fact]
    public async Task RevertOfAMerge_SendsEveryPhotoHome()
    {
        await using var db = await TwoRidesAsync();
        int onBase = await AddPhotoAsync(db, new TrackId(101));
        int onSource = await AddPhotoAsync(db, new TrackId(202));
        var save = await MergeAsync(db, 101, 202);

        var r = await db.RevertServiceFor(Me).RevertAsync(new CommitId(save.CommitId));
        Assert.Null(r.ErrorKind);

        // Back on their own rides, with no merge-origin left over — and, crucially, NOT cascade-deleted with
        // the merged ride the revert just purged.
        foreach (var (photoId, home) in new[] { (onBase, 101), (onSource, 202) })
        {
            var p = await PhotoAsync(db, photoId);
            Assert.Equal(new TrackId(home), p.TrackId);
            Assert.Null(p.MergedFromTrackId);
        }
    }

    /// <summary>
    /// Purging a merge INPUT must not take the merged ride's photos with it. The photo already lives on the
    /// merged ride; only its way home is lost — which is exactly what the "this merge can never be undone"
    /// confirmation warns about, and the FK (ON DELETE SET NULL) says so rather than cascading the photo away.
    /// </summary>
    [Fact]
    public async Task PurgingAnInput_KeepsThePhotoOnTheMergedRide_AndOnlyLosesItsWayHome()
    {
        await using var db = await TwoRidesAsync();
        int onSource = await AddPhotoAsync(db, new TrackId(202));
        var save = await MergeAsync(db, 101, 202);
        int mergedId = Assert.Single(save.Slots).TrackId;

        await db.PurgeActivityAsync(new TrackId(202));   // the user emptied the trash, confirmation and all

        var p = await PhotoAsync(db, onSource);
        Assert.Equal(new TrackId(mergedId), p.TrackId);   // still on the merged ride
        Assert.Null(p.MergedFromTrackId);    // but there is nowhere left to put it back
    }
}
