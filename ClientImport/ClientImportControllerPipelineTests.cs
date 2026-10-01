// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The employee import controller behind a real Kestrel server with MVC model binding, the controller's
/// size limits and ErrorHandlingMiddleware: an upload above the limits, an unreadable Preview body and a
/// template request without a language must all be answered the way the UI expects (a coded 400 or a
/// file), not with the framework's code-less 400. Only ClientImportController is registered; the mediator
/// is a substitute and authentication is a stub that signs every request in as Admin. Uploads are sent
/// with "Expect: 100-continue" so a rejected oversize body is never transmitted and the client reliably
/// reads the answer instead of a connection reset.
/// </summary>

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Klacks.Api.Application.Commands.ClientImport;
using Klacks.Api.Application.DTOs.ClientImport;
using Klacks.Api.Application.Queries.ClientImport;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Exceptions;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.UserBackend.Staffs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.ClientImport;

[TestFixture]
[NonParallelizable]
public class ClientImportControllerPipelineTests
{
    private const string BaseRoute = "api/backend/ClientImport/";
    private const string ProblemCodeProperty = "code";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private IMediator _mediator = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        _mediator = Substitute.For<IMediator>();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_mediator);
        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, AdminAuthenticationHandler>(JwtBearerDefaults.AuthenticationScheme, null);
        builder.Services.AddAuthorization();
        builder.Services.AddControllers()
            .ConfigureApplicationPartManager(manager =>
            {
                manager.ApplicationParts.Clear();
                manager.ApplicationParts.Add(new AssemblyPart(typeof(ClientImportController).Assembly));
                manager.FeatureProviders.Clear();
                manager.FeatureProviders.Add(new OnlyClientImportControllerProvider());
            });

        _app = builder.Build();
        _app.UseMiddleware<ErrorHandlingMiddleware>();
        _app.UseRouting();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapControllers();
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(address.TrimEnd('/') + "/") };
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Test]
    public async Task UploadAboveTheLimit_IsAnsweredWithFileTooLarge()
    {
        var oversize = new byte[ClientImportLimits.MaxUploadRequestBytes + (1024 * 1024)];

        var response = await PostFile(oversize);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(response)).ShouldBe(ClientImportErrorCodes.FileTooLarge);
    }

    [Test]
    public async Task SmallUpload_ReachesTheParseCommand()
    {
        _mediator.Send(Arg.Any<ParseClientImportCommand>(), Arg.Any<CancellationToken>()).Returns(new ClientImportParseResult());

        var response = await PostFile(Encoding.UTF8.GetBytes("Name;Ort\nAnna;Bern"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _mediator.Received().Send(Arg.Is<ParseClientImportCommand>(c => c.FileName == "staff.csv"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PreviewWithAnUnknownEnumName_IsAnsweredWithInvalidRequest()
    {
        using var body = new StringContent("{\"policy\":{\"emailType\":\"Nope\"}}", Encoding.UTF8, "application/json");

        var response = await _client.PostAsync(BaseRoute + "Preview", body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(response)).ShouldBe(ClientImportErrorCodes.InvalidRequest);
    }

    [Test]
    public async Task TemplateWithoutLanguage_IsServed()
    {
        _mediator.Send(Arg.Any<GetClientImportTemplateQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ClientImportTemplateFile([1, 2], "klacks-employee-import-de.xlsx", "application/octet-stream"));
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseRoute + "Template");
        request.Headers.AcceptLanguage.ParseAdd("de-CH");

        var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _mediator.Received().Send(
            Arg.Is<GetClientImportTemplateQuery>(q => q.Language == null && q.PreferredLanguages!.Contains("de-CH")), Arg.Any<CancellationToken>());
    }

    private async Task<HttpResponseMessage> PostFile(byte[] content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "staff.csv");
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseRoute + "Parse") { Content = form };
        request.Headers.ExpectContinue = true;
        return await _client.SendAsync(request);
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty(ProblemCodeProperty, out var code) ? code.GetString() : null;
    }

    private sealed class OnlyClientImportControllerProvider : ControllerFeatureProvider
    {
        protected override bool IsController(System.Reflection.TypeInfo typeInfo) => typeInfo.AsType() == typeof(ClientImportController);
    }

    private sealed class AdminAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "pipeline-test"), new Claim(ClaimTypes.Role, Roles.Admin)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
