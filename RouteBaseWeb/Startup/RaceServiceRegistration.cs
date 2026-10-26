using RouteBaseWeb.Services;

namespace RouteBaseWeb.Startup;

/// <summary>
/// Races-slice DI, extracted verbatim from Program.cs (2026-07-31). No reflection or assembly
/// scanning; registration order is unchanged. IRaceAccess is the slice's single authorization seam and is
/// registered first, before every CRUD service that depends on it.
/// </summary>
public static class RaceServiceRegistration
{
    public static IServiceCollection AddRaceServices(this IServiceCollection services)
    {
        // IRaceAccess is the race slice's single authorization seam — every CRUD service below it depends on it.
        services.AddScoped<RouteBaseWeb.Services.Races.IRaceAccess, RouteBaseWeb.Services.Races.RaceAccess>();
        services.AddScoped<RouteBaseWeb.Services.Races.IRaceCrud, RouteBaseWeb.Services.Races.RaceCrud>();
        services.AddScoped<RouteBaseWeb.Services.Races.IRaceCheckpointCrud, RouteBaseWeb.Services.Races.RaceCheckpointCrud>();
        services.AddScoped<RouteBaseWeb.Services.Races.IRaceParticipantCrud, RouteBaseWeb.Services.Races.RaceParticipantCrud>();
        services.AddScoped<RouteBaseWeb.Services.Races.IRaceCourseService, RouteBaseWeb.Services.Races.RaceCourseService>();
        services.AddScoped<RouteBaseWeb.Services.Races.IRaceImportService, RouteBaseWeb.Services.Races.RaceImportService>();
        services.AddScoped<RouteBaseWeb.Services.Races.IRaceImportProfileStore, RouteBaseWeb.Services.Races.RaceImportProfileStore>();
        services.AddScoped<RouteBaseWeb.Services.Races.IRaceRealParticipantService, RouteBaseWeb.Services.Races.RaceRealParticipantService>();
        services.AddScoped<RouteBaseWeb.Services.Races.IRaceCostCurveService, RouteBaseWeb.Services.Races.RaceCostCurveService>();
        services.AddScoped<RouteBaseWeb.Services.Races.IRaceReplayService, RouteBaseWeb.Services.Races.RaceReplayService>();
        return services;
    }
}
