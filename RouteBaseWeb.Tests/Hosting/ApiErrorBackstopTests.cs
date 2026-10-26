using System.Net;
using System.Net.Http.Json;
using RouteBaseWeb.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RouteBaseWeb.Tests.Hosting;

/// <summary>
/// Test-only endpoint whose action succeeds but whose RESULT dies in the output formatter — the exact
/// vector of the 2026-07-13 Error_Log storm (Ok(payload) with a non-finite double). Result-execution
/// throws bypass <c>ApiExceptionFilter</c> (IExceptionFilter covers action execution only), so they are
/// what the CC2-01 middleware net exists to catch. Discovered only where a test adds this assembly as
/// an application part; [AllowAnonymous] keeps the server-mode fallback policy out of the way.
/// </summary>
[ApiController]
[Route("api/__test/serialization-bomb")]
[AllowAnonymous]
public sealed class SerializationBombController : ControllerBase
{
    public sealed record Bomb(double Value);

    [HttpGet]
    public IActionResult Get() => Ok(new Bomb(double.PositiveInfinity));
}

/// <summary>
/// Pins the CC2-01 net's response BODY to the uniform ApiError contract. The net is the backstop for
/// throws the MVC exception filter can't see; after D31/D32 removed the legacy <c>{ error }</c> shape
/// from every controller, a net still answering <c>{ error }</c> hands clients that parse
/// <c>{ code, message }</c> an effectively empty body.
/// </summary>
[Collection(AppPipelineCollection.Name)]
public class ApiErrorBackstopTests
{
    private sealed class BackstopFactory : ServerModeAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // The net only registers outside Development (the factory default), where the developer
            // exception page would otherwise answer with plain text. Program.cs has no other
            // environment branch, so this flips exactly the middleware under test.
            builder.UseEnvironment("Production");
            builder.ConfigureTestServices(services =>
                services.AddControllers().AddApplicationPart(typeof(SerializationBombController).Assembly));
        }
    }

    [Fact]
    public async Task ResultExecutionThrow_AnswersWithApiErrorBody()
    {
        await using var factory = new BackstopFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/__test/serialization-bomb");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal(ApiErrorCodes.ServerError, body!.Code);
        Assert.False(string.IsNullOrWhiteSpace(body.Message));
    }
}
