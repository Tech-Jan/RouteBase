using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Queue;

namespace RouteBaseWeb.Startup;

/// <summary>
/// Operation-queue + handler DI, extracted verbatim from Program.cs (2026-07-31). The seven
/// IOperationHandler registrations are kept in EXACT original order — GetServices&lt;IOperationHandler&gt;()
/// enumerates them in registration order, so this order is behaviour, not style. No reflection: adding a
/// background operation type is still a payload record + handler class + one registration line here.
/// </summary>
public static class OperationQueueServiceRegistration
{
    public static IServiceCollection AddOperationQueueServices(this IServiceCollection services)
    {
        services.AddSingleton<OperationQueue>();
        // The single enqueue seam for TrackEditDerive (recording finalize/approve, elevation refresh,
        // version restore). Singleton like the queue it wraps — it holds no per-request state.
        services.AddSingleton<RouteBaseWeb.Services.TrackEdit.ITrackDeriveQueue, RouteBaseWeb.Services.TrackEdit.TrackDeriveQueue>();
        // One handler per OperationQueue.OperationType (CR5-01, the QW-01 seam): adding a background
        // operation type = payload record + handler class + one registration line here.
        services.AddSingleton<IOperationHandler, ActivityImportHandler>();
        services.AddSingleton<IOperationHandler, RouteBaseWeb.Services.Segments.SegmentAnalysisHandler>();
        services.AddSingleton<IOperationHandler, RouteBaseWeb.Services.Segments.SingleSegmentAnalysisHandler>();
        services.AddSingleton<IOperationHandler, TrackImportHandler>();
        services.AddSingleton<IOperationHandler, RouteBaseWeb.Services.Heatmaps.HeatmapGenerationHandler>();
        services.AddSingleton<IOperationHandler, RouteBaseWeb.Services.Photos.PhotoImportHandler>();
        services.AddSingleton<IOperationHandler, RouteBaseWeb.Services.TrackEdit.TrackEditDeriveHandler>();
        services.AddSingleton<IOperationHandler, ActivityExportHandler>();
        return services;
    }
}
