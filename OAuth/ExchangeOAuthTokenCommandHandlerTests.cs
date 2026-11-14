// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Klacks.Api.Application.Commands.OAuth;
using Klacks.Api.Application.Handlers.OAuth;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Application.Interfaces.OAuth;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.UnitTest.TestHelpers;
using Klacks.Api.Infrastructure.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.OAuth;

[TestFixture]
public class ExchangeOAuthTokenCommandHandlerTests
{
    private const string KnownClientId = "client-1";
    private const string OtherClientId = "client-2";
    private const string RegisteredNameOfAnOldClient = "Name the registrant chose";
    private const string ToolName = "Claude";
    private const string RedirectUri = "https://claude.ai/api/mcp/auth_callback";
    private const string UserId = "user-1";
    private const string AuthorizationCode = "test-authorization-code";
    private const string CodeVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string WrongCodeVerifier = "wrong-verifier-wrong-verifier-wrong-verifier";
    private const int ExpectedExpiresInSeconds = 30 * 24 * 60 * 60;

    private static readonly DateTimeOffset FixedUtcNow = DateTimeOffset.Parse(
        "2026-07-17T10:00:00Z",
        CultureInfo.InvariantCulture,
        DateTimeStyles.AdjustToUniversal);

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

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(FixedUtcNow);

        _codeStore = new OAuthAuthorizationCodeStore(
            new DataBaseContext(options, Substitute.For<IHttpContextAccessor>()),
            timeProvider);
        _handler = Handler(OAuthTrustTestPolicy.Create());
    }

    private ExchangeOAuthTokenCommandHandler Handler(IOAuthRedirectTrustPolicy policy)
    {
        return new ExchangeOAuthTokenCommandHandler(_clientRepository, _codeStore, _tokenRepository, policy);
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
        await StoreCode();

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
        await StoreCode();

        var result = await _handler.Handle(Command(clientId: OtherClientId), CancellationToken.None);

        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidGrant);
    }

    [Test]
    public async Task Handle_RedirectUriMismatch_ReturnsInvalidGrant()
    {
        await StoreCode();

        var result = await _handler.Handle(Command(redirectUri: "https://claude.ai/other"), CancellationToken.None);

        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidGrant);
    }

    [Test]
    public async Task Handle_WrongCodeVerifier_ReturnsInvalidGrant()
    {
        await StoreCode();

        var result = await _handler.Handle(Command(codeVerifier: WrongCodeVerifier), CancellationToken.None);

        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidGrant);
        _capturedToken.ShouldBeNull();
    }

    [Test]
    public async Task Handle_CodeReuse_SecondExchangeReturnsInvalidGrant()
    {
        await StoreCode();

        var first = await _handler.Handle(Command(), CancellationToken.None);
        var second = await _handler.Handle(Command(), CancellationToken.None);

        first.Error.ShouldBeNull();
        second.Response.ShouldBeNull();
        second.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidGrant);
    }

    [Test]
    public async Task Handle_HappyPath_IssuesPersonalAccessTokenAsOAuthAccessToken()
    {
        await StoreCode();
        var before = DateTime.UtcNow;

        var result = await _handler.Handle(Command(), CancellationToken.None);

        var after = DateTime.UtcNow;
        result.Error.ShouldBeNull();
        var response = result.Response!;
        response.AccessToken.ShouldStartWith(PatConstants.TokenPrefix);
        response.TokenType.ShouldBe(OAuthConstants.TokenTypeBearer);
        response.ExpiresIn.ShouldBe(ExpectedExpiresInSeconds);
        response.Scope.ShouldBe(OAuthConstants.McpToolsReadScope);

        _capturedToken.ShouldNotBeNull();
        _capturedToken!.UserId.ShouldBe(UserId);
        _capturedToken.Name.ShouldBe(OAuthConstants.AccessTokenNamePrefix + ToolName);
        _capturedToken.IsOAuthIssued.ShouldBeTrue();
        _capturedToken.OAuthToolName.ShouldBe(ToolName);
        _capturedToken.OAuthRedirectHost.ShouldBe("claude.ai");
        _capturedToken.ExpiresAt!.Value.ShouldBeInRange(
            before.AddDays(OAuthConstants.AccessTokenExpiresInDays),
            after.AddDays(OAuthConstants.AccessTokenExpiresInDays));
        _capturedToken.AccessMode.ShouldBe(PersonalAccessTokenAccessMode.Read);
    }

    [Test]
    public async Task Handle_OutsideThePlayground_TokenLivesThirtyDays()
    {
        await StoreCode();
        var before = DateTime.UtcNow;

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.Response!.ExpiresIn.ShouldBe((int)TimeSpan.FromDays(OAuthConstants.AccessTokenExpiresInDays).TotalSeconds);
        _capturedToken!.ExpiresAt!.Value.ShouldBeInRange(
            before.AddDays(OAuthConstants.AccessTokenExpiresInDays),
            DateTime.UtcNow.AddDays(OAuthConstants.AccessTokenExpiresInDays));
    }

    [Test]
    public async Task Handle_OnThePlayground_TokenLivesAtMostTwentyFourHours()
    {
        await StoreCode();
        var handler = Handler(OAuthTrustTestPolicy.Create(playground: true));
        var before = DateTime.UtcNow;

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.Error.ShouldBeNull();
        result.Response!.ExpiresIn.ShouldBe((int)TimeSpan.FromHours(OAuthConstants.PlaygroundAccessTokenExpiresInHours).TotalSeconds);
        _capturedToken!.ExpiresAt!.Value.ShouldBeInRange(
            before.AddHours(OAuthConstants.PlaygroundAccessTokenExpiresInHours),
            DateTime.UtcNow.AddHours(OAuthConstants.PlaygroundAccessTokenExpiresInHours));
        _capturedToken.ExpiresAt!.Value.ShouldBeLessThanOrEqualTo(DateTime.UtcNow.AddHours(24));
    }

    [Test]
    public async Task Handle_CodeBoundToWrite_IssuesWriteTokenWithFullScope()
    {
        await StoreCode(PersonalAccessTokenAccessMode.Write);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.Error.ShouldBeNull();
        result.Response!.Scope.ShouldBe(OAuthConstants.McpToolsScope);
        _capturedToken!.AccessMode.ShouldBe(PersonalAccessTokenAccessMode.Write);
    }

    [Test]
    public async Task Handle_WriteSwitchTurnedOffAfterCodeIssue_ReturnsInvalidGrantAndIssuesNothing()
    {
        await StoreCode(PersonalAccessTokenAccessMode.Write);
        var handler = Handler(OAuthTrustTestPolicy.WithSetting(SettingKeys.OAuthAllowWrite, "false"));

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.Response.ShouldBeNull();
        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidGrant);
        _capturedToken.ShouldBeNull();
    }

    [Test]
    public async Task Handle_WriteSwitchOff_StillIssuesReadCode()
    {
        await StoreCode(PersonalAccessTokenAccessMode.Read);
        var handler = Handler(OAuthTrustTestPolicy.WithSetting(SettingKeys.OAuthAllowWrite, "false"));

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.Error.ShouldBeNull();
        _capturedToken!.AccessMode.ShouldBe(PersonalAccessTokenAccessMode.Read);
    }

    [Test]
    public async Task Handle_ModeOffAfterCodeIssue_ReturnsInvalidGrant()
    {
        await StoreCode();
        var handler = Handler(OAuthTrustTestPolicy.WithSetting(SettingKeys.OAuthConnectionsMode, "off"));

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidGrant);
        _capturedToken.ShouldBeNull();
    }

    [Test]
    public async Task Handle_CodeBoundToUntrustedRedirect_ReturnsInvalidGrant()
    {
        await StoreCode(redirectUri: "https://evil.example.com/callback");

        var result = await _handler.Handle(Command(redirectUri: "https://evil.example.com/callback"), CancellationToken.None);

        result.Error!.Error.ShouldBe(OAuthConstants.ErrorInvalidGrant);
        _capturedToken.ShouldBeNull();
    }

    [Test]
    public async Task Handle_TokenNameAndToolNameComeFromTheListEntryNotFromTheRegisteredClientName()
    {
        await StoreCode();

        await _handler.Handle(Command(), CancellationToken.None);

        _capturedToken!.Name.ShouldNotContain(RegisteredNameOfAnOldClient);
        _capturedToken.OAuthToolName.ShouldNotBe(RegisteredNameOfAnOldClient);
    }

    [Test]
    public async Task Handle_LoopbackRedirect_NamesTheTokenAfterProgramOnThisComputerWithItsHost()
    {
        const string loopback = "http://127.0.0.1:33418/callback";
        await StoreCode(redirectUri: loopback);

        await _handler.Handle(Command(redirectUri: loopback), CancellationToken.None);

        _capturedToken!.Name.ShouldBe(OAuthConstants.AccessTokenNamePrefix + OAuthTrustedRedirects.LocalProgramDisplayName);
        _capturedToken.OAuthRedirectHost.ShouldBe("127.0.0.1");
    }


    private static OAuthClient Client(string clientId)
    {
        return new OAuthClient
        {
            ClientId = clientId,
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

    private async Task StoreCode(PersonalAccessTokenAccessMode accessMode = PersonalAccessTokenAccessMode.Read, string redirectUri = RedirectUri)
    {
        await _codeStore.StoreAsync(AuthorizationCode, new OAuthAuthorizationCodeData(
            UserId: UserId,
            ClientId: KnownClientId,
            RedirectUri: redirectUri,
            CodeChallenge: ComputeS256Challenge(CodeVerifier),
            AccessMode: accessMode));
    }

    private static string ComputeS256Challenge(string codeVerifier)
    {
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(codeVerifier)));
    }
}
