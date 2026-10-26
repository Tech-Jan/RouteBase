using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Startup;

/// <summary>
/// Activities-slice DI, extracted verbatim from Program.cs (2026-07-31) so the slice owns its own
/// registrations ("features as vertical slices"). No reflection or assembly scanning — every
/// service is still found by grepping its interface name, and the registration order is unchanged.
/// </summary>
public static class ActivityServiceRegistration
{
    public static IServiceCollection AddActivityServices(this IServiceCollection services)
    {
        // Activity service split along its 3 interfaces (query / GPS / command) — each its own class.
        services.AddScoped<IActivityQueryService, ActivityQueryService>();
        services.AddScoped<IActivityGpsService, ActivityGpsService>();
        services.AddScoped<IActivityCommandService, ActivityCommandService>();
        // Singleton on purpose in an otherwise-scoped slice: it hosts the SingleFlight producers for
        // GPS reads, and a coalesced producer outlives the request that started it — so it must not be
        // able to capture the scoped DbContext that ActivityGpsService holds. See the class remarks.
        services.AddSingleton<GpsCoordinateCoalescer>();

        services.AddScoped<IAutoReviewService, AutoReviewService>();
        services.AddScoped<IMultiDayTourService, MultiDayTourService>();
        return services;
    }
}
