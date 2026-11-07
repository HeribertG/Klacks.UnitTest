// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Text.Json;
using Klacks.Api.Application.Commands.OAuth;
using Klacks.Api.Application.Handlers.OAuth;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

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
    private const string SeedAdminEmail = "admin@test.com";
    private const string SeedAdminPassword = "seed-secret";
    private const string CodeChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    private static readonly DateTimeOffset FixedUtcNow = DateTimeOffset.Parse(
        "2026-07-17T10:00:00Z",
        CultureInfo.InvariantCulture,
        DateTimeStyles.AdjustToUniversal);

    private IOAuthClientRepository _clientRepository = null!;
    private IAuthenticationService _authenticationService = null!;
    private OAuthAuthorizationCodeStore _codeStore = null!;
    private DataBaseContext _context = null!;
    private IAdminSetupGateService _adminSetupGateService = null!;
    private IssueOAuthAuthorizationCodeCommandHandler _handler = null!;
    private IUserManagementService _userManagementService = null!;

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
        _authenticationService.ValidateCredentialsAsync(SeedAdminEmail, SeedAdminPassword)
            .Returns((true, new AppUser { Id = SystemAccounts.SeedAdminUserId, Email = SeedAdminEmail }));

        _adminSetupGateService = Substitute.For<IAdminSetupGateService>();
        _adminSetupGateService.IsGateActiveAsync().Returns(true);

        _userManagementService = Substitute.For<IUserManagementService>();
        _userManagementService.GetUserRolesAsync(Arg.Any<AppUser>()).Returns(new List<string> { Roles.Authorised });

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(FixedUtcNow);

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _codeStore = new OAuthAuthorizationCodeStore(_context, timeProvider);
        _handler = new IssueOAuthAuthorizationCodeCommandHandler(
            _clientRepository, _authenticationService, _codeStore, _adminSetupGateService, _userManagementService);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task Handle_PlannerAccountWithoutRole_RefusesWithAccessDeniedWithoutStoringCode()
    {
        _userManagementService.GetUserRolesAsync(Arg.Any<AppUser>()).Returns(new List<string>());
        var command = new IssueOAuthAuthorizationCodeCommand(Email, Password, KnownClientId, RegisteredRedirectUri, CodeChallenge, OAuthConstants.McpToolsScope);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Code.ShouldBeNull();
        result.Error.ShouldBe(OAuthConstants.ErrorAccessDenied);
        (await _context.OAuthAuthorizationCodes.CountAsync()).ShouldBe(0);
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

        var stored = await _codeStore.ConsumeAsync(result.Code!);
        stored.ShouldNotBeNull();
        stored!.UserId.ShouldBe(UserId);
        stored.ClientId.ShouldBe(KnownClientId);
        stored.ClientName.ShouldBe(ClientName);
        stored.RedirectUri.ShouldBe(RegisteredRedirectUri);
        stored.CodeChallenge.ShouldBe(CodeChallenge);
        stored.Scope.ShouldBe(OAuthConstants.McpToolsScope);
    }

    [Test]
    public async Task Handle_SeedAdminWhileSetupGateActive_RefusesWithoutStoringCode()
    {
        var command = new IssueOAuthAuthorizationCodeCommand(SeedAdminEmail, SeedAdminPassword, KnownClientId, RegisteredRedirectUri, CodeChallenge, OAuthConstants.McpToolsScope);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Code.ShouldBeNull();
        result.IsSetupRequired.ShouldBeTrue();
        result.ErrorDescription.ShouldBe(OAuthConstants.SetupRequiredErrorDescription);
        (await _context.OAuthAuthorizationCodes.CountAsync()).ShouldBe(0);
    }

    [Test]
    public async Task Handle_SeedAdminAfterSetup_IssuesCode()
    {
        _adminSetupGateService.IsGateActiveAsync().Returns(false);
        var command = new IssueOAuthAuthorizationCodeCommand(SeedAdminEmail, SeedAdminPassword, KnownClientId, RegisteredRedirectUri, CodeChallenge, OAuthConstants.McpToolsScope);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSetupRequired.ShouldBeFalse();
        result.Code.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task Handle_NormalUserWhileSetupGateActive_IssuesCodeWithoutConsultingGate()
    {
        var command = new IssueOAuthAuthorizationCodeCommand(Email, Password, KnownClientId, RegisteredRedirectUri, CodeChallenge, OAuthConstants.McpToolsScope);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Code.ShouldNotBeNullOrWhiteSpace();
        await _adminSetupGateService.DidNotReceive().IsGateActiveAsync();
    }

    [Test]
    public async Task Handle_InvalidCredentials_DoesNotReportSetupRequired()
    {
        var command = new IssueOAuthAuthorizationCodeCommand(SeedAdminEmail, WrongPassword, KnownClientId, RegisteredRedirectUri, CodeChallenge, null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSetupRequired.ShouldBeFalse();
        result.Error.ShouldBe(OAuthConstants.ErrorAccessDenied);
    }
}
