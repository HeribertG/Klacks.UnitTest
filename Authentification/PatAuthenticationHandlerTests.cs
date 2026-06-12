// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Security.Claims;
using System.Text.Encodings.Web;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Security;
using Klacks.Api.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Authentification;

[TestFixture]
public class PatAuthenticationHandlerTests
{
    private const string InvalidTokenMessage = "Invalid or expired personal access token.";
    private const string BearerPrefix = "Bearer ";
    private const string ForeignJwtHeader = "Bearer eyJhbGciOiJIUzI1NiJ9.payload.signature";
    private const string BasicAuthHeader = "Basic dXNlcjpwYXNz";
    private const string TestUserId = "7e6f0a44-1111-2222-3333-444455556666";
    private const string TestUserName = "jane.doe";
    private const string TestUserEmail = "jane.doe@example.com";
    private const string TestFirstName = "Jane";
    private const string TestLastName = "Doe";
    private const string TestTokenName = "ci-token";
    private const string AdminRole = "Admin";

    private IPersonalAccessTokenRepository _repository = null!;
    private UserManager<AppUser> _userManager = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IPersonalAccessTokenRepository>();
        _userManager = CreateUserManager();
    }

    [TearDown]
    public void TearDown()
    {
        _userManager.Dispose();
    }

    [Test]
    public async Task AuthenticateAsync_WithoutAuthorizationHeader_ReturnsNoResult()
    {
        var result = await AuthenticateAsync(null);

        result.None.ShouldBeTrue();
    }

    [Test]
    public async Task AuthenticateAsync_BearerTokenWithoutPatPrefix_ReturnsNoResult()
    {
        var result = await AuthenticateAsync(ForeignJwtHeader);

        result.None.ShouldBeTrue();
        await _repository.DidNotReceive().GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AuthenticateAsync_NonBearerScheme_ReturnsNoResult()
    {
        var result = await AuthenticateAsync(BasicAuthHeader);

        result.None.ShouldBeTrue();
    }

    [Test]
    public async Task AuthenticateAsync_UnknownPatToken_Fails()
    {
        var (plaintext, _, _) = PatTokenGenerator.Generate();

        var result = await AuthenticateAsync(BearerPrefix + plaintext);

        result.Succeeded.ShouldBeFalse();
        result.None.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure!.Message.ShouldBe(InvalidTokenMessage);
    }

    [Test]
    public async Task AuthenticateAsync_ExpiredPatToken_Fails()
    {
        var (plaintext, _) = SetupStoredToken(DateTime.UtcNow.AddMinutes(-5), null);
        SetupUser();

        var result = await AuthenticateAsync(BearerPrefix + plaintext);

        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure!.Message.ShouldBe(InvalidTokenMessage);
    }

    [Test]
    public async Task AuthenticateAsync_UserNotFound_Fails()
    {
        var (plaintext, _) = SetupStoredToken(DateTime.UtcNow.AddDays(1), null);

        var result = await AuthenticateAsync(BearerPrefix + plaintext);

        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure!.Message.ShouldBe(InvalidTokenMessage);
    }

    [Test]
    public async Task AuthenticateAsync_ValidPatToken_SucceedsWithJwtMirroredClaims()
    {
        var (plaintext, _) = SetupStoredToken(DateTime.UtcNow.AddDays(1), null);
        var user = SetupUser();

        var result = await AuthenticateAsync(BearerPrefix + plaintext);

        result.Succeeded.ShouldBeTrue();
        var principal = result.Ticket!.Principal;
        principal.FindFirst(ClaimTypes.NameIdentifier)!.Value.ShouldBe(user.Id);
        principal.FindFirst(ClaimTypes.Email)!.Value.ShouldBe(TestUserEmail);
        principal.FindFirst(ClaimTypes.Name)!.Value.ShouldBe(TestUserName);
        principal.FindFirst(ClaimTypes.GivenName)!.Value.ShouldBe(TestFirstName);
        principal.FindFirst(ClaimTypes.Surname)!.Value.ShouldBe(TestLastName);
        principal.IsInRole(AdminRole).ShouldBeTrue();
        result.Ticket.AuthenticationScheme.ShouldBe(PatConstants.SchemeName);
    }

    [Test]
    public async Task AuthenticateAsync_TokenWithoutExpiry_Succeeds()
    {
        var (plaintext, _) = SetupStoredToken(null, null);
        SetupUser();

        var result = await AuthenticateAsync(BearerPrefix + plaintext);

        result.Succeeded.ShouldBeTrue();
    }

    [Test]
    public async Task AuthenticateAsync_NeverUsedToken_UpdatesLastUsed()
    {
        var (plaintext, token) = SetupStoredToken(DateTime.UtcNow.AddDays(1), null);
        SetupUser();

        await AuthenticateAsync(BearerPrefix + plaintext);

        await _repository.Received(1).UpdateLastUsedAsync(token.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AuthenticateAsync_RecentlyUsedToken_SkipsLastUsedUpdate()
    {
        var recentlyUsed = DateTime.UtcNow - (PatConstants.LastUsedUpdateInterval / 2);
        var (plaintext, _) = SetupStoredToken(DateTime.UtcNow.AddDays(1), recentlyUsed);
        SetupUser();

        await AuthenticateAsync(BearerPrefix + plaintext);

        await _repository.DidNotReceive().UpdateLastUsedAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AuthenticateAsync_StaleLastUsedToken_UpdatesLastUsed()
    {
        var staleUsed = DateTime.UtcNow - PatConstants.LastUsedUpdateInterval - TimeSpan.FromMinutes(1);
        var (plaintext, token) = SetupStoredToken(DateTime.UtcNow.AddDays(1), staleUsed);
        SetupUser();

        await AuthenticateAsync(BearerPrefix + plaintext);

        await _repository.Received(1).UpdateLastUsedAsync(token.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    private async Task<AuthenticateResult> AuthenticateAsync(string? authorizationHeader)
    {
        var optionsMonitor = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        optionsMonitor.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());

        var handler = new PatAuthenticationHandler(
            optionsMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            _repository,
            _userManager);

        var context = new DefaultHttpContext();
        if (authorizationHeader != null)
        {
            context.Request.Headers.Authorization = authorizationHeader;
        }

        var scheme = new AuthenticationScheme(PatConstants.SchemeName, PatConstants.SchemeName, typeof(PatAuthenticationHandler));
        await handler.InitializeAsync(scheme, context);

        return await handler.AuthenticateAsync();
    }

    private (string Plaintext, PersonalAccessToken Token) SetupStoredToken(DateTime? expiresAt, DateTime? lastUsedAt)
    {
        var (plaintext, tokenHash, tokenPrefix) = PatTokenGenerator.Generate();
        var token = new PersonalAccessToken
        {
            Id = Guid.NewGuid(),
            UserId = TestUserId,
            Name = TestTokenName,
            TokenHash = tokenHash,
            TokenPrefix = tokenPrefix,
            ExpiresAt = expiresAt,
            LastUsedAt = lastUsedAt
        };

        _repository.GetByHashAsync(tokenHash, Arg.Any<CancellationToken>()).Returns(token);

        return (plaintext, token);
    }

    private AppUser SetupUser()
    {
        var user = new AppUser
        {
            Id = TestUserId,
            UserName = TestUserName,
            Email = TestUserEmail,
            FirstName = TestFirstName,
            LastName = TestLastName
        };

        _userManager.FindByIdAsync(TestUserId).Returns(user);
        _userManager.GetRolesAsync(user).Returns(new List<string> { AdminRole });

        return user;
    }

    private static UserManager<AppUser> CreateUserManager()
    {
        var userStore = Substitute.For<IUserStore<AppUser>>();
        var identityOptions = Substitute.For<IOptions<IdentityOptions>>();
        var passwordHasher = Substitute.For<IPasswordHasher<AppUser>>();
        var userValidators = new List<IUserValidator<AppUser>>();
        var passwordValidators = new List<IPasswordValidator<AppUser>>();
        var keyNormalizer = Substitute.For<ILookupNormalizer>();
        var errorDescriber = Substitute.For<IdentityErrorDescriber>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var logger = Substitute.For<ILogger<UserManager<AppUser>>>();

        return Substitute.For<UserManager<AppUser>>(
            userStore, identityOptions, passwordHasher, userValidators, passwordValidators,
            keyNormalizer, errorDescriber, serviceProvider, logger);
    }
}
