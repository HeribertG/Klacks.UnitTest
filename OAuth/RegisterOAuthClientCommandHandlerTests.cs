// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Text.Json;
using Klacks.Api.Application.Commands.OAuth;
using Klacks.Api.Application.DTOs.OAuth;
using Klacks.Api.Application.Handlers.OAuth;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;

namespace Klacks.UnitTest.OAuth;

[TestFixture]
public class RegisterOAuthClientCommandHandlerTests
{
    private const string ClaudeRedirectUri = "https://claude.ai/api/mcp/auth_callback";
    private const string LoopbackRedirectUri = "http://localhost:33418/callback";
    private const string ClientName = "Claude";

    private IOAuthClientRepository _repository = null!;
    private RegisterOAuthClientCommandHandler _handler = null!;
    private OAuthClient? _captured;

    [SetUp]
    public void SetUp()
    {
        _captured = null;
        _repository = Substitute.For<IOAuthClientRepository>();
        _repository.AddAsync(Arg.Do<OAuthClient>(client => _captured = client), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _handler = new RegisterOAuthClientCommandHandler(_repository);
    }

    [Test]
    public async Task Handle_WithoutRedirectUris_ReturnsInvalidRedirectUriError()
    {
        var result = await _handler.Handle(Command(ClientName, []), CancellationToken.None);

        result.Response.ShouldBeNull();
        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidRedirectUri);
    }

    [Test]
    public async Task Handle_WithRelativeRedirectUri_ReturnsInvalidRedirectUriError()
    {
        var result = await _handler.Handle(Command(ClientName, ["/callback"]), CancellationToken.None);

        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidRedirectUri);
    }

    [Test]
    public async Task Handle_WithHttpNonLoopbackRedirectUri_ReturnsInvalidRedirectUriError()
    {
        var result = await _handler.Handle(Command(ClientName, ["http://evil.example.com/callback"]), CancellationToken.None);

        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidRedirectUri);
    }

    [Test]
    public async Task Handle_WithLoopbackHttpRedirectUri_Succeeds()
    {
        var result = await _handler.Handle(Command(ClientName, [LoopbackRedirectUri]), CancellationToken.None);

        result.Error.ShouldBeNull();
        result.Response.ShouldNotBeNull();
    }

    [Test]
    public async Task Handle_WithUnsupportedGrantTypes_ReturnsInvalidClientMetadataError()
    {
        var request = new OAuthClientRegistrationRequest(ClientName, [ClaudeRedirectUri], ["client_credentials"], null, null);

        var result = await _handler.Handle(new RegisterOAuthClientCommand(request), CancellationToken.None);

        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidClientMetadata);
    }

    [Test]
    public async Task Handle_WithConfidentialAuthMethod_ReturnsInvalidClientMetadataError()
    {
        var request = new OAuthClientRegistrationRequest(ClientName, [ClaudeRedirectUri], null, null, "client_secret_post");

        var result = await _handler.Handle(new RegisterOAuthClientCommand(request), CancellationToken.None);

        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidClientMetadata);
    }

    [Test]
    public async Task Handle_HappyPath_PersistsClientAndReturnsPublicClientMetadata()
    {
        var result = await _handler.Handle(Command(ClientName, [ClaudeRedirectUri]), CancellationToken.None);

        result.Error.ShouldBeNull();
        var response = result.Response!;
        response.ClientId.ShouldNotBeNullOrWhiteSpace();
        response.ClientName.ShouldBe(ClientName);
        response.RedirectUris.ShouldBe([ClaudeRedirectUri]);
        response.TokenEndpointAuthMethod.ShouldBe(OAuthConstants.TokenEndpointAuthMethodNone);
        response.GrantTypes.ShouldBe([OAuthConstants.GrantTypeAuthorizationCode]);
        response.ResponseTypes.ShouldBe([OAuthConstants.ResponseTypeCode]);

        _captured.ShouldNotBeNull();
        _captured!.ClientId.ShouldBe(response.ClientId);
        _captured.ClientName.ShouldBe(ClientName);
        JsonSerializer.Deserialize<List<string>>(_captured.RedirectUrisJson).ShouldBe([ClaudeRedirectUri]);
    }

    private static RegisterOAuthClientCommand Command(string clientName, List<string> redirectUris)
    {
        return new RegisterOAuthClientCommand(new OAuthClientRegistrationRequest(clientName, redirectUris, null, null, null));
    }
}
