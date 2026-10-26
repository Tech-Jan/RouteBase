using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Recording;
using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Pages;
using RouteBaseWeb.Pages.Activities;
using RouteBaseWeb.Pages.Races;
using RouteBaseWeb.Pages.Segments;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Api.OpenApiContracts;

/// <summary>
/// Registers the Razor config-island DTOs as component schemas WITHOUT endpoints (D27, closing D11) —
/// these shapes are serialized into cshtml JSON islands (camelCase, STJ Web semantics), never over HTTP,
/// so ApiExplorer alone would leave them out of the doc and their TS mirrors hand-written. Registration is
/// metadata only: nothing about the pages or their serialization changes. The schemas flow through the same
/// pipeline as endpoint DTOs (RequiredPropertiesSchemaTransformer, D1/D3/D26 stamps) and into
/// src/js/generated/contracts.ts, putting island shapes under the same drift gate.
///
/// NOT here: islands built as anonymous Razor projections (segment-stats effortRows, statsBootstrap) —
/// there is no named type to register; naming those is a page refactor, not schema registration. The
/// data-attribute island EditResumeHint is not JSON at all.
/// </summary>
internal sealed class IslandSchemasDocumentTransformer : IOpenApiDocumentTransformer
{
    private static readonly Type[] IslandTypes =
    [
        typeof(ShareLiveSeedDto),        // Pages/Share/_ShareLiveBody.cshtml seed
        typeof(ShareRaceSeedDto),        // Pages/Share/_ShareRaceBody.cshtml seed
        typeof(GpsTrackWireDto),         // Activity details track island
        typeof(ShapeRectDto),            // Segment detail shape-rects island
        typeof(TrashGridBootstrap),      // Pages/Activities/Trash.cshtml
        typeof(DashboardGridRow),        // Pages/Index.cshtml dashboard grid
        typeof(ActivitiesGridBootstrap), // Pages/Activities.cshtml
        typeof(SegmentsGridRow),         // Pages/Segments/Index.cshtml
        typeof(RacesGridBootstrap),      // Pages/Races/Index.cshtml
        typeof(Pages.StatisticsBootstrap),                    // Pages/Statistics.cshtml (F1)
        typeof(ActivityDetailsConfigDto),                     // Activities/_ActivityDetailScripts.cshtml (F2a)
        typeof(SegmentStatisticsConfigDto),                   // Segments/_SegmentStatsScripts.cshtml (F2b)
        // The one settings payload, delivered to every page as the #appSettings island.
        // ⚠ ISLAND-ONLY — never add it to a request body. gen-contracts.mjs relaxes any schema reachable
        // only from requests back to all-optional, so a PUT taking this type would silently undo its
        // non-nullability with no test failing. A write endpoint needs its own SettingsUpdateRequest.
        typeof(Models.Settings.SettingsPayload),
        // Named nested types: the parents above already register these as $ref'd components during their own
        // generation (the same-shape collision the loop tolerates). Listed anyway as a safety net: if the
        // framework ever stops registering children, the explicit add keeps each schema present under its
        // C# name instead of contracts.ts silently losing the alias.
        typeof(TrashedActivity),             // TrashGridBootstrap.Rows item
        typeof(RacesGridRow),                // RacesGridBootstrap.Rows item
        typeof(GridSort),                    // ActivitiesGridBootstrap.Sort
        typeof(ActivitiesModel.OwnerOption), // ActivitiesGridBootstrap.OwnerOptions item
        typeof(SegmentEffortRowDto),         // segment-stats effortRows item (named projection, D28)
    ];

    public async Task TransformAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        foreach (var type in IslandTypes)
        {
            var schema = await context.GetOrCreateSchemaAsync(type, parameterDescription: null, cancellationToken);
            document.Components ??= new OpenApiComponents();
            document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
            // A name already present is fine only when it is THIS type's schema (happens when a parent island
            // type $ref'd this child during its own generation — same generator, same type, same shape).
            // Anything else would silently map one C# type's contract to another type's schema — the doc and
            // contracts.ts key on the short name, so that must fail the build, not pick a winner.
            if (!document.Components.Schemas.TryAdd(type.Name, schema)
                && Serialized(document.Components.Schemas[type.Name]) != Serialized(schema))
            {
                throw new InvalidOperationException(
                    $"Island schema '{type.Name}' ({type.FullName}) collides with a different component schema: " +
                    "either the type became endpoint-reachable with another shape (remove it from IslandTypes) " +
                    "or another type shares its short name (rename one).");
            }
        }
    }

    private static string Serialized(IOpenApiSchema schema)
    {
        using var text = new StringWriter();
        schema.SerializeAsV31(new OpenApiJsonWriter(text));
        return text.ToString();
    }
}
