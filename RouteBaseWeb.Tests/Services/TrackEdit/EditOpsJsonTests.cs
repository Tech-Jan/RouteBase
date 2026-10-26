using System.Text.Json;
using RouteBaseWeb.Services.TrackEdit;
using RouteBaseWeb.Services.Tracks;
using Xunit;

namespace RouteBaseWeb.Tests.Services.TrackEdit;

/// <summary>Wire pins for the frozen op/script JSON contract (A1 §2.1/§2.4) + the two script-level
/// validator paths (schemaVersion, op cap).</summary>
public class EditOpsJsonTests
{
    private static readonly IReadOnlyDictionary<int, DecodedTrack> NoSources = new Dictionary<int, DecodedTrack>();

    [Fact]
    public void Roundtrip_AllOpTypes_PreservesValues()
    {
        var script = new EditScript(1, new EditOp[]
        {
            new LoadActivityOp(4711),
            new CropToOp(0, 150, 8900),
            new DeleteRangeOp(0, 3200, 3450, TimeMode.Close, 4),
            new SplitAtOp(0, 5000),
            new AppendOp(0, 1, TimeMode.Synthesize, 2520),
        });

        var json = JsonSerializer.Serialize(script, TrackEditJson.Options);
        var back = JsonSerializer.Deserialize<EditScript>(json, TrackEditJson.Options)!;

        Assert.Equal(script.SchemaVersion, back.SchemaVersion);
        Assert.Equal(script.Ops.Count, back.Ops.Count);
        for (int i = 0; i < script.Ops.Count; i++)
            Assert.Equal(script.Ops[i], back.Ops[i]); // record value equality
    }

    [Fact]
    public void Serialized_UsesOpDiscriminatorAndCamelCase()
    {
        var script = new EditScript(1, new EditOp[]
        {
            new CropToOp(0, 1, 2),
            new DeleteRangeOp(0, 3, 4, TimeMode.Close, 7),
        });

        var json = JsonSerializer.Serialize(script, TrackEditJson.Options);

        Assert.Contains("\"op\":\"cropTo\"", json, StringComparison.Ordinal);
        Assert.Contains("\"timeMode\":\"close\"", json, StringComparison.Ordinal);
        Assert.Contains("\"gapSeconds\":7", json, StringComparison.Ordinal);
    }

    [Fact]
    public void GapSeconds_OmittedWhenNull()
    {
        var json = JsonSerializer.Serialize(
            new EditScript(1, new EditOp[] { new DeleteRangeOp(0, 1, 2, TimeMode.Keep, null) }),
            TrackEditJson.Options);

        Assert.DoesNotContain("gapSeconds", json, StringComparison.Ordinal);
        Assert.Contains("\"timeMode\":\"keep\"", json, StringComparison.Ordinal);
    }

    /// <summary>The [FromBody] path: MVC binds ops with its OWN JsonSerializerOptions, not
    /// TrackEditJson.Options — so the timeMode wire form must hold without them (real bug: append/cut
    /// 400'd because "keep" wouldn't bind to the enum).</summary>
    [Fact]
    public void TimeMode_BindsFromCamelCaseString_WithoutTrackEditJsonOptions()
    {
        var mvcOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web); // what [FromBody] uses
        const string body = """
            {"baseCursor":2,"ops":[{"op":"append","target":0,"source":1,"timeMode":"keep"}]}
            """;

        var request = JsonSerializer.Deserialize<AppendOpsRequest>(body, mvcOptions)!;

        var op = Assert.IsType<AppendOp>(Assert.Single(request.Ops));
        Assert.Equal(TimeMode.Keep, op.TimeMode);
        Assert.Contains("\"timeMode\":\"keep\"", JsonSerializer.Serialize<EditOp>(op, mvcOptions), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownOp_ThrowsJsonException() =>
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<EditOp>("{\"op\":\"teleport\"}", TrackEditJson.Options));

    [Fact]
    public void SchemaVersion2_ReturnsBadSchemaVersion()
    {
        var outcome = EditReplayEngine.ReplayScript(new EditScript(2, Array.Empty<EditOp>()), NoSources);

        Assert.NotNull(outcome.Error);
        Assert.Equal(TrackEditErrors.BadSchemaVersion, outcome.Error!.Code);
        Assert.Equal(-1, outcome.Error.OpIndex);
    }

    [Fact]
    public void ScriptOverMaxOps_ReturnsScriptTooLong()
    {
        var ops = Enumerable.Range(0, TrackEditLimits.MaxOps + 1)
            .Select(i => (EditOp)new LoadActivityOp(i)).ToArray();

        var outcome = EditReplayEngine.ReplayScript(new EditScript(1, ops), NoSources);

        Assert.NotNull(outcome.Error);
        Assert.Equal(TrackEditErrors.ScriptTooLong, outcome.Error!.Code);
        Assert.Equal(-1, outcome.Error.OpIndex);
    }
}
