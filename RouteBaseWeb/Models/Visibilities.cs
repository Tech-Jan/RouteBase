using System.Diagnostics.CodeAnalysis;

namespace RouteBaseWeb.Models;

/// <summary>
/// The visibility vocabulary, shared by activities and segments.
///
/// One list on purpose: <see cref="Services.VisibilityService"/> branches on these exact strings for BOTH
/// entity types, so two private copies that drifted — a value one writer accepts and the reader does not
/// recognise — would silently make rows invisible to everyone rather than failing loudly.
/// </summary>
public static class Visibilities
{
    public const string Private = "private";
    public const string Friends = "friends";
    public const string Public  = "public";

    /// <summary>What a new row gets when nothing else decides. Matches the entity CLR defaults.</summary>
    public const string Default = Friends;

    private static readonly HashSet<string> Known =
        new(StringComparer.OrdinalIgnoreCase) { Private, Friends, Public };

    public static bool IsValid([NotNullWhen(true)] string? value) => value != null && Known.Contains(value);

    /// <summary>Normalises a caller-supplied value, or null when it is not a recognised visibility.</summary>
#pragma warning disable CA1308 // canonical stored visibility is lowercase (the vocabulary consts); readers branch on it
    public static string? Normalize(string? value) =>
        IsValid(value) ? value.ToLowerInvariant() : null;
#pragma warning restore CA1308
}
