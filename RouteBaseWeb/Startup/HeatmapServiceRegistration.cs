using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Startup;

/// <summary>
/// Heatmaps-slice DI, extracted verbatim from Program.cs (2026-07-31). Takes the database data
/// directory because the packed .htiles store is rooted there so it backs up as a unit with the DB.
/// Registration order is unchanged (HeatmapProgress stays immediately ahead of the tile stores). No
/// reflection or assembly scanning.
/// </summary>
public static class HeatmapServiceRegistration
{
    public static IServiceCollection AddHeatmapServices(this IServiceCollection services, string dbDirectory)
    {
        services.AddSingleton<RouteBaseWeb.Services.Heatmaps.HeatmapProgress>();

        // Heatmap generation (background job + pyramid/tile helpers)
        // Packed tile file store: one .htiles per heatmap on the filesystem (rooted at the DB's data dir so it
        // backs up as a unit). Stateless (no DbContext) → singletons, like the photo store.
        services.AddSingleton(new RouteBaseWeb.Services.Heatmaps.HeatmapStorageOptions { Root = dbDirectory });
        services.AddSingleton<RouteBaseWeb.Services.Heatmaps.HeatmapTileIndexCache>();
        services.AddSingleton<RouteBaseWeb.Services.Heatmaps.HeatmapTilePackStore>();
        services.AddScoped<RouteBaseWeb.Services.Heatmaps.HeatmapTileMetaCache>();
        services.AddScoped<RouteBaseWeb.Services.Heatmaps.HeatmapPyramidBuilder>();
        services.AddScoped<IHeatmapGenerationService, RouteBaseWeb.Services.Heatmaps.HeatmapGenerationService>();
        // B2: sole owner of the Heatmap row's attach/fail/mark-stale/get-visible writes + tile purge.
        services.AddScoped<RouteBaseWeb.Services.Heatmaps.IHeatmapLifecycle, RouteBaseWeb.Services.Heatmaps.HeatmapLifecycle>();
        return services;
    }
}
