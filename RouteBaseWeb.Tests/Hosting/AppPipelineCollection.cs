using Xunit;

namespace RouteBaseWeb.Tests.Hosting;

/// <summary>
/// The one collection for every test that boots the real app via <see cref="ServerModeAppFactory"/>
/// (or an inline <c>WebApplicationFactory&lt;Program&gt;</c>). Membership is load-bearing, not
/// organisational: Program.cs resolves its content root, database dir, and the
/// <c>appsettings.Local.json</c> probe against <c>AppContext.BaseDirectory</c> — one SHARED physical
/// directory per test run — so factories booting in parallel from different collections would race
/// each other's file drops. One collection ⇒ xunit serializes them.
/// </summary>
[CollectionDefinition(Name)]
public class AppPipelineCollection : ICollectionFixture<ServerModeAppFactory>
{
    public const string Name = "AppPipeline";
}
