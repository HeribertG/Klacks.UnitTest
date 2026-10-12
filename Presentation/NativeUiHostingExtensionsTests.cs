using Klacks.Api.Application.Configuration;
using Klacks.Api.Presentation.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
}
