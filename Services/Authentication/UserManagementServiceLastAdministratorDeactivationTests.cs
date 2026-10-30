// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterizes the last-administrator protection of UserManagementService.DeactivateUserAsync over a real
/// UserManager and UserStore (EF InMemory): deactivating the only active administrator is refused with the
/// deactivation message and leaves the account active; with a second active administrator the count decides; accounts
/// without the Admin role are never blocked; and removing the Admin role and deactivating share one count, so the
/// second of two such steps against the last two administrators is refused.
/// </summary>

using Klacks.Api.Application.Services.Authentication;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Repositories.Authentification;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Services.Authentication;

[TestFixture]
public class UserManagementServiceLastAdministratorDeactivationTests
{
    private const string ActingAdminId = "0f9d3f1a-2b4c-4d5e-8a91-1122334455aa";

    private DataBaseContext _context = null!;
    private UserManager<AppUser> _userManager = null!;
    private UserManagementService _sut = null!;
    private IdentityRole _adminRole = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _userManager = new UserManager<AppUser>(
            new UserStore<AppUser, IdentityRole, DataBaseContext>(_context),
            Options.Create(new IdentityOptions()),
            new PasswordHasher<AppUser>(),
            [],
            [],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<AppUser>>.Instance);
        _sut = new UserManagementService(
            _userManager, new AppUserReader(_userManager), Substitute.For<ILogger<UserManagementService>>());
        _adminRole = new IdentityRole(Roles.Admin)
        {
            Id = Guid.NewGuid().ToString(),
            NormalizedName = Roles.Admin.ToUpperInvariant(),
        };
        _context.Roles.Add(_adminRole);
        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        _userManager.Dispose();
        _context.Dispose();
    }

    [Test]
    public async Task DeactivatingTheOnlyActiveAdministrator_IsRefusedAndTheAccountStaysActive()
    {
        var only = await Account("only", admin: true);

        var (success, message) = await _sut.DeactivateUserAsync(Guid.Parse(only.Id), ActingAdminId);

        success.ShouldBeFalse();
        message.ShouldBe(LastAdministratorRule.DeactivationRefusalMessage);
        (await _userManager.FindByIdAsync(only.Id))!.DeactivatedAt.ShouldBeNull();
        (await _sut.CountActiveAdministratorsAsync()).ShouldBe(1);
    }

    [Test]
    public async Task DeactivatingAnAdministrator_WithASecondActiveAdministrator_Succeeds()
    {
        var first = await Account("first", admin: true);
        await Account("second", admin: true);

        var (success, _) = await _sut.DeactivateUserAsync(Guid.Parse(first.Id), ActingAdminId);

        success.ShouldBeTrue();
        (await _userManager.FindByIdAsync(first.Id))!.DeactivatedAt.ShouldNotBeNull();
        (await _sut.CountActiveAdministratorsAsync()).ShouldBe(1);
    }

    [Test]
    public async Task OtherAdministratorAlreadyDeactivated_TheActiveOneIsStillTheLast()
    {
        var active = await Account("active", admin: true);
        await Account("gone", admin: true, deactivated: true);

        var (success, message) = await _sut.DeactivateUserAsync(Guid.Parse(active.Id), ActingAdminId);

        success.ShouldBeFalse();
        message.ShouldBe(LastAdministratorRule.DeactivationRefusalMessage);
    }

    [Test]
    public async Task DeactivatingAnAccountWithoutTheAdminRole_IsNeverBlocked()
    {
        await Account("only", admin: true);
        var planner = await Account("planner", admin: false);

        var (success, _) = await _sut.DeactivateUserAsync(Guid.Parse(planner.Id), ActingAdminId);

        success.ShouldBeTrue();
        (await _sut.CountActiveAdministratorsAsync()).ShouldBe(1);
    }

    [Test]
    public async Task SeededAdminHandover_NewAdminHoldsTheRole_SoTheSeedAdminCanBeDeactivated()
    {
        var seed = await Account("seed", admin: true);
        var newcomer = await Account("newcomer", admin: false);
        (await _sut.ChangeUserRoleAsync(newcomer.Id, Roles.Admin, true)).Success.ShouldBeTrue();

        var (success, _) = await _sut.DeactivateUserAsync(Guid.Parse(seed.Id), ActingAdminId);

        success.ShouldBeTrue();
        (await _sut.CountActiveAdministratorsAsync()).ShouldBe(1);
    }

    [Test]
    public async Task DemotionThenDeactivation_AgainstTheLastTwoAdministrators_RefusesTheSecondStep()
    {
        var first = await Account("first", admin: true);
        var second = await Account("second", admin: true);

        (await _sut.ChangeUserRoleAsync(first.Id, Roles.Admin, false)).Success.ShouldBeTrue();
        var (success, message) = await _sut.DeactivateUserAsync(Guid.Parse(second.Id), ActingAdminId);

        success.ShouldBeFalse();
        message.ShouldBe(LastAdministratorRule.DeactivationRefusalMessage);
        (await _sut.CountActiveAdministratorsAsync()).ShouldBe(1);
    }

    [Test]
    public async Task DeactivationThenDemotion_AgainstTheLastTwoAdministrators_RefusesTheSecondStep()
    {
        var first = await Account("first", admin: true);
        var second = await Account("second", admin: true);

        (await _sut.DeactivateUserAsync(Guid.Parse(first.Id), ActingAdminId)).Success.ShouldBeTrue();
        var (success, message) = await _sut.ChangeUserRoleAsync(second.Id, Roles.Admin, false);

        success.ShouldBeFalse();
        message.ShouldBe(LastAdministratorRule.RefusalMessage);
        (await _sut.CountActiveAdministratorsAsync()).ShouldBe(1);
    }

    [Test]
    public async Task UnknownAccountAndAlreadyDeactivatedAccount_KeepTheirOwnMessages()
    {
        var gone = await Account("gone", admin: true, deactivated: true);
        await Account("active", admin: true);

        var unknown = await _sut.DeactivateUserAsync(Guid.NewGuid(), ActingAdminId);
        var already = await _sut.DeactivateUserAsync(Guid.Parse(gone.Id), ActingAdminId);

        unknown.Message.ShouldNotBe(LastAdministratorRule.DeactivationRefusalMessage);
        already.Message.ShouldNotBe(LastAdministratorRule.DeactivationRefusalMessage);
        unknown.Success.ShouldBeFalse();
        already.Success.ShouldBeFalse();
    }

    private async Task<AppUser> Account(string name, bool admin, bool deactivated = false)
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            Email = $"{name}@test.local",
            NormalizedEmail = $"{name}@test.local".ToUpperInvariant(),
            FirstName = "Fn-" + name,
            LastName = "Ln-" + name,
            DeactivatedAt = deactivated ? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null,
        };
        _context.AppUser.Add(user);
        if (admin)
        {
            _context.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = _adminRole.Id });
        }

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return (await _userManager.FindByIdAsync(user.Id))!;
    }
}
