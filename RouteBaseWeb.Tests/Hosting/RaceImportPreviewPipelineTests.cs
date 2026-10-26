using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using RouteBaseWeb.Models;
using Xunit;

namespace RouteBaseWeb.Tests.Hosting;

/// <summary>
/// The import preview, through the REAL pipeline.
///
/// It has to be: [ApiController]'s model validation short-circuits BEFORE the action is entered, so a
/// unit test calling ImportPreview directly cannot see it — and that is exactly where this broke. Every
/// preview request carries an empty `nameColumn` until the user has mapped one, form binding converts an
/// empty value to null, and a non-nullable string property carries an implicit [Required] from nullable
/// reference types. The result was a 400 "NameColumn: The NameColumn field is required." on the FIRST
/// preview of every file, which made the panel sit on "Reading the file…" forever.
///
/// Two layers of the same bug, both fixed and both pinned here: the request no longer requires the field,
/// and the panel no longer swallows the rejection.
/// </summary>
[Collection(AppPipelineCollection.Name)]
public class RaceImportPreviewPipelineTests
{
    private const string Csv =
        "rank,bib,name,year,gender,category,club_loc,nation,status,finish,start_offset\n"
        + "1,2045,Schweizer Ben,2003,M,Herren Lizenziert,STOP&GO,Deutschland,Finisher,2:43:18.2,0:00:09.8\n"
        + "2,2046,Adler Max,2006,M,Herren Lizenziert,STOP&GO,Deutschland,DNF,,0:00:11.2\n";

    private static async Task WithClient(Func<HttpClient, Task> body)
    {
        try
        {
            LocalSettingsFile.Write("""{ "LocalMode": { "Enabled": true } }""");
            await using var factory = new LocalModeAppFactory();
            using var client = factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await body(client);
        }
        finally
        {
            LocalSettingsFile.Delete();
        }
    }

    /// <summary>A preview upload with NOTHING mapped — exactly what the panel sends on a file pick.</summary>
    private static MultipartFormDataContent UnmappedUpload()
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(Csv));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        return new MultipartFormDataContent
        {
            { file, "file", "black-forest-75km-2026.csv" },
            // The panel sends the pickers' current values, and on the first pick they are all empty.
            { new StringContent(string.Empty), "nameColumn" },
            { new StringContent("false"), "perLegTimes" },
        };
    }

    [Fact]
    public async Task APreviewWithNothingMappedYet_IsNotRejectedByModelValidation()
    {
        await WithClient(async client =>
        {
            using var content = UnmappedUpload();
            var resp = await client.PostAsync("/api/RacesApi/1/import/preview", content);

            // The race does not exist for this caller, so 404 is the honest answer — but it must come
            // from the ACTION, not from a validation short-circuit that never let the action run.
            Assert.NotEqual(HttpStatusCode.BadRequest, resp.StatusCode);

            if (resp.StatusCode == HttpStatusCode.BadRequest)
            {
                var body = await resp.Content.ReadFromJsonAsync<ApiError>();
                Assert.Fail($"preview rejected before the action: {body?.Code} {body?.Message}");
            }
        });
    }

    [Fact]
    public async Task AnIMPORTWithNoNameColumn_IsRefusedWithAMessageThatSaysWhatToDo()
    {
        await WithClient(async client =>
        {
            using var content = UnmappedUpload();
            var resp = await client.PostAsync("/api/RacesApi/1/import", content);

            // The requirement is real — it just belongs to the import rather than to the request type.
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var body = await resp.Content.ReadFromJsonAsync<ApiError>();
            Assert.NotNull(body);
            Assert.Equal(ApiErrorCodes.BadRequest, body!.Code);
            // Not the framework's "The NameColumn field is required." — a sentence naming the control.
            Assert.Contains("column", body.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NameColumn", body.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task APreviewWithNoFileAtAll_SaysSo_RatherThanFailingValidation()
    {
        await WithClient(async client =>
        {
            using var content = new MultipartFormDataContent
            {
                { new StringContent("name"), "nameColumn" },
                { new StringContent("false"), "perLegTimes" },
            };

            var resp = await client.PostAsync("/api/RacesApi/1/import/preview", content);

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var body = await resp.Content.ReadFromJsonAsync<ApiError>();
            Assert.Contains("file", body!.Message, StringComparison.OrdinalIgnoreCase);
        });
    }
}
