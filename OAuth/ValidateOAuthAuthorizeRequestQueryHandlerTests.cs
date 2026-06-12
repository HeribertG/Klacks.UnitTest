// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Text.Json;
using Klacks.Api.Application.Handlers.OAuth;
using Klacks.Api.Application.Queries.OAuth;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;

namespace Klacks.UnitTest.OAuth;

[TestFixture]
public class ValidateOAuthAuthorizeRequestQueryHandlerTests
{
    private const string KnownClientId = "client-1";
    private const string UnknownClientId = "client-unknown";
    private const string RegisteredRedirectUri = "https://claude.ai/api/mcp/auth_callback";
    private const string UnregisteredRedirectUri = "https://evil.example.com/callback";
    private const string ClientName = "Claude";
    private const string CodeChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    private IOAuthClientRepository _repository = null!;
    private ValidateOAuthAuthorizeRequestQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IOAuthClientRepository>();
        _repository.GetByClientIdAsync(KnownClientId, Arg.Any<CancellationToken>()).Returns(new OAuthClient
        {
            ClientId = KnownClientId,
            ClientName = ClientName,
            RedirectUrisJson = JsonSerializer.Serialize(new List<string> { RegisteredRedirectUri })
        });
        _handler = new ValidateOAuthAuthorizeRequestQueryHandler(_repository);
    }

    [Test]
    public async Task Handle_UnknownClient_RejectsWithoutRedirect()
    {
        var result = await _handler.Handle(Query(clientId: UnknownClientId), CancellationToken.None);

        result.IsValid.ShouldBeFalse();
        result.CanRedirectError.ShouldBeFalse();
        result.Error.ShouldBe(OAuthConstants.ErrorInvalidClient);
    }

    [Test]
    public async Task Handle_MissingClientId_RejectsWithoutRedirect()
    {
        var result = await _handler.Handle(Query(clientId: null), CancellationToken.None);

        result.IsValid.ShouldBeFalse();
        result.CanRedirectError.ShouldBeFalse();
        result.Error.ShouldBe(OAuthConstants.ErrorInvalidRequest);
    }

    [Test]
    public async Task Handle_UnregisteredRedirectUri_RejectsWithoutRedirect()
    {
        var result = await _handler.Handle(Query(redirectUri: UnregisteredRedirectUri), CancellationToken.None);

        result.IsValid.ShouldBeFalse();
        result.CanRedirectError.ShouldBeFalse();
        result.Error.ShouldBe(OAuthConstants.ErrorInvalidRequest);
    }

    [Test]
    public async Task Handle_MissingRedirectUriWithSingleRegistration_UsesRegisteredUri()
    {
        var result = await _handler.Handle(Query(redirectUri: null), CancellationToken.None);

        result.IsValid.ShouldBeTrue();
        result.EffectiveRedirectUri.ShouldBe(RegisteredRedirectUri);
    }

    [Test]
    public async Task Handle_UnsupportedResponseType_RejectsWithRedirect()
    {
        var result = await _handler.Handle(Query(responseType: "token"), CancellationToken.None);

        result.IsValid.ShouldBeFalse();
        result.CanRedirectError.ShouldBeTrue();
        result.Error.ShouldBe(OAuthConstants.ErrorUnsupportedResponseType);
        result.EffectiveRedirectUri.ShouldBe(RegisteredRedirectUri);
    }

    [Test]
    public async Task Handle_MissingCodeChallenge_RejectsWithRedirect()
    {
        var result = await _handler.Handle(Query(codeChallenge: null), CancellationToken.None);

        result.IsValid.ShouldBeFalse();
        result.CanRedirectError.ShouldBeTrue();
        result.Error.ShouldBe(OAuthConstants.ErrorInvalidRequest);
    }

    [Test]
    public async Task Handle_PlainCodeChallengeMethod_RejectsWithRedirect()
    {
        var result = await _handler.Handle(Query(codeChallengeMethod: "plain"), CancellationToken.None);

        result.IsValid.ShouldBeFalse();
        result.CanRedirectError.ShouldBeTrue();
        result.Error.ShouldBe(OAuthConstants.ErrorInvalidRequest);
    }

    [Test]
    public async Task Handle_HappyPath_ReturnsClientNameAndRedirectUri()
    {
        var result = await _handler.Handle(Query(), CancellationToken.None);

        result.IsValid.ShouldBeTrue();
        result.ClientName.ShouldBe(ClientName);
        result.EffectiveRedirectUri.ShouldBe(RegisteredRedirectUri);
    }

    private static ValidateOAuthAuthorizeRequestQuery Query(
        string? clientId = KnownClientId,
        string? redirectUri = RegisteredRedirectUri,
        string? responseType = OAuthConstants.ResponseTypeCode,
        string? codeChallenge = CodeChallenge,
        string? codeChallengeMethod = OAuthConstants.CodeChallengeMethodS256)
    {
        return new ValidateOAuthAuthorizeRequestQuery(clientId, redirectUri, responseType, codeChallenge, codeChallengeMethod);
    }
}
