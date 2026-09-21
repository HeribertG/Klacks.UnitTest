// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GroupVisibilityPreservationService: introducing the very first group must not
/// silently take everything away from the non-admins that were unrestricted until then, while every
/// later group must change nothing (fail-closed stays fail-closed for users created afterwards).
/// </summary>

using Klacks.Api.Domain.Models.Authentification;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Groups;

[TestFixture]
internal class GroupVisibilityPreservationServiceTests
{
    private const string AdminUserId = "admin-user-id";
    private const string PlannerUserId = "planner-user-id";
    private const string SupervisorUserId = "supervisor-user-id";
    private const string DeactivatedUserId = "deactivated-user-id";

    private DataBaseContext _context = null!;
    private IGroupVisibilityService _groupVisibility = null!;
    private GroupVisibilityPreservationService _service = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _context.Database.EnsureCreated();

        _context.AppUser.AddRange(
            new AppUser { Id = AdminUserId, UserName = "admin@test.com", Email = "admin@test.com" },
            new AppUser { Id = PlannerUserId, UserName = "planner@test.com", Email = "planner@test.com" },
            new AppUser { Id = SupervisorUserId, UserName = "supervisor@test.com", Email = "supervisor@test.com" },
            new AppUser
            {
                Id = DeactivatedUserId,
                UserName = "gone@test.com",
                Email = "gone@test.com",
                DeactivatedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        _context.SaveChanges();

        _groupVisibility = Substitute.For<IGroupVisibilityService>();
        _groupVisibility.ReadAdmins().Returns(_ => Task.FromResult(new List<string> { AdminUserId }));
        _groupVisibility.AnyGroupsExistAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(_context.Group.Any()));

        _service = new GroupVisibilityPreservationService(
            _context, _groupVisibility, Substitute.For<ILogger<GroupVisibilityPreservationService>>());
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Test]
    public async Task FirstRootGroup_GrantsVisibility_ToEveryActiveNonAdminUser()
    {
        var root = NewRoot("Bern");

        var granted = await CreateGroupAsync(root);

        granted.ShouldBe(2);
        var rows = await _context.GroupVisibility.AsNoTracking().ToListAsync();
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => r.GroupId == root.Id);
        rows.ShouldContain(r => r.AppUserId == PlannerUserId);
        rows.ShouldContain(r => r.AppUserId == SupervisorUserId);
    }

    [Test]
    public async Task FirstRootGroup_GrantsNothing_ToAdminsOrDeactivatedUsers()
    {
        await CreateGroupAsync(NewRoot("Bern"));

        var userIds = await _context.GroupVisibility.AsNoTracking().Select(r => r.AppUserId).ToListAsync();
        userIds.ShouldNotContain(AdminUserId);
        userIds.ShouldNotContain(DeactivatedUserId);
    }

    [Test]
    public async Task SecondRootGroup_InALaterScope_GrantsNothing()
    {
        await CreateGroupAsync(NewRoot("Bern"));
        var rowsAfterFirst = await _context.GroupVisibility.AsNoTracking().CountAsync();

        var laterScope = NewScope();
        var second = NewRoot("Zuerich");
        (await laterScope.RequiresPreservationAsync(second)).ShouldBeFalse();

        _context.Group.Add(second);
        await _context.SaveChangesAsync();

        (await _context.GroupVisibility.AsNoTracking().CountAsync()).ShouldBe(rowsAfterFirst);
    }

    [Test]
    public async Task UserCreatedAfterTheFirstGroup_StaysFailClosed()
    {
        await CreateGroupAsync(NewRoot("Bern"));

        const string newcomerId = "newcomer-user-id";
        _context.AppUser.Add(new AppUser { Id = newcomerId, UserName = "new@test.com", Email = "new@test.com" });
        await _context.SaveChangesAsync();

        var laterScope = NewScope();
        var second = NewRoot("Zuerich");
        (await laterScope.RequiresPreservationAsync(second)).ShouldBeFalse();
        (await laterScope.CountUsersKeepingFullVisibilityAsync()).ShouldBe(0);

        var rows = await _context.GroupVisibility.AsNoTracking().Select(r => r.AppUserId).ToListAsync();
        rows.ShouldNotContain(newcomerId);
    }

    [Test]
    public async Task BulkCreation_InOneScope_CoversEveryNewRoot()
    {
        var firstRoot = NewRoot("Bern");
        var secondRoot = NewRoot("Zuerich");
        var child = new Group { Id = Guid.NewGuid(), Name = "Bern Nord", ValidFrom = Today, Parent = firstRoot.Id };

        await CreateGroupAsync(firstRoot);
        await CreateGroupAsync(child);
        await CreateGroupAsync(secondRoot);

        var rows = await _context.GroupVisibility.AsNoTracking().ToListAsync();
        rows.Count.ShouldBe(4);
        rows.Count(r => r.GroupId == firstRoot.Id).ShouldBe(2);
        rows.Count(r => r.GroupId == secondRoot.Id).ShouldBe(2);
        rows.ShouldNotContain(r => r.GroupId == child.Id);
    }

    [Test]
    public async Task ChildGroup_NeverTriggersPreservation()
    {
        var parent = NewRoot("Bern");
        _context.Group.Add(parent);
        await _context.SaveChangesAsync();

        var child = new Group { Id = Guid.NewGuid(), Name = "Bern Nord", ValidFrom = Today, Parent = parent.Id };

        (await NewScope().RequiresPreservationAsync(child)).ShouldBeFalse();
    }

    [Test]
    public async Task PreserveForNewRoot_IsIdempotent_AndKeepsAnExistingRow()
    {
        var root = NewRoot("Bern");
        _context.Group.Add(root);
        await _context.SaveChangesAsync();

        var existingRowId = Guid.NewGuid();
        _context.GroupVisibility.Add(new GroupVisibility
        {
            Id = existingRowId,
            AppUserId = PlannerUserId,
            GroupId = root.Id
        });
        await _context.SaveChangesAsync();

        var granted = await _service.PreserveForNewRootAsync(root);
        await _context.SaveChangesAsync();

        granted.ShouldBe(1);
        var rows = await _context.GroupVisibility.AsNoTracking().ToListAsync();
        rows.Count.ShouldBe(2);
        rows.Count(r => r.AppUserId == PlannerUserId).ShouldBe(1);
        rows.ShouldContain(r => r.Id == existingRowId);

        var again = await _service.PreserveForNewRootAsync(root);
        await _context.SaveChangesAsync();

        again.ShouldBe(0);
        (await _context.GroupVisibility.AsNoTracking().CountAsync()).ShouldBe(2);
    }

    [Test]
    public async Task SoftDeletedGroups_DoNotCountAsExistingGroups()
    {
        var deleted = NewRoot("Alt");
        deleted.IsDeleted = true;
        _context.Group.Add(deleted);
        await _context.SaveChangesAsync();

        var granted = await CreateGroupAsync(NewRoot("Bern"));

        granted.ShouldBe(2);
    }

    [Test]
    public async Task CountUsersKeepingFullVisibility_CountsAffectedUsers_WhileNoGroupExists()
    {
        (await _service.CountUsersKeepingFullVisibilityAsync()).ShouldBe(2);
    }

    [Test]
    public async Task CountUsersKeepingFullVisibility_IsZero_OnceAGroupExists()
    {
        _context.Group.Add(NewRoot("Bern"));
        await _context.SaveChangesAsync();

        (await NewScope().CountUsersKeepingFullVisibilityAsync()).ShouldBe(0);
    }

    private static DateTime Today => new(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);

    private static Group NewRoot(string name) =>
        new() { Id = Guid.NewGuid(), Name = name, ValidFrom = Today };

    private GroupVisibilityPreservationService NewScope() =>
        new(_context, _groupVisibility, Substitute.For<ILogger<GroupVisibilityPreservationService>>());

    private async Task<int> CreateGroupAsync(Group group)
    {
        var requiresPreservation = await _service.RequiresPreservationAsync(group);

        _context.Group.Add(group);

        var granted = requiresPreservation ? await _service.PreserveForNewRootAsync(group) : 0;

        await _context.SaveChangesAsync();

        return granted;
    }
}
