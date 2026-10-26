using System.Text.Json;
using RouteBaseWeb.Models;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins the wire shape of ShareLinksController's bodies (Models/ShareLinkContracts.cs). Beyond the usual
/// contract-drift guard, two of these are security assertions: the list DTO must never gain a token
/// property, and the mint URL must stay relative.
/// </summary>
public class ShareLinksApiWireShapeTests
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

    private static ShareLinkSummaryDto SampleSummary() => new(
        Id: 3,
        Kind: ShareKindNames.Activity,
        ResourceId: 42,
        CreatedAt: DateTime.UtcNow,
        ExpiresAt: DateTime.UtcNow.AddDays(7),
        RevokedAt: null,
        IsActive: true,
        OpenCount: 4,
        LastOpenedAt: DateTime.UtcNow);

    [Fact]
    public void MintRequest_KeySet()
    {
        var e = Serialize(new ShareLinkMintRequest(ShareKindNames.Activity, 42, "7d"));
        Assert.Equal(new[] { "kind", "resourceId", "ttl" }, Keys(e));
    }

    [Fact]
    public void MintResponse_KeySet()
    {
        var e = Serialize(new ShareLinkMintResponse(3, "/s/abc", DateTime.UtcNow));
        Assert.Equal(new[] { "expiresAt", "id", "url" }, Keys(e));
    }

    [Fact]
    public void MintResponse_UrlIsRelative()
    {
        // Request.Host is spoofable here (UseForwardedHeaders trusts any upstream), so an absolute URL
        // built from it could hand the owner a share link pointing at an attacker's domain.
        var url = Serialize(new ShareLinkMintResponse(3, "/s/abc", DateTime.UtcNow))
            .GetProperty("url").GetString()!;

        Assert.StartsWith("/s/", url, StringComparison.Ordinal);
        Assert.DoesNotContain("://", url, StringComparison.Ordinal);
    }

    [Fact]
    public void SummaryDto_KeySet()
    {
        Assert.Equal(
            new[]
            {
                "createdAt", "expiresAt", "id", "isActive", "kind", "lastOpenedAt", "openCount",
                "resourceId", "revokedAt",
            },
            Keys(Serialize(SampleSummary())));
    }

    [Fact]
    public void SummaryDto_CarriesNoTokenOrHash()
    {
        // THE structural guarantee behind "the link is shown once": the list endpoint has no field that
        // could carry a credential, so it cannot leak one even by mistake. Adding one fails here.
        var keys = Keys(Serialize(SampleSummary()));

        Assert.DoesNotContain(keys, k => k.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, k => k.Contains("hash", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, k => k.Contains("url", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, k => k.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ListResponse_KeySet()
    {
        // totalOpens rides alongside the rows because it counts links the rows do not include: ended ones
        // are capped server-side, so the dialog cannot derive this by summing what it was sent.
        var e = Serialize(new ShareLinkListResponse([SampleSummary()], TotalOpens: 12));
        Assert.Equal(new[] { "links", "totalOpens" }, Keys(e));
        Assert.Equal(JsonValueKind.Array, e.GetProperty("links").ValueKind);
        Assert.Equal(12, e.GetProperty("totalOpens").GetInt32());
    }

    private static ShareLinkInventoryItemDto SampleInventoryItem() => new(
        Id: 3,
        Kind: ShareKindNames.Activity,
        ResourceId: 42,
        ResourceName: "Morning ride",
        CreatedAt: DateTime.UtcNow,
        ExpiresAt: DateTime.UtcNow.AddDays(7),
        OpenCount: 4,
        LastOpenedAt: DateTime.UtcNow);

    [Fact]
    public void InventoryItem_KeySet()
    {
        Assert.Equal(
            new[]
            {
                "createdAt", "expiresAt", "id", "kind", "lastOpenedAt", "openCount", "resourceId",
                "resourceName",
            },
            Keys(Serialize(SampleInventoryItem())));
    }

    [Fact]
    public void InventoryItem_CarriesNoTokenOrHash()
    {
        // Same structural guarantee as SummaryDto: the inventory is the widest share listing in the app —
        // every live link at once — so it is the one that must most obviously be incapable of carrying a
        // credential.
        var keys = Keys(Serialize(SampleInventoryItem()));

        Assert.DoesNotContain(keys, k => k.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, k => k.Contains("hash", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, k => k.Contains("url", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, k => k.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InventoryResponse_KeySet()
    {
        // No liveness flag on the envelope or the rows: everything here is live by construction (the
        // service filters), so a per-row isActive would be a field that is always true — and the first
        // thing to drift.
        var e = Serialize(new ShareLinkInventoryResponse([SampleInventoryItem()]));
        Assert.Equal(new[] { "links" }, Keys(e));
        Assert.Equal(JsonValueKind.Array, e.GetProperty("links").ValueKind);
    }
}
