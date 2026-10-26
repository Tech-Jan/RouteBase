namespace RouteBaseWeb.Services.Sharing;

/// <summary>
/// The share-link lifetimes a caller may ask for. A closed preset set, not a caller-supplied timestamp:
/// accepting an arbitrary <c>expiresAt</c> would make "expiry is mandatory" advisory — a client could
/// request the year 3000 and mint a permanent link through an API that promises none exist.
/// </summary>
public sealed record ShareTtl(string Name, TimeSpan Duration)
{
    public static readonly ShareTtl Day = new("24h", TimeSpan.FromHours(24));
    public static readonly ShareTtl Week = new("7d", TimeSpan.FromDays(7));
    public static readonly ShareTtl Month = new("30d", TimeSpan.FromDays(30));

    public static readonly IReadOnlyList<ShareTtl> All = [Day, Week, Month];

    /// <summary>Wire name → preset. Null for anything unrecognized, which callers turn into a 400.</summary>
    public static ShareTtl? Parse(string? name) => name switch
    {
        "24h" => Day,
        "7d" => Week,
        "30d" => Month,
        _ => null,
    };
}
