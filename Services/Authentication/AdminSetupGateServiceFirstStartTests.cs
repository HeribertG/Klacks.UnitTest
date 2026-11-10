// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins when the anonymous first start may claim an installation: only outside Development and the Playground, while
/// the seeded admin is active, still holds the Admin role with its seeded password hash, and is the only active
/// administrator. Every other state answers false, so the anonymous path never opens more than signing in with the
/// public default password would.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Services.Authentication;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Klacks.UnitTest.Services.Authentication;

[TestFixture]
public class AdminSetupGateServiceFirstStartTests
{
    private const int SeedAdminOnly = 1;
    private const string PublicDefaultPassword = "P@ssw0rt1";
    private const string SeedEmail = "admin@test.com";
    private const int SeedAdminAndOneMore = 2;

    private IUserManagementService _userManagement = null!;
    private IHostEnvironment _environment = null!;
    private Dictionary<string, string?> _configuration = null!;

    [SetUp]
    public void SetUp()
    {
        _userManagement = Substitute.For<IUserManagementService>();
        _environment = Substitute.For<IHostEnvironment>();
        _environment.EnvironmentName.Returns(Environments.Production);
        _configuration = new Dictionary<string, string?>();
    }

    private AdminSetupGateService CreateSut() =>
        new(_userManagement, new ConfigurationBuilder().AddInMemoryCollection(_configuration).Build(), _environment);

    private AppUser SeedAdmin(string? passwordHash = SystemAccounts.SeedAdminInitialPasswordHash, DateTime? deactivatedAt = null)
    {
        var seed = new AppUser
        {
            Id = SystemAccounts.SeedAdminUserId,
            PasswordHash = passwordHash,
            DeactivatedAt = deactivatedAt,
        };
        _userManagement.FindUserByIdNoTrackingAsync(SystemAccounts.SeedAdminUserId).Returns(seed);
        _userManagement.GetUserRolesAsync(seed).Returns(new List<string> { Roles.Admin });
        _userManagement.CountActiveAdministratorsAsync().Returns(SeedAdminOnly);
        return seed;
    }

    [Test]
    public void SeededHash_BelongsToThePublicDefaultPasswordAndIsRehashedOnTheFirstSignIn()
    {
        var result = new PasswordHasher<AppUser>().VerifyHashedPassword(
            new AppUser(), SystemAccounts.SeedAdminInitialPasswordHash, PublicDefaultPassword);

        result.ShouldBe(PasswordVerificationResult.SuccessRehashNeeded);
    }

    [TestCase(true, null, true)]
    [TestCase(false, null, false)]
    [TestCase(false, "", false)]
    [TestCase(false, "ABCD-EFGH-JK23", true)]
    public void FirstStart_IsReachableLocallyOrWithAConfiguredCode(bool isLocalRequest, string? configuredCode, bool expected)
    {
        _configuration[DeploymentConstants.FirstStartCodeConfigKey] = configuredCode;

        CreateSut().IsFirstStartReachable(isLocalRequest).ShouldBe(expected);
    }

    [TestCase(true, null, null, true)]
    [TestCase(false, null, "ABCD-EFGH-JK23", false)]
    [TestCase(false, "ABCD-EFGH-JK23", null, false)]
    [TestCase(false, "ABCD-EFGH-JK23", "abcd efgh jk23", true)]
    [TestCase(false, "ABCD-EFGH-JK23", "ABCD-EFGH-JK24", false)]
    public void SetupCode_IsNeededOnlyForRemoteRequests(bool isLocalRequest, string? configuredCode, string? typedCode, bool expected)
    {
        _configuration[DeploymentConstants.FirstStartCodeConfigKey] = configuredCode;

        CreateSut().IsFirstStartCodeAccepted(isLocalRequest, typedCode).ShouldBe(expected);
    }

    [Test]
    public async Task SeedSignIn_RemoteWithConfiguredCodeWhileOpen_IsReservedForTheSetupCode()
    {
        var seed = SeedAdmin();
        _userManagement.FindUserByEmailAsync(SeedEmail).Returns(seed);
        _configuration[DeploymentConstants.FirstStartCodeConfigKey] = "ABCD-EFGH-JK23";

        (await CreateSut().IsSeedAdminSignInReservedForSetupCodeAsync(SeedEmail, isLocalRequest: false)).ShouldBeTrue();
    }

    [Test]
    public async Task SeedSignIn_LocalOrWithoutCode_IsNotReserved()
    {
        var seed = SeedAdmin();
        _userManagement.FindUserByEmailAsync(SeedEmail).Returns(seed);

        (await CreateSut().IsSeedAdminSignInReservedForSetupCodeAsync(SeedEmail, isLocalRequest: false)).ShouldBeFalse();

        _configuration[DeploymentConstants.FirstStartCodeConfigKey] = "ABCD-EFGH-JK23";
        (await CreateSut().IsSeedAdminSignInReservedForSetupCodeAsync(SeedEmail, isLocalRequest: true)).ShouldBeFalse();
    }

    [Test]
    public async Task SeedSignIn_AfterThePasswordChanged_IsNotReserved()
    {
        var seed = SeedAdmin(passwordHash: "AQAAAAIAAYagAAAAEOtherHashAfterChangeOrRehash==");
        _userManagement.FindUserByEmailAsync(SeedEmail).Returns(seed);
        _configuration[DeploymentConstants.FirstStartCodeConfigKey] = "ABCD-EFGH-JK23";

        (await CreateSut().IsSeedAdminSignInReservedForSetupCodeAsync(SeedEmail, isLocalRequest: false)).ShouldBeFalse();
    }

    [Test]
    public async Task OtherAccountSignIn_IsNeverReserved()
    {
        SeedAdmin();
        _userManagement.FindUserByEmailAsync("owner@example.com").Returns(new AppUser { Id = Guid.NewGuid().ToString() });
        _configuration[DeploymentConstants.FirstStartCodeConfigKey] = "ABCD-EFGH-JK23";

        (await CreateSut().IsSeedAdminSignInReservedForSetupCodeAsync("owner@example.com", isLocalRequest: false)).ShouldBeFalse();
    }

    [Test]
    public async Task FreshInstallation_IsOpen()
    {
        SeedAdmin();

        (await CreateSut().IsFirstStartSetupOpenAsync()).ShouldBeTrue();
    }

    [Test]
    public async Task Playground_IsClosedWithoutReadingTheDatabase()
    {
        SeedAdmin();
        _configuration[DeploymentConstants.IsPlaygroundConfigKey] = "true";

        (await CreateSut().IsFirstStartSetupOpenAsync()).ShouldBeFalse();
        await _userManagement.DidNotReceive().FindUserByIdNoTrackingAsync(Arg.Any<string>());
    }

    [Test]
    public async Task Development_IsClosed()
    {
        SeedAdmin();
        _environment.EnvironmentName.Returns(Environments.Development);

        (await CreateSut().IsFirstStartSetupOpenAsync()).ShouldBeFalse();
    }

    [Test]
    public async Task SeedAdminMissing_IsClosed()
    {
        _userManagement.FindUserByIdNoTrackingAsync(SystemAccounts.SeedAdminUserId).Returns((AppUser?)null);

        (await CreateSut().IsFirstStartSetupOpenAsync()).ShouldBeFalse();
    }

    [Test]
    public async Task SeedAdminDeactivated_IsClosed()
    {
        SeedAdmin(deactivatedAt: DateTime.UtcNow);

        (await CreateSut().IsFirstStartSetupOpenAsync()).ShouldBeFalse();
    }

    [Test]
    public async Task SeedPasswordChangedOrRehashedBySignIn_IsClosed()
    {
        SeedAdmin(passwordHash: "AQAAAAIAAYagAAAAEOtherHashAfterChangeOrRehash==");

        (await CreateSut().IsFirstStartSetupOpenAsync()).ShouldBeFalse();
    }

    [Test]
    public async Task SeedAdminWithoutAdminRole_IsClosed()
    {
        var seed = SeedAdmin();
        _userManagement.GetUserRolesAsync(seed).Returns(new List<string>());

        (await CreateSut().IsFirstStartSetupOpenAsync()).ShouldBeFalse();
    }

    [Test]
    public async Task AnotherActiveAdministratorExists_IsClosed()
    {
        SeedAdmin();
        _userManagement.CountActiveAdministratorsAsync().Returns(SeedAdminAndOneMore);

        (await CreateSut().IsFirstStartSetupOpenAsync()).ShouldBeFalse();
    }
}
