// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the token policy of the account services: a password-reset token is valid for 24 hours and is 64 random
/// bytes long (86 URL-safe characters), a refresh token is 32 random bytes (44 base64 characters).
/// </summary>

using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Accounts;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Services.Accounts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Services.Accounts;

[TestFixture]
public class ResetAndRefreshTokenPolicyTests
{
    private const int ResetTokenCharacters = 86;
    private const int RefreshTokenCharacters = 44;
    private const int ResetValidityHours = 24;
    private const int ToleranceMinutes = 2;

    [Test]
    public async Task PasswordResetToken_IsValidForTwentyFourHoursAndSixtyFourBytesLong()
    {
        var email = "test@example.com";
        var user = new AppUser { Id = Guid.NewGuid().ToString(), Email = email, UserName = email };
        string? message = null;
        var userManagement = Substitute.For<IUserManagementService>();
        userManagement.FindUserByEmailAsync(email).Returns(user);
        userManagement.UpdateUserAsync(Arg.Any<AppUser>()).Returns((true, null));
        var notification = Substitute.For<IAccountNotificationService>();
        notification.SendEmailAsync(Arg.Any<string>(), email, Arg.Do<string>(m => message = m)).Returns("true");
        var service = new AccountPasswordService(
            Substitute.For<IAuthenticationService>(),
            userManagement,
            notification,
            Options.Create(new PasswordResetSettings { BaseUrl = "https://localhost:7002" }),
            Substitute.For<ILogger<AccountPasswordService>>());

        var before = DateTime.UtcNow;
        var result = await service.GeneratePasswordResetTokenAsync(email);

        result.ShouldBeTrue();
        user.PasswordResetTokenExpires.ShouldNotBeNull();
        var expectedExpiry = before.AddHours(ResetValidityHours);
        user.PasswordResetTokenExpires!.Value.ShouldBe(expectedExpiry, TimeSpan.FromMinutes(ToleranceMinutes));
        message.ShouldNotBeNull();
        message!.ShouldContain($"valid for {ResetValidityHours} hours");
        ExtractToken(message).Length.ShouldBe(ResetTokenCharacters);
    }

    [Test]
    public void RefreshToken_IsThirtyTwoRandomBytes()
    {
        var token = new RefreshTokenGenerator().GenerateRefreshToken();

        token.Length.ShouldBe(RefreshTokenCharacters);
        Convert.FromBase64String(token).Length.ShouldBe(32);
    }

    private static string ExtractToken(string message)
    {
        const string marker = "token=";
        var start = message.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = message.IndexOf('"', start);
        return message.Substring(start, end - start);
    }
}
