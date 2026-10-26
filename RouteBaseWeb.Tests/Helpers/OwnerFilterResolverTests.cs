using RouteBaseWeb.Helpers.Grid;
using RouteBaseWeb.Models.GridFilter;
using Xunit;

namespace RouteBaseWeb.Tests.Helpers;

/// <summary>
/// Direct unit tests for <see cref="OwnerFilterResolver"/> (QW-10). The token/wildcard resolution rules are
/// security-adjacent — they decide whose rows an owner filter can select — but were pinned only end-to-end
/// through the OwnerScopeService pipeline. These pin <c>Resolve</c> itself: token short-circuit, unknown-token
/// handling, wildcard name matching, and token/text union.
/// </summary>
public class OwnerFilterResolverTests
{
    private const string Me = "me-id";

    private static readonly IReadOnlyDictionary<string, string> People = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["me-id"] = "Me",
        ["u1"]    = "Alice",
        ["u2"]    = "alina",
        ["u3"]    = "Bob",
    };

    private static OwnerFilterResolver.Result Resolve(ColumnFilter? owner, IReadOnlyDictionary<string, string>? people = null)
        => OwnerFilterResolver.Resolve(owner, Me, people ?? People);

    [Fact]
    public void Resolve_EmptyFilter_Unconstrained()
    {
        Assert.True(Resolve(null).NoConstraint);
        Assert.True(Resolve(new ColumnFilter()).NoConstraint);
    }

    [Fact]
    public void Resolve_Everyone_ShortCircuitsOtherTokens()
    {
        var result = Resolve(new ColumnFilter { In = { OwnerFilterResolver.Me, OwnerFilterResolver.Everyone } });

        Assert.True(result.NoConstraint);
        Assert.False(result.MatchNone);
    }

    [Fact]
    public void Resolve_Me_ReturnsCurrentUserOnly()
    {
        var result = Resolve(new ColumnFilter { In = { OwnerFilterResolver.Me } });

        Assert.False(result.NoConstraint);
        Assert.False(result.MatchNone);
        Assert.Equal(["me-id"], result.UserIds);
    }

    [Fact]
    public void Resolve_Others_ExcludesCurrentUser()
    {
        var result = Resolve(new ColumnFilter { In = { OwnerFilterResolver.Others } });

        Assert.False(result.NoConstraint);
        Assert.Equal(["u1", "u2", "u3"], result.UserIds.OrderBy(id => id, StringComparer.Ordinal).ToList());
        Assert.DoesNotContain("me-id", result.UserIds, StringComparer.Ordinal);
    }

    [Fact]
    public void Resolve_UnknownToken_Ignored_KnownIdHonoured()
    {
        // "ghost-id" absent from the visible-user map → ignored → nobody matched → MatchNone.
        var absent = Resolve(new ColumnFilter { In = { "ghost-id" } });
        Assert.True(absent.MatchNone);
        Assert.False(absent.NoConstraint);

        // Same token, but now a known visible id → honoured.
        var present = Resolve(
            new ColumnFilter { In = { "ghost-id" } },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ghost-id"] = "Ghost" });
        Assert.False(present.MatchNone);
        Assert.Equal(["ghost-id"], present.UserIds);
    }

    [Fact]
    public void Resolve_TextWildcard_MatchesDisplayNamesCaseInsensitive()
    {
        var star = Resolve(new ColumnFilter { Text = "ali*" });
        Assert.Equal(["u1", "u2"], star.UserIds.OrderBy(id => id, StringComparer.Ordinal).ToList());

        var question = Resolve(new ColumnFilter { Text = "?ob" });
        Assert.Equal(["u3"], question.UserIds);

        var none = Resolve(new ColumnFilter { Text = "zzz" });
        Assert.True(none.MatchNone);
    }

    [Fact]
    public void Resolve_TextAndTokens_Union()
    {
        // Me token + a text term matching another user → both ids.
        var result = Resolve(new ColumnFilter { In = { OwnerFilterResolver.Me }, Text = "Bob" });

        Assert.False(result.NoConstraint);
        Assert.Equal(["me-id", "u3"], result.UserIds.OrderBy(id => id, StringComparer.Ordinal).ToList());
    }
}
