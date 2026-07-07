using System.Text.Encodings.Web;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Bots;
using Klacks.Api.Domain.Models.Bots;
using Klacks.Api.Domain.Security;
using Klacks.Api.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Authentification;

[TestFixture]
public class KlacksBotTokenAuthenticationHandlerTests
{
    private const string InvalidTokenMessage = "Invalid or expired bot token.";
    private const string BearerPrefix = "Bearer ";

    private IKlacksBotTokenRepository _tokenRepository = null!;

    [SetUp]
    public void SetUp()
    {
        _tokenRepository = Substitute.For<IKlacksBotTokenRepository>();
    }

    [Test]
    public async Task AuthenticateAsync_WithoutAuthorizationHeader_ReturnsNoResult()
    {
        var result = await AuthenticateAsync(null);

        result.None.ShouldBeTrue();
    }

    [Test]
    public async Task AuthenticateAsync_PersonalAccessToken_ReturnsNoResult()
    {
        var (patPlaintext, _, _) = PatTokenGenerator.Generate();

        var result = await AuthenticateAsync(BearerPrefix + patPlaintext);

        result.None.ShouldBeTrue();
        await _tokenRepository.DidNotReceive().GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AuthenticateAsync_UnknownToken_Fails()
    {
        var (plaintext, _, _) = KlacksBotTokenGenerator.Generate();

        var result = await AuthenticateAsync(BearerPrefix + plaintext);

        result.Succeeded.ShouldBeFalse();
        result.Failure!.Message.ShouldBe(InvalidTokenMessage);
    }

    [Test]
    public async Task AuthenticateAsync_ExpiredToken_Fails()
    {
        var plaintext = SetupStoredToken(DateTime.UtcNow.AddMinutes(-5));

        var result = await AuthenticateAsync(BearerPrefix + plaintext);

        result.Succeeded.ShouldBeFalse();
        result.Failure!.Message.ShouldBe(InvalidTokenMessage);
    }

    [Test]
    public async Task AuthenticateAsync_ValidToken_SucceedsWithBotIdentityClaimOnly()
    {
        var plaintext = SetupStoredToken(DateTime.UtcNow.AddDays(1));

        var result = await AuthenticateAsync(BearerPrefix + plaintext);

        result.Succeeded.ShouldBeTrue();
        var principal = result.Ticket!.Principal;
        principal.Claims.Count().ShouldBe(1);
        principal.FindFirst(KlacksBotTokenConstants.BotIdentityClaimType).ShouldNotBeNull();
        principal.IsInRole(Roles.Admin).ShouldBeFalse();
        principal.IsInRole(Roles.Authorised).ShouldBeFalse();
        principal.IsInRole(Roles.User).ShouldBeFalse();
        result.Ticket.AuthenticationScheme.ShouldBe(KlacksBotTokenConstants.SchemeName);
    }

    private async Task<AuthenticateResult> AuthenticateAsync(string? authorizationHeader)
    {
        var optionsMonitor = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        optionsMonitor.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());

        var handler = new KlacksBotTokenAuthenticationHandler(
            optionsMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            _tokenRepository);

        var context = new DefaultHttpContext();
        if (authorizationHeader != null)
        {
            context.Request.Headers.Authorization = authorizationHeader;
        }

        var scheme = new AuthenticationScheme(KlacksBotTokenConstants.SchemeName, KlacksBotTokenConstants.SchemeName, typeof(KlacksBotTokenAuthenticationHandler));
        await handler.InitializeAsync(scheme, context);

        return await handler.AuthenticateAsync();
    }

    private string SetupStoredToken(DateTime? expiresAt)
    {
        var (plaintext, tokenHash, tokenPrefix) = KlacksBotTokenGenerator.Generate();
        var token = new KlacksBotToken
        {
            Id = Guid.NewGuid(),
            Name = "test-token",
            TokenHash = tokenHash,
            TokenPrefix = tokenPrefix,
            ExpiresAt = expiresAt
        };

        _tokenRepository.GetByHashAsync(tokenHash, Arg.Any<CancellationToken>()).Returns(token);

        return plaintext;
    }
}
