// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Every refusal of RegisterUserAsync carries Identity's own error code, so the caller can tell the user which rule
/// failed. Before, a duplicate e-mail and blank fields came back as (false, null) and surfaced as a bare 409.
/// </summary>

using Klacks.Api.Application.Services.Authentication;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Services.Authentication;

[TestFixture]
public class UserManagementServiceRegistrationTests
{
    private const string Password = "password1!";

    private UserManager<AppUser> _userManager = null!;
    private UserManagementService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _userManager = Substitute.For<UserManager<AppUser>>(
            Substitute.For<IUserStore<AppUser>>(),
            Substitute.For<IOptions<IdentityOptions>>(),
            Substitute.For<IPasswordHasher<AppUser>>(),
            new List<IUserValidator<AppUser>>(),
            new List<IPasswordValidator<AppUser>>(),
            Substitute.For<ILookupNormalizer>(),
            new IdentityErrorDescriber(),
            Substitute.For<IServiceProvider>(),
            Substitute.For<ILogger<UserManager<AppUser>>>());

        _sut = new UserManagementService(
            _userManager, Substitute.For<IAppUserReader>(), Substitute.For<ILogger<UserManagementService>>());
    }

    [TearDown]
    public void TearDown() => _userManager.Dispose();

    [Test]
    public async Task RegisterUserAsync_EmailAlreadyInUse_RefusesWithDuplicateEmail()
    {
        _userManager.FindByEmailAsync("taken@example.com").Returns(new AppUser { Email = "taken@example.com" });

        var (success, result) = await _sut.RegisterUserAsync(
            new AppUser { Email = "taken@example.com", UserName = "taken" }, Password);

        success.ShouldBeFalse();
        result.ShouldNotBeNull();
        result.Errors.Select(error => error.Code).ShouldBe([AccountRegistrationErrorCodes.DuplicateEmail]);
        await _userManager.DidNotReceive().CreateAsync(Arg.Any<AppUser>(), Arg.Any<string>());
    }

    [Test]
    public async Task RegisterUserAsync_BlankEmail_RefusesWithInvalidEmail()
    {
        var (success, result) = await _sut.RegisterUserAsync(new AppUser { Email = " ", UserName = "user" }, Password);

        success.ShouldBeFalse();
        result!.Errors.Select(error => error.Code).ShouldBe([AccountRegistrationErrorCodes.InvalidEmail]);
    }

    [Test]
    public async Task RegisterUserAsync_BlankUserName_RefusesWithInvalidUserName()
    {
        var (success, result) = await _sut.RegisterUserAsync(
            new AppUser { Email = "user@example.com", UserName = "" }, Password);

        success.ShouldBeFalse();
        result!.Errors.Select(error => error.Code).ShouldBe([AccountRegistrationErrorCodes.InvalidUserName]);
    }

    [Test]
    public async Task RegisterUserAsync_IdentityRefusal_IsPassedThroughUnchanged()
    {
        var refusal = IdentityResult.Failed(new IdentityErrorDescriber().PasswordRequiresNonAlphanumeric());
        _userManager.CreateAsync(Arg.Any<AppUser>(), Password).Returns(refusal);

        var (success, result) = await _sut.RegisterUserAsync(
            new AppUser { Email = "user@example.com", UserName = "user" }, Password);

        success.ShouldBeFalse();
        result.ShouldBeSameAs(refusal);
    }
}
