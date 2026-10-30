// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterizes the last-administrator protection of UserManagementService.ChangeUserRoleAsync over a real
/// UserManager and UserStore (EF InMemory): removing Admin from the only active administrator is refused with the
/// shared message and leaves the role in place; with a second active administrator, or when the other administrator is
/// deactivated, the count decides; granting Admin and removing other roles are never blocked; and the advisor's verdict
/// on the same account matches what the service enforces.
/// </summary>

using Klacks.Api.Application.DTOs.Authentification;
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
public class UserManagementServiceLastAdministratorTests
{
    private DataBaseContext _context = null!;
    private UserManager<AppUser> _userManager = null!;
    private UserManagementService _sut = null!;
    private IdentityRole _adminRole = null!;
    private IdentityRole _authorisedRole = null!;

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
        _adminRole = Role(Roles.Admin);
        _authorisedRole = Role(Roles.Authorised);
        _context.Roles.AddRange(_adminRole, _authorisedRole);
        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        _userManager.Dispose();
        _context.Dispose();
    }

    [Test]
    public async Task RemovingAdmin_FromTheOnlyActiveAdministrator_IsRefusedAndTheRoleStays()
    {
        var only = await Account("only", admin: true);

        var (success, message) = await _sut.ChangeUserRoleAsync(only.Id, Roles.Admin, false);

        success.ShouldBeFalse();
        message.ShouldBe(LastAdministratorRule.RefusalMessage);
        (await _userManager.IsInRoleAsync(only, Roles.Admin)).ShouldBeTrue();
    }

    [Test]
    public async Task RemovingAdmin_WithASecondActiveAdministrator_Succeeds()
    {
        var first = await Account("first", admin: true);
        await Account("second", admin: true);

        var (success, _) = await _sut.ChangeUserRoleAsync(first.Id, Roles.Admin, false);

        success.ShouldBeTrue();
        (await _userManager.IsInRoleAsync(first, Roles.Admin)).ShouldBeFalse();
        (await _sut.CountActiveAdministratorsAsync()).ShouldBe(1);
    }

    [Test]
    public async Task OtherAdministratorDeactivated_TheActiveOneIsStillTheLast()
    {
        var active = await Account("active", admin: true);
        await Account("gone", admin: true, deactivated: true);

        var (success, message) = await _sut.ChangeUserRoleAsync(active.Id, Roles.Admin, false);

        success.ShouldBeFalse();
        message.ShouldBe(LastAdministratorRule.RefusalMessage);
    }

    [Test]
    public async Task RemovingAdmin_FromADeactivatedAdministrator_IsAllowed()
    {
        await Account("active", admin: true);
        var gone = await Account("gone", admin: true, deactivated: true);

        var (success, _) = await _sut.ChangeUserRoleAsync(gone.Id, Roles.Admin, false);

        success.ShouldBeTrue();
        (await _sut.CountActiveAdministratorsAsync()).ShouldBe(1);
    }

    [Test]
    public async Task GrantingAdmin_AndRemovingAnotherRole_AreNeverBlocked()
    {
        var only = await Account("only", admin: true);
        var other = await Account("other", admin: false);
        await _userManager.AddToRoleAsync(only, Roles.Authorised);

        (await _sut.ChangeUserRoleAsync(other.Id, Roles.Admin, true)).Success.ShouldBeTrue();
        (await _sut.ChangeUserRoleAsync(only.Id, Roles.Authorised, false)).Success.ShouldBeTrue();
        (await _sut.CountActiveAdministratorsAsync()).ShouldBe(2);
    }

    [Test]
    public async Task AdminRoleName_IsMatchedCaseInsensitively()
    {
        var only = await Account("only", admin: true);

        var (success, message) = await _sut.ChangeUserRoleAsync(only.Id, Roles.Admin.ToUpperInvariant(), false);

        success.ShouldBeFalse();
        message.ShouldBe(LastAdministratorRule.RefusalMessage);
    }

    [TestCase(1)]
    [TestCase(2)]
    public async Task AdvisorBlockingVerdict_MatchesWhatTheServiceEnforces(int administrators)
    {
        var target = await Account("target", admin: true);
        for (var i = 1; i < administrators; i++)
        {
            await Account($"extra{i}", admin: true);
        }

        var evaluation = UserRoleChangeAdvisor.Evaluate(new UserRoleChangeFacts(
            target.Id, "Target", [Roles.Admin], Roles.Authorised, true, await _sut.CountActiveAdministratorsAsync(), false));
        var (success, _) = await _sut.ChangeUserRoleAsync(target.Id, Roles.Admin, false);

        evaluation.Findings.Any(finding => finding.Code == UserRoleChangeFindingCode.LastAdministratorDemotion)
            .ShouldBe(!success);
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

    private static IdentityRole Role(string name) => new(name)
    {
        Id = Guid.NewGuid().ToString(),
        NormalizedName = name.ToUpperInvariant(),
    };
}
