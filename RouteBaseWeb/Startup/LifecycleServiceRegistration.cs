namespace RouteBaseWeb.Startup;

/// <summary>
/// Activity-lifecycle + track-version-history slice DI, extracted verbatim from Program.cs
/// (2026-07-31). Registration order is unchanged. No reflection or assembly scanning.
/// </summary>
public static class LifecycleServiceRegistration
{
    public static IServiceCollection AddLifecycleServices(this IServiceCollection services)
    {
        services.AddScoped<RouteBaseWeb.Services.Lifecycle.IActivityLifecycleService, RouteBaseWeb.Services.Lifecycle.ActivityLifecycleService>();
        services.AddScoped<RouteBaseWeb.Services.Lifecycle.TrashQueryService>();
        services.AddScoped<RouteBaseWeb.Services.Tracks.ITrackVersionHistory, RouteBaseWeb.Services.Tracks.TrackVersionHistory>();
        return services;
    }
}
