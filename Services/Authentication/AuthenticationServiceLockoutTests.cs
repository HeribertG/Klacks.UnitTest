// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Authentication;
using Klacks.Api.Application.Validation.Accounts;
using Klacks.Api.Domain.Interfaces.Authentification;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Services.Authentication;

[TestFixture]
internal class AuthenticationServiceLockoutTests
{
    private const string Email = "user@test.com";
    private const string Password = "P@ssw0rt1";
    private const string NewPassword = "N3w-P@ssw0rt";
    private const int PolicyLength = 12;
    private const int LockedOutFailedCount = 5;
    private const int LockoutMinutes = 15;
    private const string ResetToken = "reset-token";

    private UserManager<AppUser> _userManager = null!;
    private IIdentityProviderRepository _identityProviderRepository = null!;
    private ILdapService _ldapService = null!;
    private AuthenticationService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _userManager = CreateUserManager();
        _identityProviderRepository = Substitute.For<IIdentityProviderRepository>();
        _ldapService = Substitute.For<ILdapService>();

        // No identity providers configured, so the LDAP fallback always fails.
        _identityProviderRepository.GetAuthenticationProviders()
            .Returns(new List<IdentityProvider>());

        var jwtValidator = new JwtValidator(new JwtSettings());
        var logger = Substitute.For<ILogger<AuthenticationService>>();

        _sut = new AuthenticationService(
            _userManager,
            jwtValidator,
            _identityProviderRepository,
            _ldapService,
            logger);
    }

    [TearDown]
    public void TearDown()
    {
        _userManager?.Dispose();
    }

    [Test]
    public async Task ValidateCredentials_WhenUserIsLockedOut_IsRejectedWithoutCheckingPassword()
    {
        var user = new AppUser { Id = "1", Email = Email };
        _userManager.FindByEmailAsync(Email).Returns(user);
        _userManager.HasPasswordAsync(user).Returns(true);
        _userManager.IsLockedOutAsync(user).Returns(true);

        var (isValid, resultUser) = await _sut.ValidateCredentialsAsync(Email, Password);

        isValid.ShouldBeFalse();
        resultUser.ShouldBeNull();
        await _userManager.DidNotReceive().CheckPasswordAsync(user, Arg.Any<string>());
        await _userManager.DidNotReceive().AccessFailedAsync(user);
    }

    [Test]
    public async Task ValidateCredentials_WhenPasswordIsWrong_IncrementsFailedAccessCount()
    {
        var user = new AppUser { Id = "2", Email = Email };
        _userManager.FindByEmailAsync(Email).Returns(user);
        _userManager.HasPasswordAsync(user).Returns(true);
        _userManager.IsLockedOutAsync(user).Returns(false);
        _userManager.CheckPasswordAsync(user, Password).Returns(false);
        _userManager.AccessFailedAsync(user).Returns(IdentityResult.Success);

        var (isValid, resultUser) = await _sut.ValidateCredentialsAsync(Email, Password);

        isValid.ShouldBeFalse();
        resultUser.ShouldBeNull();
        await _userManager.Received(1).AccessFailedAsync(user);
        await _userManager.DidNotReceive().ResetAccessFailedCountAsync(user);
    }

    [Test]
    public async Task ValidateCredentials_WhenPasswordIsCorrect_ResetsFailedAccessCount()
    {
        var user = new AppUser { Id = "3", Email = Email };
        _userManager.FindByEmailAsync(Email).Returns(user);
        _userManager.HasPasswordAsync(user).Returns(true);
        _userManager.IsLockedOutAsync(user).Returns(false);
        _userManager.CheckPasswordAsync(user, Password).Returns(true);
        _userManager.ResetAccessFailedCountAsync(user).Returns(IdentityResult.Success);

        var (isValid, resultUser) = await _sut.ValidateCredentialsAsync(Email, Password);

        isValid.ShouldBeTrue();
        resultUser.ShouldBe(user);
        await _userManager.Received(1).ResetAccessFailedCountAsync(user);
        await _userManager.DidNotReceive().AccessFailedAsync(user);
    }

    [Test]
    public async Task ValidateCredentials_WhenUserIsDeactivated_IsRejectedWithoutCheckingPassword()
    {
        var user = new AppUser { Id = "5", Email = Email, DeactivatedAt = DateTime.UtcNow.AddDays(-1) };
        _userManager.FindByEmailAsync(Email).Returns(user);
        _userManager.HasPasswordAsync(user).Returns(true);
        _userManager.IsLockedOutAsync(user).Returns(false);
        _userManager.CheckPasswordAsync(user, Password).Returns(true);

        var (isValid, resultUser) = await _sut.ValidateCredentialsAsync(Email, Password);

        isValid.ShouldBeFalse();
        resultUser.ShouldBeNull();
        await _userManager.DidNotReceive().CheckPasswordAsync(user, Arg.Any<string>());
    }

    [Test]
    public async Task ValidateCredentials_WhenUserIsDeactivated_DoesNotFallBackToLdapForThatAccount()
    {
        var user = new AppUser { Id = "6", Email = Email, DeactivatedAt = DateTime.UtcNow };
        _userManager.FindByEmailAsync(Email).Returns(user);
        _userManager.HasPasswordAsync(user).Returns(false);

        var (isValid, _) = await _sut.ValidateCredentialsAsync(Email, Password);

        isValid.ShouldBeFalse();

        // The refusal happens before the LDAP fallback: a deactivated account must not be able to
        // sign in through a directory provider either.
        await _identityProviderRepository.DidNotReceive().GetAuthenticationProviders();
    }

    [Test]
    public async Task ValidateCredentials_WhenUserIsActive_IsNotAffectedByTheDeactivationCheck()
    {
        var user = new AppUser { Id = "7", Email = Email, DeactivatedAt = null };
        _userManager.FindByEmailAsync(Email).Returns(user);
        _userManager.HasPasswordAsync(user).Returns(true);
        _userManager.IsLockedOutAsync(user).Returns(false);
        _userManager.CheckPasswordAsync(user, Password).Returns(true);
        _userManager.ResetAccessFailedCountAsync(user).Returns(IdentityResult.Success);

        var (isValid, resultUser) = await _sut.ValidateCredentialsAsync(Email, Password);

        isValid.ShouldBeTrue();
        resultUser.ShouldBe(user);
    }

    [Test]
    public async Task ValidateCredentials_WhenLocalUserHasNoPassword_SkipsLockoutAndFallsBackToLdap()
    {
        var user = new AppUser { Id = "4", Email = Email };
        _userManager.FindByEmailAsync(Email).Returns(user);
        _userManager.HasPasswordAsync(user).Returns(false);

        var (isValid, resultUser) = await _sut.ValidateCredentialsAsync(Email, Password);

        isValid.ShouldBeFalse();
        resultUser.ShouldBeNull();
        await _userManager.DidNotReceive().IsLockedOutAsync(user);
        await _userManager.DidNotReceive().AccessFailedAsync(user);
        await _identityProviderRepository.Received(1).GetAuthenticationProviders();
    }

    [Test]
    public async Task ChangePassword_WhenOldPasswordIsWrong_IncrementsFailedAccessCount()
    {
        var user = new AppUser { Id = "10", Email = Email };
        _userManager.ChangePasswordAsync(user, Password, NewPassword)
            .Returns(IdentityResult.Failed(new IdentityErrorDescriber().PasswordMismatch()));
        _userManager.AccessFailedAsync(user).Returns(IdentityResult.Success);

        var (success, _) = await _sut.ChangePasswordAsync(user, Password, NewPassword);

        success.ShouldBeFalse();
        await _userManager.Received(1).AccessFailedAsync(user);
        await _userManager.DidNotReceive().ResetAccessFailedCountAsync(user);
    }

    [Test]
    public async Task ChangePassword_WhenOnlyThePasswordPolicyFails_DoesNotCountAFailedAccess()
    {
        var user = new AppUser { Id = "11", Email = Email };
        _userManager.ChangePasswordAsync(user, Password, NewPassword)
            .Returns(IdentityResult.Failed(new IdentityErrorDescriber().PasswordTooShort(PolicyLength)));

        var (success, _) = await _sut.ChangePasswordAsync(user, Password, NewPassword);

        success.ShouldBeFalse();
        await _userManager.DidNotReceive().AccessFailedAsync(user);
        await _userManager.DidNotReceive().ResetAccessFailedCountAsync(user);
    }

    [Test]
    public async Task ChangePassword_WhenSuccessful_ResetsFailedAccessCount()
    {
        var user = new AppUser { Id = "12", Email = Email };
        _userManager.ChangePasswordAsync(user, Password, NewPassword).Returns(IdentityResult.Success);
        _userManager.ResetAccessFailedCountAsync(user).Returns(IdentityResult.Success);

        var (success, _) = await _sut.ChangePasswordAsync(user, Password, NewPassword);

        success.ShouldBeTrue();
        await _userManager.Received(1).ResetAccessFailedCountAsync(user);
        await _userManager.DidNotReceive().AccessFailedAsync(user);
    }

    [Test]
    public async Task ResetPassword_ClearsTheLockoutAndTheFailedAccessCount_InTheSameUpdate()
    {
        var user = new AppUser
        {
            Id = "13",
            Email = Email,
            AccessFailedCount = LockedOutFailedCount,
            LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(LockoutMinutes)
        };
        int? failedCountAtUpdate = null;
        DateTimeOffset? lockoutEndAtUpdate = DateTimeOffset.MaxValue;
        _userManager.UpdateAsync(user).Returns(call =>
        {
            failedCountAtUpdate = user.AccessFailedCount;
            lockoutEndAtUpdate = user.LockoutEnd;
            return Task.FromResult(IdentityResult.Success);
        });

        var (success, _) = await _sut.ResetPasswordAsync(user, ResetToken, NewPassword);

        success.ShouldBeTrue();
        await _userManager.Received(1).UpdateAsync(user);
        failedCountAtUpdate.ShouldBe(0);
        lockoutEndAtUpdate.ShouldBeNull();
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
