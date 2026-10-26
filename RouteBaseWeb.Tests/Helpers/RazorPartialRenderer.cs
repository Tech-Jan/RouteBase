using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Xunit;

namespace RouteBaseWeb.Tests.Helpers;

/// <summary>
/// Renders a compiled Razor partial to HTML, outside a running host.
///
/// Exists so the share view's markup can be ASSERTED rather than eyeballed: the share page and the
/// canonical page share their partials, so "an owner affordance leaked onto the anonymous page" is a
/// realistic future regression that neither the compiler nor the existing tests can see.
///
/// Deliberately minimal — MVC core + the Razor view engine over the web assembly's compiled views. No
/// test host, no HTTP pipeline, no database.
/// </summary>
public sealed class RazorPartialRenderer : IDisposable
{
    private readonly ServiceProvider _provider;

    public RazorPartialRenderer()
    {
        var services = new ServiceCollection().AddSingleton(TimeProvider.System);

        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IWebHostEnvironment>(new StubWebHostEnvironment());
        services.AddSingleton(new DiagnosticListener("RazorPartialRenderer"));
        services.AddSingleton<DiagnosticSource>(sp => sp.GetRequiredService<DiagnosticListener>());

        services
            .AddMvcCore()
            .AddRazorViewEngine()
            // Compiled views live in the web assembly; without this part the engine finds nothing.
            .AddApplicationPart(typeof(RouteBaseWeb.Auth.ShareLinkDefaults).Assembly);

        _provider = services.BuildServiceProvider();
    }

    /// <summary>Renders <paramref name="viewPath"/> (an absolute "/Pages/..." path) with the given model.</summary>
    public async Task<string> RenderAsync(string viewPath, object model)
    {
        var engine = _provider.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = _provider.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = _provider };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());

        var viewResult = engine.GetView(executingFilePath: null, viewPath, isMainPage: false);
        Assert.True(viewResult.Success,
            $"Razor could not find '{viewPath}'. Searched:\n  "
            + string.Join("\n  ", viewResult.SearchedLocations ?? []));

        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            Model = model,
        };

        await using var writer = new StringWriter();
        var viewContext = new ViewContext(
            actionContext,
            viewResult.View,
            viewData,
            new TempDataDictionary(httpContext, tempDataProvider),
            writer,
            new HtmlHelperOptions());

        await viewResult.View.RenderAsync(viewContext);
        return writer.ToString();
    }

    public void Dispose() => _provider.Dispose();

    /// <summary>Content root points at the web project so any file-provider lookup resolves.</summary>
    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public StubWebHostEnvironment()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RouteBase.sln")))
                dir = dir.Parent;

            ContentRootPath = Path.Combine(dir?.FullName ?? ".", "RouteBaseWeb");
            WebRootPath = Path.Combine(ContentRootPath, "wwwroot");
            ContentRootFileProvider = new PhysicalFileProvider(ContentRootPath);
            WebRootFileProvider = Directory.Exists(WebRootPath)
                ? new PhysicalFileProvider(WebRootPath)
                : ContentRootFileProvider;
        }

        public string ApplicationName { get; set; } = "RouteBaseWeb";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; }
        public string WebRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
    }
}
