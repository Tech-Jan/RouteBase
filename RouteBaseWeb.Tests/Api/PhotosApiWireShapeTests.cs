using System.Text.Json;
using RouteBaseWeb.Models.Photos;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins the wire shape of PhotosApiController's photo-specific success/conflict bodies
/// (Models/Photos/PhotoContracts.cs) — the shared batch-upload lifecycle bodies are pinned by
/// ImportApiWireShapeTests (Models/Uploads/BatchUploadContracts.cs, identical shapes on both
/// controllers). Most of these are unread by the TS client (activity-photos.ts / pages/import.ts /
/// pages/photos.ts all check resp.ok only), named per the DTO convention regardless.
/// </summary>
public class PhotosApiWireShapeTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonElement Serialize(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, Web)).RootElement.Clone();

    private static string[] Keys(JsonElement e)
    {
        var keys = new List<string>();
        foreach (var p in e.EnumerateObject()) keys.Add(p.Name);
        keys.Sort(StringComparer.Ordinal);
        return keys.ToArray();
    }

    [Fact]
    public void PhotoBeginBatch_KeySet()
    {
        var e = Serialize(new PhotoBeginBatchResponse("batch-1", 1024, 42));
        Assert.Equal(new[] { "batchId", "previewMaxEdge", "trackId" }, Keys(e));
    }

    [Fact]
    public void AssignPhoto_KeySetAndValues()
    {
        var e = Serialize(new AssignPhotoResponse(7, "imported"));
        Assert.Equal(new[] { "outcome", "photoId" }, Keys(e));
        Assert.Equal(7, e.GetProperty("photoId").GetInt32());
    }

    [Fact]
    public void DuplicatePhotoConflict_KeySet()
    {
        var e = Serialize(new DuplicatePhotoConflictBody("duplicate photo", "duplicate"));
        Assert.Equal(new[] { "code", "message", "outcome" }, Keys(e)); // D32: { code, message, outcome }
        Assert.Equal("conflict", e.GetProperty("code").GetString());
    }

    [Fact]
    public void PhotoLocation_KeySetAndValues()
    {
        var e = Serialize(new PhotoLocationResponse(7, 47.0, 11.0));
        Assert.Equal(new[] { "lat", "lng", "photoId" }, Keys(e));
    }

    [Fact]
    public void DeletedCount_KeySetAndValue()
    {
        var e = Serialize(new DeletedCountResponse(3));
        Assert.Equal(new[] { "deleted" }, Keys(e));
        Assert.Equal(3, e.GetProperty("deleted").GetInt32());
    }
}
