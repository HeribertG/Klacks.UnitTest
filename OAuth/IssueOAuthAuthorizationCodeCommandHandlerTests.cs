// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Text.Json;
using Klacks.Api.Application.Commands.OAuth;
using Klacks.Api.Application.Handlers.OAuth;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Authentication;

namespace Klacks.UnitTest.OAuth;

[TestFixture]
public class IssueOAuthAuthorizationCodeCommandHandlerTests
{
    private const string KnownClientId = "client-1";
    private const string ClientName = "Claude";
    private const string RegisteredRedirectUri = "https://claude.ai/api/mcp/auth_callback";
    private const string UnregisteredRedirectUri = "https://evil.example.com/callback";
    private const string Email = "user@example.com";
    private const string Password = "secret";
    private const string WrongPassword = "wrong";
    private const string UserId = "user-1";
    private const string CodeChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    private IOAuthClientRepository _clientRepository = null!;
    private IAuthenticationService _authenticationService = null!;
    private OAuthAuthorizationCodeStore _codeStore = null!;
    private IssueOAuthAuthorizationCodeCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _clientRepository = Substitute.For<IOAuthClientRepository>();
        _clientRepository.GetByClientIdAsync(KnownClientId, Arg.Any<CancellationToken>()).Returns(new OAuthClient
        {
            ClientId = KnownClientId,
            ClientName = ClientName,
            RedirectUrisJson = JsonSerializer.Serialize(new List<string> { RegisteredRedirectUri })
        });

        _authenticationService = Substitute.For<IAuthenticationService>();
        _authenticationService.ValidateCredentialsAsync(Email, Password)
            .Returns((true, new AppUser { Id = UserId, Email = Email }));
        _authenticationService.ValidateCredentialsAsync(Email, WrongPassword)
            .Returns((false, (AppUser?)null));

        _codeStore = new OAuthAuthorizationCodeStore();
        _handler = new IssueOAuthAuthorizationCodeCommandHandler(_clientRepository, _authenticationService, _codeStore);
    }

    [Test]
    public async Task Handle_UnknownClient_ReturnsInvalidClient()
    {
        var command = new IssueOAuthAuthorizationCodeCommand(Email, Password, "client-unknown", RegisteredRedirectUri, CodeChallenge, null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Code.ShouldBeNull();
        result.Error.ShouldBe(OAuthConstants.ErrorInvalidClient);
    }

    [Test]
    public async Task Handle_UnregisteredRedirectUri_ReturnsInvalidRequest()
    {
        var command = new IssueOAuthAuthorizationCodeCommand(Email, Password, KnownClientId, UnregisteredRedirectUri, CodeChallenge, null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Code.ShouldBeNull();
        result.Error.ShouldBe(OAuthConstants.ErrorInvalidRequest);
    }

    [Test]
    public async Task Handle_InvalidCredentials_ReturnsAccessDenied()
    {
        var command = new IssueOAuthAuthorizationCodeCommand(Email, WrongPassword, KnownClientId, RegisteredRedirectUri, CodeChallenge, null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Code.ShouldBeNull();
        result.Error.ShouldBe(OAuthConstants.ErrorAccessDenied);
    }

    [Test]
    public async Task Handle_HappyPath_StoresCodeBoundToClientRedirectUriAndChallenge()
    {
        var command = new IssueOAuthAuthorizationCodeCommand(Email, Password, KnownClientId, RegisteredRedirectUri, CodeChallenge, OAuthConstants.McpToolsScope);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Error.ShouldBeNull();
        result.Code.ShouldNotBeNullOrWhiteSpace();

        var stored = _codeStore.Consume(result.Code!);
        stored.ShouldNotBeNull();
        stored!.UserId.ShouldBe(UserId);
        stored.ClientId.ShouldBe(KnownClientId);
        stored.ClientName.ShouldBe(ClientName);
        stored.RedirectUri.ShouldBe(RegisteredRedirectUri);
        stored.CodeChallenge.ShouldBe(CodeChallenge);
        stored.Scope.ShouldBe(OAuthConstants.McpToolsScope);
    }
}
