// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using Klacks.Api.Application.Commands.OAuth;
using Klacks.Api.Application.DTOs.OAuth;
using Klacks.Api.Application.Handlers.OAuth;
using Klacks.Api.Application.Interfaces.OAuth;
using Klacks.Api.Application.Services.OAuth;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.OAuth;

[TestFixture]
public class RegisterOAuthClientCommandHandlerTests
{
    private const string ClaudeRedirectUri = "https://claude.ai/api/mcp/auth_callback";
    private const string ChatGptRedirectUri = "https://chatgpt.com/connector/oauth/abc123";
    private const string LoopbackRedirectUri = "http://localhost:33418/callback";
    private const string EvilRedirectUri = "https://evil.example.com/callback";
    private const string AttackerChosenName = "Klacks Official";

    private IOAuthClientRepository _repository = null!;
    private ILogger<RegisterOAuthClientCommandHandler> _logger = null!;
    private OAuthClient? _captured;

    [SetUp]
    public void SetUp()
    {
        _captured = null;
        _repository = Substitute.For<IOAuthClientRepository>();
        _repository.AddAsync(Arg.Do<OAuthClient>(client => _captured = client), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _logger = Substitute.For<ILogger<RegisterOAuthClientCommandHandler>>();
    }

    private RegisterOAuthClientCommandHandler Handler(IOAuthRedirectTrustPolicy? policy = null)
    {
        return new RegisterOAuthClientCommandHandler(_repository, policy ?? OAuthTrustTestPolicy.Create(), _logger);
    }

    [Test]
    public async Task Handle_WithoutRedirectUris_ReturnsInvalidRedirectUriError()
    {
        var result = await Handler().Handle(Command([]), CancellationToken.None);

        result.Response.ShouldBeNull();
        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidRedirectUri);
    }

    [Test]
    public async Task Handle_WithRelativeRedirectUri_ReturnsInvalidRedirectUriError()
    {
        var result = await Handler().Handle(Command(["/callback"]), CancellationToken.None);

        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidRedirectUri);
    }

    [Test]
    public async Task Handle_WithHttpNonLoopbackRedirectUri_ReturnsInvalidRedirectUriError()
    {
        var result = await Handler().Handle(Command(["http://evil.example.com/callback"]), CancellationToken.None);

        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidRedirectUri);
        result.ErrorStatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task Handle_WithUntrustedHttpsRedirectUri_RejectsNamingTheHostAndPointingToTheAdministrator()
    {
        var result = await Handler().Handle(Command([EvilRedirectUri]), CancellationToken.None);

        result.Response.ShouldBeNull();
        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidRedirectUri);
        result.Error.ErrorDescription.ShouldContain("https://evil.example.com");
        result.Error.ErrorDescription.ShouldContain("administrator");
        await _repository.DidNotReceive().AddAsync(Arg.Any<OAuthClient>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_WithOnlyUntrustedRedirectUris_NamesEveryRefusedHost()
    {
        var result = await Handler().Handle(
            Command(["cursor://anysphere.cursor-retrieval/oauth", EvilRedirectUri]),
            CancellationToken.None);

        result.Error!.ErrorDescription.ShouldContain("cursor://anysphere.cursor-retrieval");
        result.Error.ErrorDescription.ShouldContain("https://evil.example.com");
    }

    [Test]
    public async Task Handle_WithTrustedAndUntrustedUris_KeepsOnlyTheTrustedOnesInStoreAndResponse()
    {
        var result = await Handler().Handle(
            Command(["cursor://anysphere.cursor-retrieval/oauth", LoopbackRedirectUri, EvilRedirectUri]),
            CancellationToken.None);

        result.Error.ShouldBeNull();
        result.Response!.RedirectUris.ShouldBe([LoopbackRedirectUri]);
        JsonSerializer.Deserialize<List<string>>(_captured!.RedirectUrisJson).ShouldBe([LoopbackRedirectUri]);
    }

    [Test]
    public async Task Handle_WithDuplicateTrustedUris_StoresEachOnce()
    {
        var result = await Handler().Handle(Command([ClaudeRedirectUri, ClaudeRedirectUri]), CancellationToken.None);

        result.Response!.RedirectUris.ShouldBe([ClaudeRedirectUri]);
    }

    [Test]
    public async Task Handle_WithRefusedTarget_LogsTheTargetAtInformation()
    {
        await Handler().Handle(Command([EvilRedirectUri]), CancellationToken.None);

        _logger.ReceivedCalls().Count(call =>
                call.GetMethodInfo().Name == nameof(ILogger.Log)
                && call.GetArguments()[0] is LogLevel.Information)
            .ShouldBe(1);
    }

    [Test]
    public async Task Handle_WithLoopbackHttpRedirectUri_SucceedsAsProgramOnThisComputer()
    {
        var result = await Handler().Handle(Command([LoopbackRedirectUri]), CancellationToken.None);

        result.Error.ShouldBeNull();
        result.Response!.ClientName.ShouldBe(OAuthTrustedRedirects.LocalProgramDisplayName);
    }

    [Test]
    public async Task Handle_WithLoopbackSwitchOff_RejectsLocalPrograms()
    {
        var policy = OAuthTrustTestPolicy.WithSetting(SettingKeys.OAuthAllowLoopback, "false");

        var result = await Handler(policy).Handle(Command([LoopbackRedirectUri]), CancellationToken.None);

        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidRedirectUri);
        result.Error.ErrorDescription.ShouldBe(OAuthTrustMessages.LocalProgramsDisabledDescription);
    }

    [Test]
    public async Task Handle_WithModeOff_RejectsEvenListedVendors()
    {
        var policy = OAuthTrustTestPolicy.WithSetting(SettingKeys.OAuthConnectionsMode, "off");

        var result = await Handler(policy).Handle(Command([ClaudeRedirectUri]), CancellationToken.None);

        result.Error!.Error.ShouldBe(OAuthConstants.ErrorAccessDenied);
        result.Error.ErrorDescription.ShouldBe(OAuthTrustMessages.ConnectionsDisabledDescription);
        result.ErrorStatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        await _repository.DidNotReceive().AddAsync(Arg.Any<OAuthClient>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_WithCustomTargetOfTheAdministrator_Succeeds()
    {
        var policy = OAuthTrustTestPolicy.WithCustomTargets("https://mcp.example.org/cb");

        var result = await Handler(policy).Handle(Command(["https://mcp.example.org/cb"]), CancellationToken.None);

        result.Error.ShouldBeNull();
        result.Response!.ClientName.ShouldBe("mcp.example.org");
    }

    [Test]
    public async Task Handle_WithUnsupportedGrantTypes_ReturnsInvalidClientMetadataError()
    {
        var request = new OAuthClientRegistrationRequest([ClaudeRedirectUri], ["client_credentials"], null, null);

        var result = await Handler().Handle(new RegisterOAuthClientCommand(request), CancellationToken.None);

        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidClientMetadata);
    }

    [Test]
    public async Task Handle_WithConfidentialAuthMethod_ReturnsInvalidClientMetadataError()
    {
        var request = new OAuthClientRegistrationRequest([ClaudeRedirectUri], null, null, "client_secret_post");

        var result = await Handler().Handle(new RegisterOAuthClientCommand(request), CancellationToken.None);

        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidClientMetadata);
    }

    [Test]
    public async Task Handle_HappyPath_PersistsClientAndReturnsPublicClientMetadata()
    {
        var result = await Handler().Handle(Command([ClaudeRedirectUri]), CancellationToken.None);

        result.Error.ShouldBeNull();
        var response = result.Response!;
        response.ClientId.ShouldNotBeNullOrWhiteSpace();
        response.ClientName.ShouldBe(OAuthTrustedRedirects.ClaudeDisplayName);
        response.RedirectUris.ShouldBe([ClaudeRedirectUri]);
        response.TokenEndpointAuthMethod.ShouldBe(OAuthConstants.TokenEndpointAuthMethodNone);
        response.GrantTypes.ShouldBe([OAuthConstants.GrantTypeAuthorizationCode]);
        response.ResponseTypes.ShouldBe([OAuthConstants.ResponseTypeCode]);

        _captured.ShouldNotBeNull();
        _captured!.ClientId.ShouldBe(response.ClientId);
        JsonSerializer.Deserialize<List<string>>(_captured.RedirectUrisJson).ShouldBe([ClaudeRedirectUri]);
    }

    [Test]
    public async Task Handle_NameComesFromTheListEntryOfTheFirstTrustedUri()
    {
        var result = await Handler().Handle(Command([ChatGptRedirectUri, ClaudeRedirectUri]), CancellationToken.None);

        result.Response!.ClientName.ShouldBe(OAuthTrustedRedirects.ChatGptDisplayName);
        result.Response.ClientName.ShouldNotBe(AttackerChosenName);
    }

    [Test]
    public void OAuthClient_DoesNotStoreAClientName()
    {
        typeof(OAuthClient).GetProperties().Select(p => p.Name).ShouldNotContain("ClientName");
    }

    [Test]
    public void RegistrationRequest_ClientNameOfTheCaller_IsNotPartOfTheModel()
    {
        var json = "{\"client_name\":\"" + AttackerChosenName + "\",\"redirect_uris\":[\"" + ClaudeRedirectUri + "\"]}";

        var request = JsonSerializer.Deserialize<OAuthClientRegistrationRequest>(json);

        request!.RedirectUris.ShouldBe([ClaudeRedirectUri]);
        typeof(OAuthClientRegistrationRequest).GetProperties().Select(p => p.Name).ShouldNotContain("ClientName");
    }

    private static RegisterOAuthClientCommand Command(List<string> redirectUris)
    {
        return new RegisterOAuthClientCommand(new OAuthClientRegistrationRequest(redirectUris, null, null, null));
    }
}
