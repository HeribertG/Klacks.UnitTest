// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A group-sealed day is locked for every active member of the group even while it is empty: before, a group
/// seal only reached a client through a work of the group on that day, so a new absence on an empty sealed day
/// went through. Covers the repository checks and the DayLockService guard the write handlers call, the
/// other group's members, an expired membership, and lifting the lock by the owning group's reopen only.
/// </summary>

using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Schedules;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Repositories.Schedules;

[TestFixture]
public class SealedDayRepositoryMembershipLockTests
{
    private const string ReopenedBy = "reopener";

    private static readonly DateOnly SealedDate = new(2027, 3, 14);

    private DataBaseContext _context = null!;
    private SealedDayRepository _sut = null!;

    private readonly Guid _groupA = Guid.NewGuid();
    private readonly Guid _groupB = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);
        _sut = new SealedDayRepository(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task EmptySealedDay_LocksMemberOfTheSealedGroup()
    {
        var member = await AddMemberAsync(_groupA);
        await SealAsync(_groupA);

        (await _sut.IsDayLockedAsync(SealedDate, member)).ShouldBeTrue();
        (await _sut.GetLockedPairsAsync([(SealedDate, member)])).ShouldContain((SealedDate, member));
        (await _sut.FindFirstLockedDateForClientAsync(SealedDate.AddDays(-5), SealedDate.AddDays(5), member))
            .ShouldBe(SealedDate);
    }

    [Test]
    public async Task EmptySealedDay_NewBreakOfMember_IsRefusedByDayLockService()
    {
        var member = await AddMemberAsync(_groupA);
        await SealAsync(_groupA);
        var dayLock = new DayLockService(_sut);

        var ex = await Should.ThrowAsync<InvalidRequestException>(
            () => dayLock.EnsureNotLockedAsync(SealedDate, member, analyseToken: null));

        ex.Message.ShouldContain("sealed");
    }

    [Test]
    public async Task EmptySealedDay_DoesNotLockMemberOfAnotherGroup()
    {
        var otherMember = await AddMemberAsync(_groupB);
        await SealAsync(_groupA);

        (await _sut.IsDayLockedAsync(SealedDate, otherMember)).ShouldBeFalse();
        (await _sut.GetLockedPairsAsync([(SealedDate, otherMember)])).ShouldBeEmpty();
        (await _sut.FindFirstLockedDateForClientAsync(SealedDate, SealedDate, otherMember)).ShouldBeNull();
    }

    [Test]
    public async Task EmptySealedDay_DoesNotLockFormerMember()
    {
        var formerMember = await AddMemberAsync(_groupA, new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        await SealAsync(_groupA);

        (await _sut.IsDayLockedAsync(SealedDate, formerMember)).ShouldBeFalse();
    }

    [Test]
    public async Task ReopenByOtherGroup_KeepsTheLock_ReopenByOwningGroup_LiftsIt()
    {
        var member = await AddMemberAsync(_groupA);
        await AddGroupItemAsync(member, _groupB);
        await SealAsync(_groupA);
        await SealAsync(_groupB);

        await _sut.SoftDeleteRangeAsync(SealedDate, SealedDate, _groupB, ReopenedBy);
        await _context.SaveChangesAsync();
        (await _sut.IsDayLockedAsync(SealedDate, member)).ShouldBeTrue();

        await _sut.SoftDeleteRangeAsync(SealedDate, SealedDate, _groupA, ReopenedBy);
        await _context.SaveChangesAsync();
        (await _sut.IsDayLockedAsync(SealedDate, member)).ShouldBeFalse();
    }

    [Test]
    public async Task ScenarioWrite_IsNotRefused()
    {
        var member = await AddMemberAsync(_groupA);
        await SealAsync(_groupA);
        var dayLock = new DayLockService(_sut);

        await Should.NotThrowAsync(() => dayLock.EnsureNotLockedAsync(SealedDate, member, Guid.NewGuid()));
    }

    [Test]
    public async Task ScenarioMembershipOrScenarioWork_DoNotLockTheDay()
    {
        var clientId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        _context.Membership.Add(new Membership
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = _groupA, ClientId = clientId, AnalyseToken = Guid.NewGuid() });
        _context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = _groupA, ShiftId = shiftId, ScenarioSourceGroupItemId = Guid.NewGuid() });
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = SealedDate,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
        });
        await _context.SaveChangesAsync();
        await SealAsync(_groupA);

        (await _sut.IsDayLockedAsync(SealedDate, clientId)).ShouldBeFalse();
    }

    [Test]
    public async Task GetLockedClientDays_ListsEveryLockedMemberDayOfTheRange()
    {
        var member = await AddMemberAsync(_groupA);
        var outsider = Guid.NewGuid();
        await SealAsync(_groupA);

        var locked = await _sut.GetLockedClientDaysAsync([member, outsider], SealedDate.AddDays(-3), SealedDate.AddDays(3));

        locked.ShouldBe(new[] { (member, SealedDate) });
    }
    [Test]
    public async Task EmptyApprovedDay_LocksMember_NotOtherGroupsMember_AndSurvivesPeriodReopen()
    {
        var member = await AddMemberAsync(_groupA);
        var otherMember = await AddMemberAsync(_groupB);
        await _sut.AddAsync(new SealedDay
        {
            Date = SealedDate,
            GroupId = _groupA,
            Level = WorkLockLevel.Approved,
            SealedAt = DateTime.UtcNow,
            SealedBy = ReopenedBy,
        });
        await _context.SaveChangesAsync();

        (await _sut.IsDayLockedAsync(SealedDate, member)).ShouldBeTrue();
        (await _sut.IsDayLockedAsync(SealedDate, otherMember)).ShouldBeFalse();

        await _sut.SoftDeleteRangeAsync(SealedDate, SealedDate, _groupA, ReopenedBy);
        await _context.SaveChangesAsync();

        (await _sut.IsDayLockedAsync(SealedDate, member)).ShouldBeTrue();
        (await _sut.GetRangeAsync(SealedDate, SealedDate, _groupA)).ShouldBeEmpty();
        (await _sut.GetDayApprovalsAsync(SealedDate)).ShouldHaveSingleItem().GroupId.ShouldBe(_groupA);
    }
    private async Task<Guid> AddMemberAsync(Guid groupId, DateTime? groupItemUntil = null)
    {
        var clientId = Guid.NewGuid();
        _context.Membership.Add(new Membership
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await AddGroupItemAsync(clientId, groupId, groupItemUntil);
        return clientId;
    }

    private async Task AddGroupItemAsync(Guid clientId, Guid groupId, DateTime? validUntil = null)
    {
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ClientId = clientId,
            ValidUntil = validUntil,
        });
        await _context.SaveChangesAsync();
    }

    private async Task SealAsync(Guid groupId)
    {
        await _sut.AddAsync(new SealedDay
        {
            Date = SealedDate,
            GroupId = groupId,
            Level = WorkLockLevel.Closed,
            SealedAt = DateTime.UtcNow,
            SealedBy = ReopenedBy,
        });
        await _context.SaveChangesAsync();
    }
}
