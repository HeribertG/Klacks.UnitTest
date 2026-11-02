// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for PeriodClosedEntryFilter against an in-memory EF Core database, verifying
/// global seals, group-scoped seals via an active group membership (Membership and GroupItem validity windows) or
/// via a worked shift of the sealed group (a non-member pool employee), and that soft-deleted or non-closed
/// SealedDay rows never mark an entry as period-closed.
/// </summary>
using Shouldly;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Exports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class PeriodClosedEntryFilterTests
{
    private DataBaseContext _context = null!;
    private PeriodClosedEntryFilter _filter = null!;

    private readonly Guid _memberClientId = Guid.NewGuid();
    private readonly Guid _nonMemberClientId = Guid.NewGuid();
    private readonly Guid _sealedGroupId = Guid.NewGuid();
    private readonly Guid _sealedGroupShiftId = Guid.NewGuid();
    private readonly Guid _otherShiftId = Guid.NewGuid();

    private static readonly DateOnly FromDate = new(2026, 1, 1);
    private static readonly DateOnly UntilDate = new(2026, 1, 31);
    private static readonly DateOnly GloballySealedDate = new(2026, 1, 10);
    private static readonly DateOnly GroupSealedDate = new(2026, 1, 15);
    private static readonly DateOnly UnsealedDate = new(2026, 1, 20);
    private static readonly DateOnly SoftDeletedSealDate = new(2026, 1, 21);
    private static readonly DateOnly ApprovedOnlySealDate = new(2026, 1, 22);

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _filter = new PeriodClosedEntryFilter(_context);

        _context.SealedDay.Add(new SealedDay
        {
            Id = Guid.NewGuid(),
            Date = GloballySealedDate,
            GroupId = null,
            Level = WorkLockLevel.Closed,
        });
        _context.SealedDay.Add(new SealedDay
        {
            Id = Guid.NewGuid(),
            Date = GroupSealedDate,
            GroupId = _sealedGroupId,
            Level = WorkLockLevel.Closed,
        });
        _context.SealedDay.Add(new SealedDay
        {
            Id = Guid.NewGuid(),
            Date = SoftDeletedSealDate,
            GroupId = null,
            Level = WorkLockLevel.Closed,
            IsDeleted = true,
        });
        _context.SealedDay.Add(new SealedDay
        {
            Id = Guid.NewGuid(),
            Date = ApprovedOnlySealDate,
            GroupId = null,
            Level = WorkLockLevel.Approved,
        });

        AddMembership(_memberClientId, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), null);
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = _sealedGroupId,
            ClientId = _memberClientId,
        });
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = _sealedGroupId,
            ShiftId = _sealedGroupShiftId,
        });

        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task IsClosed_ReturnsTrue_ForAnyClient_OnGloballySealedDate()
    {
        var lookup = await _filter.BuildAsync(FromDate, UntilDate, [_memberClientId, _nonMemberClientId]);

        lookup.IsClosed(_memberClientId, GloballySealedDate).ShouldBeTrue();
        lookup.IsClosed(_nonMemberClientId, GloballySealedDate).ShouldBeTrue();
    }

    [Test]
    public async Task IsClosed_ReturnsTrue_OnlyForGroupMember_OnGroupSealedDate()
    {
        var lookup = await _filter.BuildAsync(FromDate, UntilDate, [_memberClientId, _nonMemberClientId]);

        lookup.IsClosed(_memberClientId, GroupSealedDate).ShouldBeTrue();
        lookup.IsClosed(_nonMemberClientId, GroupSealedDate).ShouldBeFalse();
    }

    [Test]
    public async Task IsClosed_ReturnsFalse_OnDateWithoutSeal()
    {
        var lookup = await _filter.BuildAsync(FromDate, UntilDate, [_memberClientId]);

        lookup.IsClosed(_memberClientId, UnsealedDate).ShouldBeFalse();
    }

    [Test]
    public async Task IsClosed_ReturnsFalse_WhenSealedDayIsSoftDeleted()
    {
        var lookup = await _filter.BuildAsync(FromDate, UntilDate, [_memberClientId]);

        lookup.IsClosed(_memberClientId, SoftDeletedSealDate).ShouldBeFalse();
    }

    [Test]
    public async Task IsClosed_ReturnsFalse_WhenSealLevelIsNotClosed()
    {
        var lookup = await _filter.BuildAsync(FromDate, UntilDate, [_memberClientId]);

        lookup.IsClosed(_memberClientId, ApprovedOnlySealDate).ShouldBeFalse();
    }

    [Test]
    public async Task IsClosed_ReturnsFalse_WhenGroupMembershipIsSoftDeleted()
    {
        var deletedMemberClientId = Guid.NewGuid();
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = _sealedGroupId,
            ClientId = deletedMemberClientId,
            IsDeleted = true,
        });
        _context.SaveChanges();

        var lookup = await _filter.BuildAsync(FromDate, UntilDate, [deletedMemberClientId]);

        lookup.IsClosed(deletedMemberClientId, GroupSealedDate).ShouldBeFalse();
    }

    [Test]
    public async Task IsClosed_ReturnsFalse_ForSealedDateOutsideRequestedRange()
    {
        var lookup = await _filter.BuildAsync(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), [_memberClientId]);

        lookup.IsClosed(_memberClientId, GloballySealedDate).ShouldBeFalse();
    }

    [Test]
    public async Task IsClosed_ReturnsTrue_ForNonMember_WhoWorkedAShiftOfTheSealedGroup()
    {
        AddWork(_nonMemberClientId, GroupSealedDate, _sealedGroupShiftId);
        _context.SaveChanges();

        var lookup = await _filter.BuildAsync(FromDate, UntilDate, [_nonMemberClientId]);

        lookup.IsClosed(_nonMemberClientId, GroupSealedDate).ShouldBeTrue();
    }

    [Test]
    public async Task IsClosed_ReturnsFalse_ForNonMember_WhoWorkedAShiftOfAnotherGroup()
    {
        AddWork(_nonMemberClientId, GroupSealedDate, _otherShiftId);
        _context.SaveChanges();

        var lookup = await _filter.BuildAsync(FromDate, UntilDate, [_nonMemberClientId]);

        lookup.IsClosed(_nonMemberClientId, GroupSealedDate).ShouldBeFalse();
    }

    [Test]
    public async Task IsClosed_ReturnsFalse_ForNonMember_WhenTheGroupShiftWorkIsAScenarioOrDeleted()
    {
        AddWork(_nonMemberClientId, GroupSealedDate, _sealedGroupShiftId, analyseToken: Guid.NewGuid());
        AddWork(_nonMemberClientId, GroupSealedDate, _sealedGroupShiftId, isDeleted: true);
        _context.SaveChanges();

        var lookup = await _filter.BuildAsync(FromDate, UntilDate, [_nonMemberClientId]);

        lookup.IsClosed(_nonMemberClientId, GroupSealedDate).ShouldBeFalse();
    }

    [Test]
    public async Task IsClosed_ReturnsFalse_WhenMembershipEndedBeforeTheGroupSealedDate()
    {
        var formerMemberClientId = Guid.NewGuid();
        AddMembership(
            formerMemberClientId,
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 14, 0, 0, 0, DateTimeKind.Utc));
        AddGroupMember(formerMemberClientId, null, null);
        _context.SaveChanges();

        var lookup = await _filter.BuildAsync(FromDate, UntilDate, [formerMemberClientId]);

        lookup.IsClosed(formerMemberClientId, GroupSealedDate).ShouldBeFalse();
    }

    [Test]
    public async Task IsClosed_ReturnsFalse_WhenGroupItemValidityExcludesTheGroupSealedDate()
    {
        var laterMemberClientId = Guid.NewGuid();
        AddMembership(laterMemberClientId, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), null);
        AddGroupMember(laterMemberClientId, new DateTime(2026, 1, 16, 0, 0, 0, DateTimeKind.Utc), null);
        _context.SaveChanges();

        var lookup = await _filter.BuildAsync(FromDate, UntilDate, [laterMemberClientId]);

        lookup.IsClosed(laterMemberClientId, GroupSealedDate).ShouldBeFalse();
    }

    [Test]
    public async Task IsClosed_ReturnsTrue_WhenMembershipAndGroupItemValidityCoverTheGroupSealedDate()
    {
        var windowedMemberClientId = Guid.NewGuid();
        AddMembership(
            windowedMemberClientId,
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        AddGroupMember(
            windowedMemberClientId,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        _context.SaveChanges();

        var lookup = await _filter.BuildAsync(FromDate, UntilDate, [windowedMemberClientId]);

        lookup.IsClosed(windowedMemberClientId, GroupSealedDate).ShouldBeTrue();
    }

    [Test]
    public async Task IsClosed_ReturnsFalse_ForGroupItemWithoutMembership()
    {
        var groupItemOnlyClientId = Guid.NewGuid();
        AddGroupMember(groupItemOnlyClientId, null, null);
        _context.SaveChanges();

        var lookup = await _filter.BuildAsync(FromDate, UntilDate, [groupItemOnlyClientId]);

        lookup.IsClosed(groupItemOnlyClientId, GroupSealedDate).ShouldBeFalse();
    }

    private void AddMembership(Guid clientId, DateTime validFrom, DateTime? validUntil)
    {
        _context.Membership.Add(new Membership
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
        });
    }

    private void AddGroupMember(Guid clientId, DateTime? validFrom, DateTime? validUntil)
    {
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = _sealedGroupId,
            ClientId = clientId,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
        });
    }

    private void AddWork(
        Guid clientId,
        DateOnly date,
        Guid shiftId,
        Guid? analyseToken = null,
        bool isDeleted = false)
    {
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = date,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            LockLevel = WorkLockLevel.Closed,
            AnalyseToken = analyseToken,
            IsDeleted = isDeleted,
        });
    }
}
