// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ShiftGroupScopeReadRepository, the single place that decides which group memberships
/// may move a proactive notification's audience. Covers the many-to-many case a single GroupId cannot
/// express, the soft-delete and scenario exclusions (GroupItem has no global query filter, so both are
/// manual and therefore droppable), the stable ordering the ledger's representative value depends on,
/// the work-to-shift resolution, and the empty-input short circuit. Uses a real EF Core InMemory
/// DataBaseContext, as EmptyContainerDetectorTests does, because the assertions are about a query.
/// </summary>

using Klacks.Api.Domain.Models.Associations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class ShiftGroupScopeReadRepositoryTests
{
    private DataBaseContext _context = null!;
    private ShiftGroupScopeReadRepository _sut = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _sut = new ShiftGroupScopeReadRepository(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    private async Task<Guid> AddShiftAsync(params Guid[] groupIds)
    {
        var shiftId = Guid.NewGuid();
        foreach (var groupId in groupIds)
        {
            await _context.GroupItem.AddAsync(new GroupItem { Id = Guid.NewGuid(), ShiftId = shiftId, GroupId = groupId });
        }

        await _context.SaveChangesAsync();
        return shiftId;
    }

    private async Task<Guid> AddWorkAsync(Guid shiftId, bool isDeleted = false)
    {
        var workId = Guid.NewGuid();
        await _context.Work.AddAsync(new Work
        {
            Id = workId,
            ShiftId = shiftId,
            CurrentDate = DateOnly.FromDateTime(DateTime.UtcNow),
            IsDeleted = isDeleted
        });
        await _context.SaveChangesAsync();
        return workId;
    }

    [Test]
    public async Task GetGroupIdsByShiftIdsAsync_ShiftInThreeGroups_ReturnsAllThree()
    {
        var groupIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var shiftId = await AddShiftAsync(groupIds);

        var result = await _sut.GetGroupIdsByShiftIdsAsync(new[] { shiftId });

        Assert.That(result[shiftId], Is.EquivalentTo(groupIds));
    }

    [Test]
    public async Task GetGroupIdsByShiftIdsAsync_OrdersGroupsById_SoTheLedgerRepresentativeIsStable()
    {
        var groupIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var shiftId = await AddShiftAsync(groupIds);

        var result = await _sut.GetGroupIdsByShiftIdsAsync(new[] { shiftId });

        Assert.That(result[shiftId], Is.EqualTo(groupIds.OrderBy(groupId => groupId).ToList()));
    }

    [Test]
    public async Task GetGroupIdsByShiftIdsAsync_ShiftWithoutAnyMembership_IsAbsentFromTheResult()
    {
        var shiftId = Guid.NewGuid();

        var result = await _sut.GetGroupIdsByShiftIdsAsync(new[] { shiftId });

        Assert.That(result.ContainsKey(shiftId), Is.False);
    }

    [Test]
    public async Task GetGroupIdsByShiftIdsAsync_SoftDeletedMembership_IsExcluded()
    {
        // GroupItem carries no global soft-delete query filter, so this exclusion is manual and would
        // silently disappear if the Where clause were simplified.
        var shiftId = Guid.NewGuid();
        var liveGroupId = Guid.NewGuid();
        await _context.GroupItem.AddAsync(new GroupItem { Id = Guid.NewGuid(), ShiftId = shiftId, GroupId = liveGroupId });
        await _context.GroupItem.AddAsync(new GroupItem { Id = Guid.NewGuid(), ShiftId = shiftId, GroupId = Guid.NewGuid(), IsDeleted = true });
        await _context.SaveChangesAsync();

        var result = await _sut.GetGroupIdsByShiftIdsAsync(new[] { shiftId });

        Assert.That(result[shiftId], Is.EqualTo(new[] { liveGroupId }));
    }

    [Test]
    public async Task GetGroupIdsByShiftIdsAsync_ScenarioMembership_IsExcluded()
    {
        // A what-if scenario must never move the audience of a real notification.
        var shiftId = Guid.NewGuid();
        var liveGroupId = Guid.NewGuid();
        await _context.GroupItem.AddAsync(new GroupItem { Id = Guid.NewGuid(), ShiftId = shiftId, GroupId = liveGroupId });
        await _context.GroupItem.AddAsync(new GroupItem
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            GroupId = Guid.NewGuid(),
            AnalyseToken = Guid.NewGuid()
        });
        await _context.GroupItem.AddAsync(new GroupItem
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            GroupId = Guid.NewGuid(),
            ScenarioSourceGroupItemId = Guid.NewGuid()
        });
        await _context.SaveChangesAsync();

        var result = await _sut.GetGroupIdsByShiftIdsAsync(new[] { shiftId });

        Assert.That(result[shiftId], Is.EqualTo(new[] { liveGroupId }));
    }

    [Test]
    public async Task GetGroupIdsByShiftIdsAsync_ClientMembershipOfTheSameGroup_IsNotMistakenForAShift()
    {
        var shiftId = Guid.NewGuid();
        var shiftGroupId = Guid.NewGuid();
        await _context.GroupItem.AddAsync(new GroupItem { Id = Guid.NewGuid(), ShiftId = shiftId, GroupId = shiftGroupId });
        await _context.GroupItem.AddAsync(new GroupItem { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), GroupId = Guid.NewGuid() });
        await _context.SaveChangesAsync();

        var result = await _sut.GetGroupIdsByShiftIdsAsync(new[] { shiftId });

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[shiftId], Is.EqualTo(new[] { shiftGroupId }));
    }

    [Test]
    public async Task GetGroupIdsByShiftIdsAsync_ResolvesSeveralShiftsAtOnce_AndIgnoresUnrequestedOnes()
    {
        var firstGroupId = Guid.NewGuid();
        var secondGroupId = Guid.NewGuid();
        var firstShiftId = await AddShiftAsync(firstGroupId);
        var secondShiftId = await AddShiftAsync(secondGroupId, firstGroupId);
        await AddShiftAsync(Guid.NewGuid());

        var result = await _sut.GetGroupIdsByShiftIdsAsync(new[] { firstShiftId, secondShiftId });

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[firstShiftId], Is.EqualTo(new[] { firstGroupId }));
        Assert.That(result[secondShiftId], Is.EquivalentTo(new[] { firstGroupId, secondGroupId }));
    }

    [Test]
    public async Task GetGroupIdsByShiftIdsAsync_NoIds_ReturnsEmptyWithoutQuerying()
    {
        var result = await _sut.GetGroupIdsByShiftIdsAsync(Array.Empty<Guid>());

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetGroupIdsByWorkIdsAsync_ResolvesThroughTheWorksShift()
    {
        var groupIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var shiftId = await AddShiftAsync(groupIds);
        var workId = await AddWorkAsync(shiftId);

        var result = await _sut.GetGroupIdsByWorkIdsAsync(new[] { workId });

        Assert.That(result[workId], Is.EquivalentTo(groupIds));
    }

    [Test]
    public async Task GetGroupIdsByWorkIdsAsync_UnknownWorkId_IsAbsentFromTheResult()
    {
        var unknownWorkId = Guid.NewGuid();

        var result = await _sut.GetGroupIdsByWorkIdsAsync(new[] { unknownWorkId });

        Assert.That(result.ContainsKey(unknownWorkId), Is.False);
    }

    [Test]
    public async Task GetGroupIdsByWorkIdsAsync_SoftDeletedWork_IsExcluded()
    {
        var shiftId = await AddShiftAsync(Guid.NewGuid());
        var workId = await AddWorkAsync(shiftId, isDeleted: true);

        var result = await _sut.GetGroupIdsByWorkIdsAsync(new[] { workId });

        Assert.That(result.ContainsKey(workId), Is.False);
    }

    [Test]
    public async Task GetGroupIdsByWorkIdsAsync_NoIds_ReturnsEmptyWithoutQuerying()
    {
        var result = await _sut.GetGroupIdsByWorkIdsAsync(Array.Empty<Guid>());

        Assert.That(result, Is.Empty);
    }
}
