using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Startup;

/// <summary>
/// Import / export / parsing slice DI, extracted verbatim from Program.cs (2026-07-31) as one
/// contiguous block, so registration order is unchanged. Takes <see cref="IConfiguration"/> because
/// TrackUploadLimits is bound from the "TrackUpload" section. No reflection or assembly scanning.
/// </summary>
public static class ImportExportServiceRegistration
{
    public static IServiceCollection AddImportExportServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Shared import service — provider-agnostic DB write pipeline
        services.AddScoped<IActivityImportService, ActivityImportService>();

        // GI-01: coordinate-based timezone resolver (offline GeoTimeZone lookup) for the import path.
        services.AddScoped<IActivityTimezoneResolver, GeoTimeZoneActivityResolver>();
        // D3a: WAL-safe nightly DB snapshot (VACUUM INTO) + prune.
        services.AddScoped<IDatabaseBackupService, DatabaseBackupService>();

        // Activity-file import + GPX export
        services.AddScoped<IGpxParsingService, GpxParsingService>();
        services.AddScoped<ITcxParsingService, TcxParsingService>();
        services.AddScoped<IFitParsingService, FitParsingService>();
        services.AddScoped<ITrackFileParser, TrackFileParserRouter>();
        services.AddScoped<IGpxExportService, GpxExportService>();
        services.AddScoped<IActivityExportService, ActivityExportService>();
        services.AddSingleton<TrackImportStaging>();
        services.AddSingleton(
            configuration.GetSection("TrackUpload").Get<TrackUploadLimits>() ?? new TrackUploadLimits());
        services.AddSingleton<TrackUploadPolicy>();
        services.AddScoped<TrackFileImportOrchestrator>();
        return services;
    }
}
