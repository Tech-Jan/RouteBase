using System.Globalization;
using System.Text.Json;
using RouteBaseWeb.Services.TrackEdit;

namespace RouteBaseWeb.Tests.Services.TrackEdit;

public sealed record FixtureExpectError(int OpIndex, string Code);

public sealed record FixtureOrigin(int? ActivityId, int? SplitFromSlotId);

public sealed record FixtureSlot(int SlotId, FixtureOrigin Origin, Dictionary<string, long?[]> Channels);

/// <summary>One parity fixture (A1 §2.9). Channel values are quantized ints on the codec grid; <c>null</c>
/// entry = a missing sample; an absent channel key = a whole-NULL channel.</summary>
public sealed class FixtureCase
{
    public required string FileName { get; init; }
    public required string Name { get; init; }
    public required Dictionary<int, Dictionary<string, long?[]>> Sources { get; init; }
    public required EditScript Script { get; init; }
    public List<FixtureSlot>? ExpectSlots { get; init; }
    public FixtureExpectError? ExpectError { get; init; }

    public override string ToString() => FileName;
}

/// <summary>Loads the shared JSON parity fixtures the C# suite consumes (the vitest twin reads the same
/// files). Auto-discovers — adding a fixture is adding a file (no registration).</summary>
public static class FixtureLoader
{
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "TestData", "TrackEdit");

    public static IReadOnlyList<string> FileNames() =>
        System.IO.Directory.EnumerateFiles(Directory, "*.json")
            .Select(Path.GetFileName)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray()!;

    public static FixtureCase Load(string fileName)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory, fileName)));
        var root = doc.RootElement;

        var sources = new Dictionary<int, Dictionary<string, long?[]>>();
        foreach (var src in root.GetProperty("sources").EnumerateObject())
            sources[int.Parse(src.Name, CultureInfo.InvariantCulture)] = ReadChannels(src.Value);

        var script = root.GetProperty("script").Deserialize<EditScript>(TrackEditJson.Options)!;

        List<FixtureSlot>? expectSlots = null;
        if (root.TryGetProperty("expect", out var expect))
        {
            expectSlots = new List<FixtureSlot>();
            foreach (var slot in expect.GetProperty("slots").EnumerateArray())
            {
                var origin = slot.GetProperty("origin");
                expectSlots.Add(new FixtureSlot(
                    slot.GetProperty("slotId").GetInt32(),
                    new FixtureOrigin(
                        origin.TryGetProperty("activityId", out var a) ? a.GetInt32() : null,
                        origin.TryGetProperty("splitFromSlotId", out var s) ? s.GetInt32() : null),
                    ReadChannels(slot.GetProperty("channels"))));
            }
        }

        FixtureExpectError? expectError = null;
        if (root.TryGetProperty("expectError", out var err))
            expectError = new FixtureExpectError(err.GetProperty("opIndex").GetInt32(), err.GetProperty("code").GetString()!);

        return new FixtureCase
        {
            FileName = fileName,
            Name = root.GetProperty("name").GetString()!,
            Sources = sources,
            Script = script,
            ExpectSlots = expectSlots,
            ExpectError = expectError,
        };
    }

    private static Dictionary<string, long?[]> ReadChannels(JsonElement obj)
    {
        var map = new Dictionary<string, long?[]>(StringComparer.Ordinal);
        foreach (var ch in obj.EnumerateObject())
            map[ch.Name] = ch.Value.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.Null ? (long?)null : e.GetInt64())
                .ToArray();
        return map;
    }
}
