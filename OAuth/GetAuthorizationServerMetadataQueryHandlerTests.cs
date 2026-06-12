// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Configuration;
using Klacks.Api.Application.Handlers.OAuth;
using Klacks.Api.Application.Queries.OAuth;
using Klacks.Api.Domain.Constants;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.OAuth;

[TestFixture]
public class GetAuthorizationServerMetadataQueryHandlerTests
{
    private const string PublicBaseUrlWithTrailingSlash = "https://klacks.example.com/";
    private const string ExpectedBaseUrl = "https://klacks.example.com";

    private GetAuthorizationServerMetadataQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        var options = Options.Create(new McpPublicEndpointOptions { PublicBaseUrl = PublicBaseUrlWithTrailingSlash });
        _handler = new GetAuthorizationServerMetadataQueryHandler(options);
    }

    [Test]
    public async Task Handle_ReturnsIssuerWithoutTrailingSlash()
    {
        var metadata = await _handler.Handle(new GetAuthorizationServerMetadataQuery(), CancellationToken.None);

        metadata.Issuer.ShouldBe(ExpectedBaseUrl);
    }

    [Test]
    public async Task Handle_ReturnsAllRequiredEndpoints()
    {
        var metadata = await _handler.Handle(new GetAuthorizationServerMetadataQuery(), CancellationToken.None);

        metadata.AuthorizationEndpoint.ShouldBe($"{ExpectedBaseUrl}/oauth/authorize");
        metadata.TokenEndpoint.ShouldBe($"{ExpectedBaseUrl}/oauth/token");
        metadata.RegistrationEndpoint.ShouldBe($"{ExpectedBaseUrl}/oauth/register");
    }

    [Test]
    public async Task Handle_AdvertisesOAuth21CapabilitiesForPublicClients()
    {
        var metadata = await _handler.Handle(new GetAuthorizationServerMetadataQuery(), CancellationToken.None);

        metadata.ResponseTypesSupported.ShouldBe([OAuthConstants.ResponseTypeCode]);
        metadata.GrantTypesSupported.ShouldBe([OAuthConstants.GrantTypeAuthorizationCode]);
        metadata.CodeChallengeMethodsSupported.ShouldBe([OAuthConstants.CodeChallengeMethodS256]);
        metadata.TokenEndpointAuthMethodsSupported.ShouldBe([OAuthConstants.TokenEndpointAuthMethodNone]);
        metadata.ScopesSupported.ShouldBe([OAuthConstants.McpToolsScope]);
    }
}
