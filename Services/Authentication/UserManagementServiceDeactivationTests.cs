// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the switch behind the deactivation gate: deactivating stamps both audit fields, reactivating
/// clears them again, and the shared "may this account hold a session" question answers true for a
/// deactivated as well as for a locked-out account.
/// </summary>

using Klacks.Api.Application.Services.Authentication;
using Klacks.Api.Domain.Models.Authentification;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Services.Authentication;

[TestFixture]
public class UserManagementServiceDeactivationTests
{
    private static readonly Guid TargetId = Guid.NewGuid();
    private const string ActingAdminId = "0f9d3f1a-2b4c-4d5e-8a91-1122334455aa";

    private UserManager<AppUser> _userManager = null!;
    private UserManagementService _sut = null!;
    private AppUser _target = null!;

    [SetUp]
    public void SetUp()
    {
        _userManager = CreateUserManager();
        _target = new AppUser { Id = TargetId.ToString(), UserName = "target", Email = "target@test.com" };

        _userManager.FindByIdAsync(TargetId.ToString()).Returns(_target);
        _userManager.UpdateAsync(_target).Returns(IdentityResult.Success);
        _userManager.IsLockedOutAsync(_target).Returns(false);

        _sut = new UserManagementService(
            _userManager,
            Substitute.For<ILogger<UserManagementService>>());
    }

    [TearDown]
    public void TearDown() => _userManager.Dispose();

    [Test]
    public async Task Deactivate_SetsBothFieldsAndPersistsThem()
    {
        var before = DateTime.UtcNow;

        var (success, _) = await _sut.DeactivateUserAsync(TargetId, ActingAdminId);

        success.ShouldBeTrue();
        _target.DeactivatedAt.ShouldNotBeNull();
        _target.DeactivatedAt!.Value.ShouldBeGreaterThanOrEqualTo(before);
        _target.DeactivatedBy.ShouldBe(ActingAdminId);
        await _userManager.Received(1).UpdateAsync(_target);
    }

    [Test]
    public async Task Deactivate_DoesNotDeleteTheAccount()
    {
        await _sut.DeactivateUserAsync(TargetId, ActingAdminId);

        await _userManager.DidNotReceive().DeleteAsync(Arg.Any<AppUser>());
    }

    [Test]
    public async Task Deactivate_UnknownUser_Fails()
    {
        _userManager.FindByIdAsync(TargetId.ToString()).Returns((AppUser?)null);

        var (success, message) = await _sut.DeactivateUserAsync(TargetId, ActingAdminId);

        success.ShouldBeFalse();
        message.ShouldContain("not found");
        await _userManager.DidNotReceive().UpdateAsync(Arg.Any<AppUser>());
    }

    [Test]
    public async Task Deactivate_AlreadyDeactivatedUser_DoesNotOverwriteTheOriginalStamp()
    {
        var original = DateTime.UtcNow.AddDays(-3);
        _target.DeactivatedAt = original;
        _target.DeactivatedBy = "someone-else";

        var (success, _) = await _sut.DeactivateUserAsync(TargetId, ActingAdminId);

        success.ShouldBeFalse();
        _target.DeactivatedAt.ShouldBe(original);
        _target.DeactivatedBy.ShouldBe("someone-else");
    }

    [Test]
    public async Task Reactivate_ClearsBothFields()
    {
        _target.DeactivatedAt = DateTime.UtcNow.AddDays(-1);
        _target.DeactivatedBy = ActingAdminId;

        var (success, _) = await _sut.ReactivateUserAsync(TargetId);

        success.ShouldBeTrue();
        _target.DeactivatedAt.ShouldBeNull();
        _target.DeactivatedBy.ShouldBeNull();
        await _userManager.Received(1).UpdateAsync(_target);
    }

    [Test]
    public async Task Reactivate_ActiveUser_Fails()
    {
        _target.DeactivatedAt = null;

        var (success, message) = await _sut.ReactivateUserAsync(TargetId);

        success.ShouldBeFalse();
        message.ShouldContain("not deactivated");
        await _userManager.DidNotReceive().UpdateAsync(Arg.Any<AppUser>());
    }

    [Test]
    public async Task IsAccountBlocked_IsTrueForADeactivatedAccount()
    {
        _target.DeactivatedAt = DateTime.UtcNow;

        (await _sut.IsAccountBlockedAsync(_target)).ShouldBeTrue();
    }

    [Test]
    public async Task IsAccountBlocked_IsTrueForALockedOutAccount()
    {
        _userManager.IsLockedOutAsync(_target).Returns(true);

        (await _sut.IsAccountBlockedAsync(_target)).ShouldBeTrue();
    }

    [Test]
    public async Task IsAccountBlocked_IsFalseForAnActiveAccount()
    {
        (await _sut.IsAccountBlockedAsync(_target)).ShouldBeFalse();
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
