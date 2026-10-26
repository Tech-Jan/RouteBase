namespace RouteBaseWeb.Startup;

/// <summary>
/// Photo import-pipeline slice DI, extracted verbatim from Program.cs (2026-07-31). Takes
/// <see cref="IConfiguration"/> because PhotoUploadLimits is bound from the "PhotoUpload" section. The
/// photo rendition file store (PhotoStorageOptions / IPhotoImageStore) is registered separately in
/// Program.cs alongside the other filesystem stores and is out of this slice's scope. No reflection.
/// </summary>
public static class PhotoServiceRegistration
{
    public static IServiceCollection AddPhotoServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Photo import pipeline (§3–§5): staging (temp batch dirs), stateless EXIF/derivative services,
        // per-user progress, and the scoped orchestrator (uses DbContext + track reader per work item).
        services.AddSingleton<RouteBaseWeb.Services.Photos.PhotoImportStaging>();
        services.AddSingleton(
            configuration.GetSection("PhotoUpload").Get<RouteBaseWeb.Services.Photos.PhotoUploadLimits>()
                ?? new RouteBaseWeb.Services.Photos.PhotoUploadLimits());
        services.AddSingleton<RouteBaseWeb.Services.Photos.PhotoUploadPolicy>();
        services.AddSingleton<RouteBaseWeb.Services.Photos.PhotoExifReader>();
        services.AddSingleton<RouteBaseWeb.Services.Photos.PhotoDerivativeService>();
        services.AddSingleton<RouteBaseWeb.Services.Photos.PhotoImportProgress>();
        services.AddScoped<RouteBaseWeb.Services.Photos.PhotoImportOrchestrator>();
        // Photo read/mutate surface (B3): the API controller is a thin HTTP mapper over these.
        services.AddScoped<RouteBaseWeb.Services.Photos.IPhotoQueryService, RouteBaseWeb.Services.Photos.PhotoQueryService>();
        services.AddScoped<RouteBaseWeb.Services.Photos.IPhotoCommandService, RouteBaseWeb.Services.Photos.PhotoCommandService>();
        return services;
    }
}
