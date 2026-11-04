// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Shouldly;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Accounts;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Security;
using Klacks.Api.Domain.Services.Accounts;
using Klacks.Api.Application.DTOs.Registrations;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Klacks.UnitTest.Services.Accounts;

[TestFixture]
public class AccountPasswordServiceTests
{
    private const string CallerId = "11111111-aaaa-bbbb-cccc-000000000001";
    private const string CallerEmail = "caller@example.com";
    private const string OtherUserId = "22222222-aaaa-bbbb-cccc-000000000002";
    private const string OtherEmail = "colleague@example.com";

    private AccountPasswordService _passwordService;
    private IAuthenticationService _mockAuthService;
    private IUserManagementService _mockUserManagementService;
    private IAccountNotificationService _mockNotificationService;
    private IAccountAuthenticationService _mockAccountAuthenticationService;
    private IRefreshTokenService _mockRefreshTokenService;
    private IPersonalAccessTokenRepository _mockPersonalAccessTokenRepository;
    private ILogger<AccountPasswordService> _mockLogger;

    [SetUp]
    public void SetUp()
    {
        _mockAuthService = Substitute.For<IAuthenticationService>();
        _mockUserManagementService = Substitute.For<IUserManagementService>();
        _mockNotificationService = Substitute.For<IAccountNotificationService>();
        _mockAccountAuthenticationService = Substitute.For<IAccountAuthenticationService>();
        _mockRefreshTokenService = Substitute.For<IRefreshTokenService>();
        _mockPersonalAccessTokenRepository = Substitute.For<IPersonalAccessTokenRepository>();
        _mockLogger = Substitute.For<ILogger<AccountPasswordService>>();

        var settings = Options.Create(new PasswordResetSettings { BaseUrl = "https://localhost:7002" });

        _passwordService = new AccountPasswordService(
            _mockAuthService,
            _mockUserManagementService,
            _mockNotificationService,
            _mockAccountAuthenticationService,
            _mockRefreshTokenService,
            _mockPersonalAccessTokenRepository,
            settings,
            _mockLogger);
    }

    [Test]
    public async Task ChangePasswordAsync_ResolvesTheAccountByCallerId_AndIgnoresTheBodyEmail()
    {
        var caller = CreateUser(CallerId, CallerEmail);
        var colleague = CreateUser(OtherUserId, OtherEmail);
        var model = CreateChangeModel(OtherEmail);
        _mockUserManagementService.FindUserByIdAsync(CallerId).Returns(caller);
        _mockUserManagementService.FindUserByEmailAsync(OtherEmail).Returns(colleague);
        _mockAuthService.ChangePasswordAsync(caller, model.OldPassword, model.Password).Returns((true, null));
        _mockAccountAuthenticationService.GenerateAuthenticationAsync(caller).Returns(new AuthenticatedResult { Success = true });

        var result = await _passwordService.ChangePasswordAsync(CallerId, model);

        result.Success.ShouldBeTrue();
        result.Email.ShouldBe(CallerEmail);
        await _mockAuthService.Received(1).ChangePasswordAsync(caller, model.OldPassword, model.Password);
        await _mockAuthService.DidNotReceive().ChangePasswordAsync(colleague, Arg.Any<string>(), Arg.Any<string>());
        await _mockUserManagementService.DidNotReceive().FindUserByEmailAsync(Arg.Any<string>());
        await _mockRefreshTokenService.DidNotReceive().RemoveAllUserRefreshTokensAsync(OtherUserId);
    }

    [Test]
    public async Task ChangePasswordAsync_WithUnknownCallerId_FailsWithoutRevealingThatTheAccountIsMissing()
    {
        var model = CreateChangeModel(OtherEmail);
        _mockUserManagementService.FindUserByIdAsync(CallerId).Returns((AppUser?)null);
        string? errorKey = null;
        string? errorMessage = null;
        _mockAuthService
            .When(x => x.SetModelError(Arg.Any<AuthenticatedResult>(), Arg.Any<string>(), Arg.Any<string>()))
            .Do(call =>
            {
                errorKey = call.ArgAt<string>(1);
                errorMessage = call.ArgAt<string>(2);
            });

        var result = await _passwordService.ChangePasswordAsync(CallerId, model);

        result.Success.ShouldBeFalse();
        errorKey.ShouldNotBeNull();
        errorKey!.ShouldNotContain("does not exist");
        errorMessage.ShouldNotBeNull();
        errorMessage!.ShouldNotContain("no user");
        await _mockAuthService.DidNotReceive().ChangePasswordAsync(Arg.Any<AppUser>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task ChangePasswordAsync_WhenAccountIsBlocked_IsRejectedBeforeTheOldPasswordIsChecked()
    {
        var caller = CreateUser(CallerId, CallerEmail);
        var model = CreateChangeModel(CallerEmail);
        _mockUserManagementService.FindUserByIdAsync(CallerId).Returns(caller);
        _mockUserManagementService.IsAccountBlockedAsync(caller).Returns(true);

        var result = await _passwordService.ChangePasswordAsync(CallerId, model);

        result.Success.ShouldBeFalse();
        await _mockAuthService.DidNotReceive().ChangePasswordAsync(Arg.Any<AppUser>(), Arg.Any<string>(), Arg.Any<string>());
        await _mockRefreshTokenService.DidNotReceive().RemoveAllUserRefreshTokensAsync(Arg.Any<string>());
    }

    [Test]
    public async Task ChangePasswordAsync_OnSuccess_RevokesAllRefreshTokensBeforeIssuingTheFreshPair()
    {
        var caller = CreateUser(CallerId, CallerEmail);
        var model = CreateChangeModel(CallerEmail);
        var renewed = new AuthenticatedResult { Success = true, Token = "access", RefreshToken = "refresh" };
        _mockUserManagementService.FindUserByIdAsync(CallerId).Returns(caller);
        _mockAuthService.ChangePasswordAsync(caller, model.OldPassword, model.Password).Returns((true, null));
        _mockAccountAuthenticationService.GenerateAuthenticationAsync(caller).Returns(renewed);

        var result = await _passwordService.ChangePasswordAsync(CallerId, model);

        result.ShouldBeSameAs(renewed);
        result.RefreshToken.ShouldBe("refresh");
        Received.InOrder(() =>
        {
            _mockAuthService.ChangePasswordAsync(caller, model.OldPassword, model.Password);
            _mockRefreshTokenService.RemoveAllUserRefreshTokensAsync(CallerId);
            _mockAccountAuthenticationService.GenerateAuthenticationAsync(caller);
        });
    }

    [Test]
    public async Task ChangePasswordAsync_OnSuccess_KeepsThePersonalAccessTokens()
    {
        var caller = CreateUser(CallerId, CallerEmail);
        var model = CreateChangeModel(CallerEmail);
        _mockUserManagementService.FindUserByIdAsync(CallerId).Returns(caller);
        _mockAuthService.ChangePasswordAsync(caller, model.OldPassword, model.Password).Returns((true, null));
        _mockAccountAuthenticationService.GenerateAuthenticationAsync(caller).Returns(new AuthenticatedResult { Success = true });

        await _passwordService.ChangePasswordAsync(CallerId, model);

        await _mockRefreshTokenService.Received(1).RemoveAllUserRefreshTokensAsync(CallerId);
        await _mockPersonalAccessTokenRepository.DidNotReceive().RevokeAllByUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ChangePasswordAsync_WithWrongOldPassword_RevokesNothingAndIssuesNoTokens()
    {
        var caller = CreateUser(CallerId, CallerEmail);
        var model = CreateChangeModel(CallerEmail);
        _mockUserManagementService.FindUserByIdAsync(CallerId).Returns(caller);
        _mockAuthService.ChangePasswordAsync(caller, model.OldPassword, model.Password)
            .Returns((false, IdentityResult.Failed(new IdentityErrorDescriber().PasswordMismatch())));

        var result = await _passwordService.ChangePasswordAsync(CallerId, model);

        result.Success.ShouldBeFalse();
        await _mockRefreshTokenService.DidNotReceive().RemoveAllUserRefreshTokensAsync(Arg.Any<string>());
        await _mockAccountAuthenticationService.DidNotReceive().GenerateAuthenticationAsync(Arg.Any<AppUser>(), Arg.Any<bool>(), Arg.Any<string?>());
    }

    [Test]
    public async Task ChangePasswordAsync_WithUnexpectedFailure_ShouldReturnFailure()
    {
        var caller = CreateUser(CallerId, CallerEmail);
        var model = CreateChangeModel(CallerEmail);
        _mockUserManagementService.FindUserByIdAsync(CallerId).Returns(caller);
        _mockAuthService.ChangePasswordAsync(caller, model.OldPassword, model.Password).Returns((false, null));

        var result = await _passwordService.ChangePasswordAsync(CallerId, model);

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task GeneratePasswordResetTokenAsync_WithExistingEmail_ShouldReturnTrue()
    {
        var email = "test@example.com";
        var testUser = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = email,
            UserName = email
        };

        string? capturedMessage = null;
        _mockUserManagementService.FindUserByEmailAsync(email).Returns(testUser);
        _mockUserManagementService.UpdateUserAsync(Arg.Any<AppUser>()).Returns((true, null));
        _mockNotificationService.SendEmailAsync(Arg.Any<string>(), email, Arg.Do<string>(m => capturedMessage = m)).Returns("true");

        var result = await _passwordService.GeneratePasswordResetTokenAsync(email);

        result.ShouldBeTrue();
        await _mockNotificationService.Received(1).SendEmailAsync(Arg.Any<string>(), email, Arg.Any<string>());

        capturedMessage.ShouldNotBeNull();
        var rawToken = ExtractTokenFromMessage(capturedMessage!);
        testUser.PasswordResetToken.ShouldBe(PasswordResetTokenHasher.Hash(rawToken));
        testUser.PasswordResetToken.ShouldNotBe(rawToken);
    }

    private static string ExtractTokenFromMessage(string message)
    {
        const string marker = "token=";
        var start = message.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = message.IndexOf('"', start);
        return message.Substring(start, end - start);
    }

    [Test]
    public async Task GeneratePasswordResetTokenAsync_WithNonExistentEmail_ShouldReturnFalse()
    {
        var email = "nonexistent@example.com";

        _mockUserManagementService.FindUserByEmailAsync(email).Returns((AppUser?)null);

        var result = await _passwordService.GeneratePasswordResetTokenAsync(email);

        result.ShouldBeFalse();
        await _mockNotificationService.DidNotReceive().SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task ValidatePasswordResetTokenAsync_WithValidToken_ShouldReturnTrue()
    {
        var token = "validtoken";
        var testUser = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "test@example.com",
            PasswordResetToken = PasswordResetTokenHasher.Hash(token),
            PasswordResetTokenExpires = DateTime.UtcNow.AddHours(1)
        };

        _mockUserManagementService.FindUserByTokenAsync(token).Returns(testUser);

        var result = await _passwordService.ValidatePasswordResetTokenAsync(token);

        result.ShouldBeTrue();
    }

    [Test]
    public async Task ValidatePasswordResetTokenAsync_WithExpiredToken_ShouldReturnFalse()
    {
        var token = "expiredtoken";
        var testUser = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "test@example.com",
            PasswordResetToken = PasswordResetTokenHasher.Hash(token),
            PasswordResetTokenExpires = DateTime.UtcNow.AddHours(-1)
        };

        _mockUserManagementService.FindUserByTokenAsync(token).Returns(testUser);

        var result = await _passwordService.ValidatePasswordResetTokenAsync(token);

        result.ShouldBeFalse();
    }

    [Test]
    public async Task ResetPasswordAsync_WithValidToken_ShouldReturnSuccess()
    {
        var token = "validtoken";
        var newPassword = "NewPassword123!";
        var resetPasswordResource = new ResetPasswordResource
        {
            Token = token,
            Password = newPassword
        };

        var testUser = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "test@example.com",
            PasswordResetToken = PasswordResetTokenHasher.Hash(token),
            PasswordResetTokenExpires = DateTime.UtcNow.AddHours(1)
        };

        _mockUserManagementService.FindUserByTokenAsync(token).Returns(testUser);
        _mockAuthService.ResetPasswordAsync(testUser, token, newPassword).Returns((true, null));
        _mockUserManagementService.UpdateUserAsync(Arg.Any<AppUser>()).Returns((true, null));

        var result = await _passwordService.ResetPasswordAsync(resetPasswordResource);

        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
    }

    [Test]
    public async Task ResetPasswordAsync_OnSuccess_RevokesAllRefreshTokensAndAllPersonalAccessTokens()
    {
        var token = "validtoken";
        var resetPasswordResource = new ResetPasswordResource { Token = token, Password = "NewPassword123!" };
        var testUser = new AppUser
        {
            Id = CallerId,
            Email = CallerEmail,
            PasswordResetToken = PasswordResetTokenHasher.Hash(token),
            PasswordResetTokenExpires = DateTime.UtcNow.AddHours(1)
        };
        _mockUserManagementService.FindUserByTokenAsync(token).Returns(testUser);
        _mockAuthService.ResetPasswordAsync(testUser, token, resetPasswordResource.Password).Returns((true, null));
        _mockUserManagementService.UpdateUserAsync(testUser).Returns((true, null));

        var result = await _passwordService.ResetPasswordAsync(resetPasswordResource);

        result.Success.ShouldBeTrue();
        await _mockRefreshTokenService.Received(1).RemoveAllUserRefreshTokensAsync(CallerId);
        await _mockPersonalAccessTokenRepository.Received(1).RevokeAllByUserAsync(CallerId, CancellationToken.None);
    }

    [Test]
    public async Task ResetPasswordAsync_WhenTheResetFails_RevokesNothing()
    {
        var token = "validtoken";
        var resetPasswordResource = new ResetPasswordResource { Token = token, Password = "NewPassword123!" };
        var testUser = new AppUser
        {
            Id = CallerId,
            Email = CallerEmail,
            PasswordResetToken = PasswordResetTokenHasher.Hash(token),
            PasswordResetTokenExpires = DateTime.UtcNow.AddHours(1)
        };
        _mockUserManagementService.FindUserByTokenAsync(token).Returns(testUser);
        _mockAuthService.ResetPasswordAsync(testUser, token, resetPasswordResource.Password).Returns((false, null));

        var result = await _passwordService.ResetPasswordAsync(resetPasswordResource);

        result.Success.ShouldBeFalse();
        await _mockRefreshTokenService.DidNotReceive().RemoveAllUserRefreshTokensAsync(Arg.Any<string>());
        await _mockPersonalAccessTokenRepository.DidNotReceive().RevokeAllByUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static AppUser CreateUser(string id, string email) => new()
    {
        Id = id,
        Email = email,
        UserName = email,
        FirstName = "John",
        LastName = "Doe"
    };

    private static ChangePasswordResource CreateChangeModel(string bodyEmail) => new()
    {
        Email = bodyEmail,
        OldPassword = "OldPassword123!",
        Password = "NewPassword123!"
    };
}
