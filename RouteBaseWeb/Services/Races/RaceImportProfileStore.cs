using System.Text.Json;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Services.Races;

/// <summary>
/// A saved column mapping, as the browser sees it — already resolved onto THIS race's checkpoint ids.
///
/// Resolving server-side is deliberate. The stored form is race-INDEPENDENT (see
/// <see cref="RaceImportProfileSplit"/>), and matching it onto a race needs that race's checkpoints —
/// which the panel would otherwise have to hold a second copy of, and get subtly wrong the first time a
/// checkpoint was renamed.
/// </summary>
public sealed record RaceImportProfileDto
{
    public required string Name { get; init; }
    public required string? NameColumn { get; init; }
    public required string? FinishColumn { get; init; }
    public required string? BibColumn { get; init; }
    public required string? CategoryColumn { get; init; }
    public required string? GenderColumn { get; init; }
    public required string? ClubColumn { get; init; }
    public required string? StatusColumn { get; init; }
    public required string? GunTimeColumn { get; init; }
    public required string? NetTimeColumn { get; init; }
    public required string? StartOffsetColumn { get; init; }
    public required string? WaveColumn { get; init; }
    public required string? DisciplineColumn { get; init; }
    public required bool PerLegTimes { get; init; }
    /// <summary>checkpoint id → column, for the race this was requested against.</summary>
    public required IReadOnlyDictionary<int, string> SplitColumns { get; init; }
}

/// <summary>
/// One saved split-column mapping, in its PORTABLE form.
///
/// Never <c>checkpoint_ID</c>, and that is the whole point of profiles: they are per USER, not per race,
/// so next year's Datasport file imports in one click. A checkpoint id belongs to one race and would
/// pin a profile to it forever.
///
/// Two keys, tried in that order. The checkpoint's NAME survives a race being rebuilt from scratch;
/// <see cref="Seq"/> survives a checkpoint being renamed. Storing both means the common cases — same
/// event, next year, same course — resolve without the user touching anything.
/// </summary>
public sealed record RaceImportProfileSplit
{
    public required int Seq { get; init; }
    public required string? CheckpointName { get; init; }
    public required string Column { get; init; }
}

/// <summary>The stored shape. Persisted as JSON in one ProfileSettings row.</summary>
public sealed record RaceImportProfileRecord
{
    public string? NameColumn { get; init; }
    public string? FinishColumn { get; init; }
    public string? BibColumn { get; init; }
    public string? CategoryColumn { get; init; }
    public string? GenderColumn { get; init; }
    public string? ClubColumn { get; init; }
    public string? StatusColumn { get; init; }
    public string? GunTimeColumn { get; init; }
    public string? NetTimeColumn { get; init; }
    public string? StartOffsetColumn { get; init; }
    public string? WaveColumn { get; init; }
    public string? DisciplineColumn { get; init; }
    public bool PerLegTimes { get; init; }
    public IReadOnlyList<RaceImportProfileSplit> Splits { get; init; } = [];
}

public interface IRaceImportProfileStore
{
    /// <summary>Every profile this user has saved, resolved onto <paramref name="checkpoints"/>.</summary>
    Task<IReadOnlyList<RaceImportProfileDto>> ListAsync(
        string userId, IReadOnlyList<RaceCheckpoint> checkpoints);

    /// <summary>Save (or replace) a profile. Split columns are converted to their portable form here.</summary>
    Task SaveAsync(
        string userId, string name, RaceCsvMapping mapping, IReadOnlyList<RaceCheckpoint> checkpoints);

    /// <summary>Remove a profile. Returns false when the user had no profile by that name.</summary>
    Task<bool> DeleteAsync(string userId, string name);
}

/// <summary>
/// Mapping profiles on the existing <see cref="ProfileSettings"/> KV store — no new table.
///
/// A dedicated <c>Csv_Import_Profile</c> table was considered and rejected: its cost is dominated by
/// <c>DbSchemaParityTests</c>, <c>ChildTableRegistryParityTests</c> and <c>DatabaseResetService</c>, not
/// by the DDL, and it buys only a tidier delete. ProfileSettings is already a generic per-user store that
/// tolerates keys the settings catalog has never heard of, is already in <c>ChildTableRegistry</c>, and is
/// already wiped by <c>DatabaseResetService</c> — so a profile is cleaned up by machinery that exists.
/// JSON-in-TEXT has four precedents here, closest being <c>Segment.match_rules_json</c>.
/// </summary>
public class RaceImportProfileStore : IRaceImportProfileStore
{
    private readonly IProfileSettingsService _settings;

    public RaceImportProfileStore(IProfileSettingsService settings) => _settings = settings;

    /// <summary>
    /// The key prefix. Deliberately NOT a <c>SettingKeys</c> constant: that class is regex-gated
    /// (<c>SettingsTierGateTests</c>) against chrome nouns — <c>Column</c> among them — and these keys are
    /// per-profile anyway, so there is no fixed key to declare. One prefix + one name, in one function, is
    /// the same shape as every other id-suffixed key in this codebase.
    /// </summary>
    private const string KeyPrefix = "Mapping:";

    internal static string SettingKeyFor(string name) => KeyPrefix + name;

    /// <summary>A profile name a person can retype: no colons (the key separator), bounded, non-blank.</summary>
    internal static string? NormalizeName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 60) return null;
        return trimmed.Contains(':', StringComparison.Ordinal) ? null : trimmed;
    }

    public async Task<IReadOnlyList<RaceImportProfileDto>> ListAsync(
        string userId, IReadOnlyList<RaceCheckpoint> checkpoints)
    {
        var rows = await _settings.GetCategorySettingsAsync(SettingCategories.RaceImport, userId);

        var profiles = new List<RaceImportProfileDto>();
        foreach (var (key, json) in rows)
        {
            if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal)) continue;
            var record = Deserialize(json);
            // A row we cannot read is skipped, not thrown on: one corrupt profile must not take the
            // import panel down with it, and the user can simply save over it.
            if (record == null) continue;
            profiles.Add(ToDto(key[KeyPrefix.Length..], record, checkpoints));
        }
        return profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task SaveAsync(
        string userId, string name, RaceCsvMapping mapping, IReadOnlyList<RaceCheckpoint> checkpoints)
    {
        var bySeq = checkpoints.ToDictionary(c => c.CheckpointId.Value, c => c);
        var record = new RaceImportProfileRecord
        {
            NameColumn = mapping.NameColumn,
            FinishColumn = mapping.FinishColumn,
            BibColumn = mapping.BibColumn,
            CategoryColumn = mapping.CategoryColumn,
            GenderColumn = mapping.GenderColumn,
            ClubColumn = mapping.ClubColumn,
            StatusColumn = mapping.StatusColumn,
            GunTimeColumn = mapping.GunTimeColumn,
            NetTimeColumn = mapping.NetTimeColumn,
            StartOffsetColumn = mapping.StartOffsetColumn,
            WaveColumn = mapping.WaveColumn,
            DisciplineColumn = mapping.DisciplineColumn,
            PerLegTimes = mapping.PerLegTimes,
            Splits = mapping.SplitColumns
                .Where(kv => bySeq.ContainsKey(kv.Key))
                .Select(kv => new RaceImportProfileSplit
                {
                    Seq = bySeq[kv.Key].Seq,
                    CheckpointName = bySeq[kv.Key].CheckpointName,
                    Column = kv.Value,
                })
                .OrderBy(s => s.Seq)
                .ToList(),
        };

        return _settings.SetSettingAsync(
            SettingCategories.RaceImport, SettingKeyFor(name), JsonSerializer.Serialize(record), userId);
    }

    public Task<bool> DeleteAsync(string userId, string name) =>
        _settings.DeleteSettingAsync(SettingCategories.RaceImport, SettingKeyFor(name), userId);

    /// <summary>
    /// Resolve a stored profile onto one race: by checkpoint NAME first, then by seq.
    ///
    /// Name first because it is the identity a person recognises — "Raimartihof" is Raimartihof whether it
    /// is checkpoint 2 or 3 this year. Seq is the fallback for a checkpoint that was renamed, and a
    /// checkpoint already claimed by a name match is never claimed again by a seq match, or one column
    /// would land on two checkpoints (which is a composite-PK collision at import).
    /// </summary>
    private static RaceImportProfileDto ToDto(
        string name, RaceImportProfileRecord record, IReadOnlyList<RaceCheckpoint> checkpoints)
    {
        var splitColumns = new Dictionary<int, string>();
        var claimed = new HashSet<int>();

        foreach (var split in record.Splits)
        {
            var match = split.CheckpointName is { Length: > 0 } cpName
                ? checkpoints.FirstOrDefault(c =>
                    string.Equals(c.CheckpointName, cpName, StringComparison.OrdinalIgnoreCase)
                    && !claimed.Contains(c.CheckpointId.Value))
                : null;
            match ??= checkpoints.FirstOrDefault(c => c.Seq == split.Seq && !claimed.Contains(c.CheckpointId.Value));
            if (match == null) continue;

            claimed.Add(match.CheckpointId.Value);
            splitColumns[match.CheckpointId.Value] = split.Column;
        }

        return new RaceImportProfileDto
        {
            Name = name,
            NameColumn = record.NameColumn,
            FinishColumn = record.FinishColumn,
            BibColumn = record.BibColumn,
            CategoryColumn = record.CategoryColumn,
            GenderColumn = record.GenderColumn,
            ClubColumn = record.ClubColumn,
            StatusColumn = record.StatusColumn,
            GunTimeColumn = record.GunTimeColumn,
            NetTimeColumn = record.NetTimeColumn,
            StartOffsetColumn = record.StartOffsetColumn,
            WaveColumn = record.WaveColumn,
            DisciplineColumn = record.DisciplineColumn,
            PerLegTimes = record.PerLegTimes,
            SplitColumns = splitColumns,
        };
    }

    private static RaceImportProfileRecord? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RaceImportProfileRecord>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
