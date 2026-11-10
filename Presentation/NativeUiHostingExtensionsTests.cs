using Klacks.Api.Application.Configuration;
using Klacks.Api.Presentation.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Shouldly;

namespace Klacks.UnitTest.Presentation;

[TestFixture]
public class NativeUiHostingExtensionsTests
{
    private string _uiRoot = string.Empty;
    private string _indexPath = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _uiRoot = Path.Combine(Path.GetTempPath(), "klacks-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_uiRoot);
        _indexPath = Path.Combine(_uiRoot, "index.html");
        File.WriteAllText(_indexPath, "<html>spa</html>");
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_uiRoot, recursive: true);
    }

    private static DefaultHttpContext CreateContext(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    [Test]
    public async Task ServeIndexAsync_GetDeepLink_ReturnsIndexWithoutCaching()
    {
        var context = CreateContext(HttpMethods.Get, "/workplace/schedule");

        await NativeUiHostingExtensions.ServeIndexAsync(context, _indexPath);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Headers.CacheControl.ToString().ShouldBe("no-cache");
        ReadBody(context).ShouldBe("<html>spa</html>");
    }

    [Test]
    public async Task ServeIndexAsync_UnknownApiRoute_Returns404()
    {
        var context = CreateContext(HttpMethods.Get, "/api/backend/unknown");

        await NativeUiHostingExtensions.ServeIndexAsync(context, _indexPath);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        ReadBody(context).ShouldBeEmpty();
    }

    [Test]
    public async Task ServeIndexAsync_Post_Returns404()
    {
        var context = CreateContext(HttpMethods.Post, "/workplace/schedule");

        await NativeUiHostingExtensions.ServeIndexAsync(context, _indexPath);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task ServeIndexAsync_Head_ReturnsHeadersWithoutBody()
    {
        var context = CreateContext(HttpMethods.Head, "/workplace/schedule");

        await NativeUiHostingExtensions.ServeIndexAsync(context, _indexPath);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        ReadBody(context).ShouldBeEmpty();
    }

    [Test]
    public void UseNativeUi_RootWithoutIndex_FailsFast()
    {
        var emptyRoot = Path.Combine(_uiRoot, "empty");
        Directory.CreateDirectory(emptyRoot);
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());

        Should.Throw<InvalidOperationException>(() => app.UseNativeUi(emptyRoot));
    }

    [TestCase("/api/backend/Version")]
    [TestCase("/API/backend/unknown")]
    [TestCase("/hubs/work-notifications")]
    [TestCase("/mcp")]
    [TestCase("/oauth/token")]
    [TestCase("/.well-known/oauth-authorization-server")]
    [TestCase("/health")]
    [TestCase("/api")]
    [TestCase("/internal/health/deep")]
    public void IsServerOnlyPath_ServerRoutes_AreNotAnsweredByTheSpa(string path)
    {
        NativeUiHostingExtensions.IsServerOnlyPath(new PathString(path)).ShouldBeTrue();
    }

    [TestCase("/")]
    [TestCase("/workplace/schedule")]
    [TestCase("/settings/general")]
    [TestCase("/healthcare")]
    [TestCase("/apiary")]
    public void IsServerOnlyPath_SpaRoutes_FallBackToIndex(string path)
    {
        NativeUiHostingExtensions.IsServerOnlyPath(new PathString(path)).ShouldBeFalse();
    }

    [Test]
    public void GetUiRoot_Unset_ReturnsNull()
    {
        var configuration = new ConfigurationBuilder().Build();

        NativeUiHostingExtensions.GetUiRoot(configuration).ShouldBeNull();
    }

    [Test]
    public void GetUiRoot_Set_ReturnsFullPath()
    {
        var uiRoot = Path.Combine(Path.GetTempPath(), "klacks-ui");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [NativeHostingOptions.UiRootKey] = uiRoot })
            .Build();

        NativeUiHostingExtensions.GetUiRoot(configuration).ShouldBe(Path.GetFullPath(uiRoot));
    }

    [TestCase("index.html", "/", "no-cache")]
    [TestCase("index.html", "/index.html", "no-cache")]
    [TestCase("version.json", "/version.json", "no-store")]
    [TestCase("de.json", "/assets/i18n/de.json", "no-cache")]
    [TestCase("active-industries-manual.html", "/assets/docs/de/active-industries-manual.html", "no-cache")]
    [TestCase("login-background.png", "/assets/png/login-background.png", "no-cache")]
    [TestCase("main-5XKQ2JBL.js", "/main-5XKQ2JBL.js", null)]
    [TestCase("styles-AB12CD34.css", "/styles-AB12CD34.css", null)]
    [TestCase("chunk-ZY98XW76.js", "/chunk-ZY98XW76.js", null)]
    public void GetCacheControl_RevalidatesEveryFileWhoseNameSurvivesABuild(string fileName, string path, string? expected)
    {
        NativeUiHostingExtensions.GetCacheControl(fileName, new PathString(path)).ShouldBe(expected);
    }

    [TestCase("/assets/i18n/de.json", "no-cache")]
    [TestCase("/version.json", "no-store")]
    [TestCase("/main-5XKQ2JBL.js", null)]
    public async Task UseNativeUi_ServesStaticFilesWithTheirCacheControl(string path, string? expected)
    {
        Directory.CreateDirectory(Path.Combine(_uiRoot, "assets", "i18n"));
        File.WriteAllText(Path.Combine(_uiRoot, "assets", "i18n", "de.json"), "{}");
        File.WriteAllText(Path.Combine(_uiRoot, "version.json"), "{}");
        File.WriteAllText(Path.Combine(_uiRoot, "main-5XKQ2JBL.js"), "x");
        var environment = Substitute.For<IWebHostEnvironment>();
        environment.WebRootFileProvider.Returns(new PhysicalFileProvider(_uiRoot));
        environment.ContentRootPath.Returns(_uiRoot);
        var services = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .AddSingleton(environment)
            .BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseNativeUi(_uiRoot);
        var pipeline = app.Build();
        var context = CreateContext(HttpMethods.Get, path);
        context.RequestServices = services;

        await pipeline(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var cacheControl = context.Response.Headers.CacheControl.ToString();
        if (expected is null)
        {
            cacheControl.ShouldBeEmpty();
        }
        else
        {
            cacheControl.ShouldBe(expected);
        }
    }
}
