using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Startup;

/// <summary>
/// Segments-slice DI, extracted verbatim from Program.cs (2026-07-31). The shape-segment matcher
/// (formerly registered a few lines below the race block) is grouped here with the rest of its slice —
/// the only cross-slice move in the split; both it and its factory are distinct service types with no
/// IEnumerable&lt;T&gt; consumer, so DI resolution is identical. No reflection or assembly scanning.
/// </summary>
public static class SegmentServiceRegistration
{
    public static IServiceCollection AddSegmentServices(this IServiceCollection services)
    {
        // Segment service split along 3 responsibilities (CRUD / analysis / efforts read-model), each its own
        // class behind its own interface. SegmentCache is the shared cache-invalidation collaborator so Crud's
        // delete still evicts the Efforts read-model's keys.
        services.AddScoped<SegmentCache>();
        services.AddScoped<ISegmentCrud, SegmentCrud>();
        services.AddScoped<ISegmentAnalysis, SegmentAnalysis>();
        services.AddScoped<ISegmentEfforts, SegmentEfforts>();
        // Identity-free GPS→wire effort math. Separate from ISegmentEfforts because the share path needs the
        // same math behind a different gate, and a shared implementation is the only way the two can't drift.
        services.AddScoped<RouteBaseWeb.Services.Segments.ISegmentEffortPerformanceBuilder,
                           RouteBaseWeb.Services.Segments.SegmentEffortPerformanceBuilder>();
        // Segment view record + its gate seam. The builder is identity-free; ISegmentViewSource is what decides
        // whose data a render sees, so the share route can swap the source without touching the builder.
        services.AddScoped<RouteBaseWeb.Services.Segments.ISegmentDetailViewBuilder,
                           RouteBaseWeb.Services.Segments.SegmentDetailViewBuilder>();
        services.AddScoped<RouteBaseWeb.Services.Segments.AuthenticatedSegmentViewSource>();
        // Shape-segment matcher: registered concretely (the live-preview API uses it directly) and
        // behind ISegmentMatchStrategy (the analysis dispatch uses it). Same scoped instance for both.
        services.AddScoped<RouteBaseWeb.Services.Segments.ShapeSegmentMatcher>();
        services.AddScoped<RouteBaseWeb.Services.Segments.ISegmentMatchStrategy>(
            sp => sp.GetRequiredService<RouteBaseWeb.Services.Segments.ShapeSegmentMatcher>());
        return services;
    }
}
