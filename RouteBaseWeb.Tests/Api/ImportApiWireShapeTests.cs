using System.Text.Json;
using RouteBaseWeb.Models.Uploads;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins the wire shape of ImportApiController's success/policy-rejected bodies
/// (Models/Uploads/BatchUploadContracts.cs) — shared with PhotosApiController's identical batch-upload
/// lifecycle. import.ts reads only batchId from the begin response; the rest are checked via resp.ok.
/// </summary>
public class ImportApiWireShapeTests
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
    public void BeginBatch_KeySetAndValue()
    {
        var e = Serialize(new BeginBatchResponse("batch-1"));
        Assert.Equal(new[] { "batchId" }, Keys(e));
        Assert.Equal("batch-1", e.GetProperty("batchId").GetString());
    }

    [Fact]
    public void StagedFiles_KeySetAndValue()
    {
        var e = Serialize(new StagedFilesResponse(3));
        Assert.Equal(new[] { "staged" }, Keys(e));
        Assert.Equal(3, e.GetProperty("staged").GetInt32());
    }

    [Fact]
    public void PolicyRejected_KeySetAndValues()
    {
        var e = Serialize(new PolicyRejectedBody("Too many files", new[] { "a.gpx", "b.gpx" }));
        Assert.Equal(new[] { "code", "files", "message" }, Keys(e)); // D32: { code, message, files }
        Assert.Equal("badRequest", e.GetProperty("code").GetString());
        Assert.Equal(2, e.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public void CommitBatch_KeySetAndValues()
    {
        var e = Serialize(new CommitBatchResponse("batch-1", 5));
        Assert.Equal(new[] { "batchId", "queued" }, Keys(e));
        Assert.Equal(5, e.GetProperty("queued").GetInt32());
    }

    [Fact]
    public void AbortBatch_KeySetAndValue()
    {
        var e = Serialize(new AbortBatchResponse(true));
        Assert.Equal(new[] { "aborted" }, Keys(e));
        Assert.True(e.GetProperty("aborted").GetBoolean());
    }
}
