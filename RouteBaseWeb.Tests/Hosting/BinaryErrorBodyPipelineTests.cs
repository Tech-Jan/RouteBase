using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace RouteBaseWeb.Tests.Hosting;

/// <summary>
/// Binary-serving endpoints (gpx/zip/jpeg) declare JSON ApiError bodies for their 400/404 paths. A bare
/// <c>[Produces("application/gpx+xml")]</c> is a RESULT FILTER: it stamps the binary content type onto
/// every ObjectResult — including the error ones — and no formatter can write ApiError as gpx/zip/jpeg,
/// so the declared errors left the server as empty 406s. These tests run the real result pipeline
/// (unit tests calling actions directly never execute result filters) and pin: error paths on binary
/// endpoints answer their declared status with their declared JSON body.
/// </summary>
[Collection(AppPipelineCollection.Name)]
public class BinaryErrorBodyPipelineTests
{
    [Fact]
    public async Task BinaryEndpoints_ErrorPaths_AnswerDeclaredJsonBodies_Not406()
    {
        try
        {
            LocalSettingsFile.Write("""{ "LocalMode": { "Enabled": true } }""");
            await using var factory = new LocalModeAppFactory();
            using var client = factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            // GPX export of a nonexistent activity → declared 404 {code:"notFound"}.
            var export = await client.GetAsync("/api/activities/export/999999");
            Assert.Equal(HttpStatusCode.NotFound, export.StatusCode);
            Assert.Contains("notFound", await export.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            // ZIP export with neither all=true nor trackIds → declared 400 {code:"badRequest"}.
            var exportAll = await client.GetAsync("/api/activities/export");
            Assert.Equal(HttpStatusCode.BadRequest, exportAll.StatusCode);
            Assert.Contains("badRequest", await exportAll.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            // Photo rendition with a bogus tier → declared 400, before any lookup.
            var image = await client.GetAsync("/api/photos/1/image?tier=bogus");
            Assert.Equal(HttpStatusCode.BadRequest, image.StatusCode);
            Assert.Contains("badRequest", await image.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            LocalSettingsFile.Delete();
        }
    }
}
