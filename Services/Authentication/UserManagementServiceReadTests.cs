// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterizes the user reads of UserManagementService over a real UserManager and UserStore (EF InMemory) instead
/// of a substituted UserManager: the user list skips deactivated accounts, comes in DisplayOrder and flags the
/// Authorised and Admin roles; the password-reset lookup matches the stored token hash and returns a tracked user
/// (the reset flow changes and saves it); the lookup by id returns an untracked user.
/// </summary>

using Klacks.Api.Application.Services.Authentication;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Services.Authentication;

[TestFixture]
public class UserManagementServiceReadTests
{
    private DataBaseContext _context = null!;
    private UserManager<AppUser> _userManager = null!;
    private UserManagementService _sut = null!;

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
        _sut = new UserManagementService(_userManager, Substitute.For<ILogger<UserManagementService>>());
    }

    [TearDown]
    public void TearDown()
    {
        _userManager.Dispose();
        _context.Dispose();
    }

    [Test]
    public async Task UserList_SkipsDeactivatedAccounts_InDisplayOrder_WithRoleFlags()
    {
        var third = User("third", displayOrder: 3);
        var first = User("first", displayOrder: 1);
        var deactivated = User("gone", displayOrder: 0);
        deactivated.DeactivatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var second = User("second", displayOrder: 2);
        _context.AppUser.AddRange(third, first, deactivated, second);
        var admin = new IdentityRole(Roles.Admin) { Id = Guid.NewGuid().ToString(), NormalizedName = Roles.Admin.ToUpperInvariant() };
        var authorised = new IdentityRole(Roles.Authorised) { Id = Guid.NewGuid().ToString(), NormalizedName = Roles.Authorised.ToUpperInvariant() };
        _context.Roles.AddRange(admin, authorised);
        _context.UserRoles.AddRange(
            new IdentityUserRole<string> { UserId = first.Id, RoleId = admin.Id },
            new IdentityUserRole<string> { UserId = second.Id, RoleId = authorised.Id });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var list = await _sut.GetUserListAsync();

        list.Select(u => u.UserName).ShouldBe(["first", "second", "third"]);
        list.Select(u => (u.IsAdmin, u.IsAuthorised)).ShouldBe([(true, false), (false, true), (false, false)]);
        list[0].Email.ShouldBe("first@test.local");
        list[0].FirstName.ShouldBe("Fn-first");
    }

    [Test]
    public async Task FindByToken_MatchesTheStoredHash_AndReturnsATrackedUser()
    {
        const string token = "reset-token-value";
        var holder = User("holder", displayOrder: 1);
        holder.PasswordResetToken = PasswordResetTokenHasher.Hash(token);
        var other = User("other", displayOrder: 2);
        other.PasswordResetToken = PasswordResetTokenHasher.Hash("another-token");
        _context.AppUser.AddRange(holder, other);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var found = await _sut.FindUserByTokenAsync(token);

        found.ShouldNotBeNull();
        found.Id.ShouldBe(holder.Id);
        _context.Entry(found).State.ShouldBe(EntityState.Unchanged);
        (await _sut.FindUserByTokenAsync("unknown-token")).ShouldBeNull();
    }

    [Test]
    public async Task FindByIdNoTracking_ReturnsAnUntrackedUser()
    {
        var user = User("plain", displayOrder: 1);
        _context.AppUser.Add(user);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var found = await _sut.FindUserByIdNoTrackingAsync(user.Id);

        found.ShouldNotBeNull();
        found.UserName.ShouldBe("plain");
        _context.Entry(found).State.ShouldBe(EntityState.Detached);
        (await _sut.FindUserByIdNoTrackingAsync(Guid.NewGuid().ToString())).ShouldBeNull();
    }

    private static AppUser User(string userName, int displayOrder) => new()
    {
        Id = Guid.NewGuid().ToString(),
        UserName = userName,
        NormalizedUserName = userName.ToUpperInvariant(),
        Email = $"{userName}@test.local",
        NormalizedEmail = $"{userName}@test.local".ToUpperInvariant(),
        FirstName = "Fn-" + userName,
        LastName = "Ln-" + userName,
        DisplayOrder = displayOrder,
    };
}
