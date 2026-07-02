using System.Text.Encodings.Web;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Models.Imports;
using Klacks.Api.Domain.Security;
using Klacks.Api.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Authentification;

[TestFixture]
public class ErpImportTokenAuthenticationHandlerTests
{
    private const string InvalidTokenMessage = "Invalid or expired ERP import token.";
    private const string BearerPrefix = "Bearer ";
    private static readonly Guid TestDropPointId = Guid.Parse("11112222-3333-4444-5555-666677778888");

    private IErpImportTokenRepository _tokenRepository = null!;
    private IErpDropPointRepository _dropPointRepository = null!;

    [SetUp]
    public void SetUp()
    {
        _tokenRepository = Substitute.For<IErpImportTokenRepository>();
        _dropPointRepository = Substitute.For<IErpDropPointRepository>();
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
        var (plaintext, _, _) = ErpImportTokenGenerator.Generate();

        var result = await AuthenticateAsync(BearerPrefix + plaintext);

        result.Succeeded.ShouldBeFalse();
        result.Failure!.Message.ShouldBe(InvalidTokenMessage);
    }

    [Test]
    public async Task AuthenticateAsync_ExpiredToken_Fails()
    {
        var (plaintext, _) = SetupStoredToken(DateTime.UtcNow.AddMinutes(-5));
        SetupEnabledDropPoint();

        var result = await AuthenticateAsync(BearerPrefix + plaintext);

        result.Succeeded.ShouldBeFalse();
        result.Failure!.Message.ShouldBe(InvalidTokenMessage);
    }

    [Test]
    public async Task AuthenticateAsync_DisabledDropPoint_Fails()
    {
        var (plaintext, _) = SetupStoredToken(DateTime.UtcNow.AddDays(1));
        SetupEnabledDropPoint(isEnabled: false);

        var result = await AuthenticateAsync(BearerPrefix + plaintext);

        result.Succeeded.ShouldBeFalse();
        result.Failure!.Message.ShouldBe(InvalidTokenMessage);
    }

    [Test]
    public async Task AuthenticateAsync_ValidToken_SucceedsWithDropPointClaimOnly()
    {
        var (plaintext, _) = SetupStoredToken(DateTime.UtcNow.AddDays(1));
        SetupEnabledDropPoint();

        var result = await AuthenticateAsync(BearerPrefix + plaintext);

        result.Succeeded.ShouldBeTrue();
        var principal = result.Ticket!.Principal;
        principal.FindFirst(ErpImportTokenConstants.DropPointIdClaimType)!.Value.ShouldBe(TestDropPointId.ToString());
        principal.Claims.Count().ShouldBe(1);
        result.Ticket.AuthenticationScheme.ShouldBe(ErpImportTokenConstants.SchemeName);
    }

    private async Task<AuthenticateResult> AuthenticateAsync(string? authorizationHeader)
    {
        var optionsMonitor = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        optionsMonitor.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());

        var handler = new ErpImportTokenAuthenticationHandler(
            optionsMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            _tokenRepository,
            _dropPointRepository);

        var context = new DefaultHttpContext();
        if (authorizationHeader != null)
        {
            context.Request.Headers.Authorization = authorizationHeader;
        }

        var scheme = new AuthenticationScheme(ErpImportTokenConstants.SchemeName, ErpImportTokenConstants.SchemeName, typeof(ErpImportTokenAuthenticationHandler));
        await handler.InitializeAsync(scheme, context);

        return await handler.AuthenticateAsync();
    }

    private (string Plaintext, ErpImportToken Token) SetupStoredToken(DateTime? expiresAt)
    {
        var (plaintext, tokenHash, tokenPrefix) = ErpImportTokenGenerator.Generate();
        var token = new ErpImportToken
        {
            Id = Guid.NewGuid(),
            DropPointId = TestDropPointId,
            Name = "test-token",
            TokenHash = tokenHash,
            TokenPrefix = tokenPrefix,
            ExpiresAt = expiresAt
        };

        _tokenRepository.GetByHashAsync(tokenHash, Arg.Any<CancellationToken>()).Returns(token);

        return (plaintext, token);
    }

    private void SetupEnabledDropPoint(bool isEnabled = true)
    {
        var dropPoint = new ErpDropPoint
        {
            Id = TestDropPointId,
            Name = "Test ERP",
            SourceSystemId = "erp-1",
            BucketPrefix = "customer-1",
            IsEnabled = isEnabled
        };

        _dropPointRepository.GetNoTracking(TestDropPointId).Returns(dropPoint);
    }
}
