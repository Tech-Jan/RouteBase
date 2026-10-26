using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using RouteBaseWeb.Models;
using Xunit;

namespace RouteBaseWeb.Tests.Hosting;

/// <summary>
/// API-07: a malformed request body must answer the canonical ApiError, not the framework's
/// ValidationProblemDetails. Run through the REAL pipeline on purpose — [ApiController]'s validation
/// short-circuit happens before the action is ever entered, so a unit test calling the action directly
/// cannot see it. That is precisely why this was the last non-ApiError body on the wire.
/// </summary>
[Collection(AppPipelineCollection.Name)]
public class ValidationErrorPipelineTests
{
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

    [Fact]
    public async Task AMalformedBody_AnswersApiError_NotValidationProblemDetails()
    {
        await WithClient(async client =>
        {
            // trackId is an int; a string that is not a number fails model binding before the action runs.
            var content = new StringContent("""{ "courseTrackId": "not-a-number" }""", Encoding.UTF8, "application/json");
            var resp = await client.PostAsync("/api/RacesApi", content);

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

            var raw = await resp.Content.ReadAsStringAsync();
            // The tells of the shape we replaced — a ProblemDetails body carries these, ApiError never does.
            Assert.DoesNotContain("\"title\"", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("\"traceId\"", raw, StringComparison.Ordinal);

            var body = await resp.Content.ReadFromJsonAsync<ApiError>();
            Assert.NotNull(body);
            Assert.Equal(ApiErrorCodes.ValidationFailed, body!.Code);
            Assert.False(string.IsNullOrWhiteSpace(body.Message));
            // The message names the offending field, so the caller can act on it.
            Assert.Contains("courseTrackId", body.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact] // API-01: the raw System.Text.Json exception text must never reach the wire
    public async Task AMalformedBody_DoesNotLeakRawParserInternals()
    {
        await WithClient(async client =>
        {
            var content = new StringContent("""{ "courseTrackId": "not-a-number" }""", Encoding.UTF8, "application/json");
            var resp = await client.PostAsync("/api/RacesApi", content);

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

            var body = await resp.Content.ReadFromJsonAsync<ApiError>();
            Assert.NotNull(body);
            // STJ's own message leaks .NET internals — the CLR type it tried to bind and the byte/line
            // position in the request. None of that belongs on the wire (25897ed0 claimed to strip it but
            // AllowInputFormatterExceptionMessages defaults true, so the raw text passed straight through).
            Assert.DoesNotContain("System.", body!.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("BytePositionInLine", body.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("LineNumber", body.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task UnparseableJson_AlsoAnswersApiError()
    {
        await WithClient(async client =>
        {
            var content = new StringContent("{ this is not json", Encoding.UTF8, "application/json");
            var resp = await client.PostAsync("/api/RacesApi", content);

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

            var body = await resp.Content.ReadFromJsonAsync<ApiError>();
            Assert.NotNull(body);
            Assert.Equal(ApiErrorCodes.ValidationFailed, body!.Code);
        });
    }

    [Fact]
    public async Task ARuleRejection_KeepsTheBadRequestTier_ValidationFailedMeansMalformed()
    {
        await WithClient(async client =>
        {
            // Well-formed body, rejected by a rule (visibility vocabulary) — a DIFFERENT thing, and the
            // code must stay distinguishable from "your body did not parse".
            var content = new StringContent(
                """{ "courseTrackId": 1, "raceName": "x", "visibility": "everyone" }""",
                Encoding.UTF8, "application/json");
            var resp = await client.PostAsync("/api/RacesApi", content);

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

            var body = await resp.Content.ReadFromJsonAsync<ApiError>();
            Assert.NotNull(body);
            Assert.Equal(ApiErrorCodes.BadRequest, body!.Code);
        });
    }
}
