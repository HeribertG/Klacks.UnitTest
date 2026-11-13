// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// GroupMembershipService works on regular memberships only: saving the member list of a group neither removes the
/// temporary memberships of borrowed days nor the memberships a scenario holds, a person who is only borrowed is not
/// yet a member (adding them writes a regular membership next to the borrowed days), and removing a member leaves the
/// borrowed days alone. Runs on the in-memory provider, which enforces no unique index.
/// </summary>

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Groups;

[TestFixture]
public class GroupMembershipServiceTemporaryRowsTests
{
    private static readonly DateTime Monday = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Friday = new(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc);

    private readonly Guid _groupId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _borrowedId = Guid.NewGuid();
    private readonly Guid _scenarioToken = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private GroupMembershipService _service = null!;
    private GroupItem _regular = null!;
    private GroupItem _borrowedMonday = null!;
    private GroupItem _borrowedFriday = null!;
    private GroupItem _scenarioDay = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _service = new GroupMembershipService(
            _context, Substitute.For<ILogger<GroupMembershipService>>(), Substitute.For<IGroupHierarchyService>());

        _regular = new GroupItem { Id = Guid.NewGuid(), GroupId = _groupId, ClientId = _memberId };
        _borrowedMonday = TemporaryDay(Monday, token: null);
        _borrowedFriday = TemporaryDay(Friday, token: null);
        _scenarioDay = TemporaryDay(Monday, _scenarioToken);
        _context.GroupItem.AddRange(_regular, _borrowedMonday, _borrowedFriday, _scenarioDay);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task SavingTheMemberList_WithoutTheBorrowedPerson_LeavesTheirBorrowedDaysAndScenarioRows()
    {
        // Act: the member list of the master data names the regular member only.
        await _service.UpdateGroupMembershipAsync(_groupId, [_memberId]);

        // Assert
        (await LiveIdsAsync()).ShouldBe([_regular.Id, _borrowedMonday.Id, _borrowedFriday.Id, _scenarioDay.Id], ignoreOrder: true);
    }

    [Test]
    public async Task SavingAnEmptyMemberList_RemovesTheRegularMemberOnly()
    {
        // Act
        await _service.UpdateGroupMembershipAsync(_groupId, []);

        // Assert
        (await LiveIdsAsync()).ShouldBe([_borrowedMonday.Id, _borrowedFriday.Id, _scenarioDay.Id], ignoreOrder: true);
    }

    [Test]
    public async Task SavingTheMemberList_WithTheBorrowedPerson_MakesThemARegularMember_NextToTheBorrowedDays()
    {
        // Act
        await _service.UpdateGroupMembershipAsync(_groupId, [_memberId, _borrowedId]);

        // Assert
        var rowsOfBorrowed = await _context.GroupItem.AsNoTracking()
            .Where(gi => gi.ClientId == _borrowedId && gi.AnalyseToken == null)
            .ToListAsync();
        rowsOfBorrowed.Count(gi => !gi.IsTemporary).ShouldBe(1);
        rowsOfBorrowed.Count(gi => gi.IsTemporary).ShouldBe(2);
    }

    [Test]
    public async Task ABorrowedPerson_IsNoMember_AndIsNotCounted()
    {
        // Act
        var isMember = await _service.IsClientInGroupAsync(_groupId, _borrowedId);
        var count = await _service.GetGroupMemberCountAsync(_groupId);

        // Assert
        isMember.ShouldBeFalse();
        count.ShouldBe(1);
    }

    [Test]
    public async Task AddingABorrowedPerson_WritesARegularMembership()
    {
        // Act
        await _service.AddClientToGroupAsync(_groupId, _borrowedId);

        // Assert
        var rowsOfBorrowed = await _context.GroupItem.AsNoTracking()
            .Where(gi => gi.ClientId == _borrowedId && gi.AnalyseToken == null)
            .ToListAsync();
        rowsOfBorrowed.Count(gi => !gi.IsTemporary).ShouldBe(1);
        rowsOfBorrowed.Select(gi => gi.Id).ShouldContain(_borrowedMonday.Id);
        rowsOfBorrowed.Select(gi => gi.Id).ShouldContain(_borrowedFriday.Id);
    }

    [Test]
    public async Task RemovingABorrowedPerson_WhoIsNoRegularMember_RemovesNothing()
    {
        // Act
        await _service.RemoveClientFromGroupAsync(_groupId, _borrowedId);

        // Assert
        (await LiveIdsAsync()).ShouldBe([_regular.Id, _borrowedMonday.Id, _borrowedFriday.Id, _scenarioDay.Id], ignoreOrder: true);
    }

    private GroupItem TemporaryDay(DateTime day, Guid? token) => new()
    {
        Id = Guid.NewGuid(),
        GroupId = _groupId,
        ClientId = _borrowedId,
        ValidFrom = day,
        ValidUntil = day,
        AnalyseToken = token,
        IsTemporary = true,
    };

    private Task<List<Guid>> LiveIdsAsync()
        => _context.GroupItem.AsNoTracking().Select(gi => gi.Id).ToListAsync();
}
