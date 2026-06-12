// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Klacks.Api.Application.Commands.OAuth;
using Klacks.Api.Application.Handlers.OAuth;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Authentication;

namespace Klacks.UnitTest.OAuth;

[TestFixture]
public class ExchangeOAuthTokenCommandHandlerTests
{
    private const string KnownClientId = "client-1";
    private const string OtherClientId = "client-2";
    private const string ClientName = "Claude";
    private const string RedirectUri = "https://claude.ai/api/mcp/auth_callback";
    private const string UserId = "user-1";
    private const string AuthorizationCode = "test-authorization-code";
    private const string CodeVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string WrongCodeVerifier = "wrong-verifier-wrong-verifier-wrong-verifier";
    private const int ExpectedExpiresInSeconds = 30 * 24 * 60 * 60;

    private IOAuthClientRepository _clientRepository = null!;
    private OAuthAuthorizationCodeStore _codeStore = null!;
    private IPersonalAccessTokenRepository _tokenRepository = null!;
    private ExchangeOAuthTokenCommandHandler _handler = null!;
    private PersonalAccessToken? _capturedToken;

    [SetUp]
    public void SetUp()
    {
        _capturedToken = null;

        _clientRepository = Substitute.For<IOAuthClientRepository>();
        _clientRepository.GetByClientIdAsync(KnownClientId, Arg.Any<CancellationToken>())
            .Returns(Client(KnownClientId));
        _clientRepository.GetByClientIdAsync(OtherClientId, Arg.Any<CancellationToken>())
            .Returns(Client(OtherClientId));

        _tokenRepository = Substitute.For<IPersonalAccessTokenRepository>();
        _tokenRepository.AddAsync(Arg.Do<PersonalAccessToken>(token => _capturedToken = token), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _codeStore = new OAuthAuthorizationCodeStore();
        _handler = new ExchangeOAuthTokenCommandHandler(_clientRepository, _codeStore, _tokenRepository);
    }

    [Test]
    public async Task Handle_UnsupportedGrantType_ReturnsUnsupportedGrantType()
    {
        var result = await _handler.Handle(Command(grantType: "client_credentials"), CancellationToken.None);

        result.Response.ShouldBeNull();
        result.Error!.Error.ShouldBe(OAuthConstants.ErrorUnsupportedGrantType);
    }

    [Test]
    public async Task Handle_MissingCodeVerifier_ReturnsInvalidRequest()
    {
        StoreCode();

        var result = await _handler.Handle(Command(codeVerifier: null), CancellationToken.None);

        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidRequest);
    }

    [Test]
    public async Task Handle_UnknownClient_ReturnsInvalidClientWith401()
    {
        var result = await _handler.Handle(Command(clientId: "client-unknown"), CancellationToken.None);

        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidClient);
        result.ErrorStatusCode.ShouldBe(401);
    }

    [Test]
    public async Task Handle_UnknownCode_ReturnsInvalidGrant()
    {
        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidGrant);
    }

    [Test]
    public async Task Handle_CodeIssuedToOtherClient_ReturnsInvalidGrant()
    {
        StoreCode();

        var result = await _handler.Handle(Command(clientId: OtherClientId), CancellationToken.None);

        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidGrant);
    }

    [Test]
    public async Task Handle_RedirectUriMismatch_ReturnsInvalidGrant()
    {
        StoreCode();

        var result = await _handler.Handle(Command(redirectUri: "https://claude.ai/other"), CancellationToken.None);

        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidGrant);
    }

    [Test]
    public async Task Handle_WrongCodeVerifier_ReturnsInvalidGrant()
    {
        StoreCode();

        var result = await _handler.Handle(Command(codeVerifier: WrongCodeVerifier), CancellationToken.None);

        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidGrant);
        _capturedToken.ShouldBeNull();
    }

    [Test]
    public async Task Handle_CodeReuse_SecondExchangeReturnsInvalidGrant()
    {
        StoreCode();

        var first = await _handler.Handle(Command(), CancellationToken.None);
        var second = await _handler.Handle(Command(), CancellationToken.None);

        first.Error.ShouldBeNull();
        second.Response.ShouldBeNull();
        second.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidGrant);
    }

    [Test]
    public async Task Handle_HappyPath_IssuesPersonalAccessTokenAsOAuthAccessToken()
    {
        StoreCode();
        var before = DateTime.UtcNow;

        var result = await _handler.Handle(Command(), CancellationToken.None);

        var after = DateTime.UtcNow;
        result.Error.ShouldBeNull();
        var response = result.Response!;
        response.AccessToken.ShouldStartWith(PatConstants.TokenPrefix);
        response.TokenType.ShouldBe(OAuthConstants.TokenTypeBearer);
        response.ExpiresIn.ShouldBe(ExpectedExpiresInSeconds);
        response.Scope.ShouldBe(OAuthConstants.McpToolsScope);

        _capturedToken.ShouldNotBeNull();
        _capturedToken!.UserId.ShouldBe(UserId);
        _capturedToken.Name.ShouldBe(OAuthConstants.AccessTokenNamePrefix + ClientName);
        _capturedToken.ExpiresAt!.Value.ShouldBeInRange(
            before.AddDays(OAuthConstants.AccessTokenExpiresInDays),
            after.AddDays(OAuthConstants.AccessTokenExpiresInDays));
    }

    private static OAuthClient Client(string clientId)
    {
        return new OAuthClient
        {
            ClientId = clientId,
            ClientName = ClientName,
            RedirectUrisJson = JsonSerializer.Serialize(new List<string> { RedirectUri })
        };
    }

    private static ExchangeOAuthTokenCommand Command(
        string? grantType = OAuthConstants.GrantTypeAuthorizationCode,
        string? code = AuthorizationCode,
        string? redirectUri = RedirectUri,
        string? clientId = KnownClientId,
        string? codeVerifier = CodeVerifier)
    {
        return new ExchangeOAuthTokenCommand(grantType, code, redirectUri, clientId, codeVerifier);
    }

    private void StoreCode()
    {
        _codeStore.Store(AuthorizationCode, new OAuthAuthorizationCodeData(
            UserId: UserId,
            ClientId: KnownClientId,
            ClientName: ClientName,
            RedirectUri: RedirectUri,
            CodeChallenge: ComputeS256Challenge(CodeVerifier),
            Scope: OAuthConstants.McpToolsScope));
    }

    private static string ComputeS256Challenge(string codeVerifier)
    {
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(codeVerifier)));
    }
}
