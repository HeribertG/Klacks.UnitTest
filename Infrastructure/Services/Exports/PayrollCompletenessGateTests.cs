// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for PayrollCompletenessGate against an in-memory EF Core database: a complete person raises no blocker;
/// entries below Closed (also a break, also when another group's seal locks the day) raise EntryNotClosed with the
/// shift's group; days with an entry or an active membership that no Closed seal locks raise DayNotLocked (a day
/// approval of Level Approved never counts, a global seal locks everybody, a person without a group needs a global
/// close); an export of an overlapping period raises OverlappingExport; scenario and deleted rows, customers and
/// persons without entries are ignored; the client filter, the ordering and the 500 cap hold.
/// </summary>
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Exports;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Infrastructure.Repositories.Exports;
using Klacks.Api.Infrastructure.Services.Exports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class PayrollCompletenessGateTests
{
    private const string FormatDatev = "datev-lug-bewegungsdaten";
    private const int PersonsAboveCap = 510;

    private static readonly DateOnly From = new(2026, 3, 2);
    private static readonly DateOnly Until = new(2026, 3, 4);
    private static readonly DateOnly Day1 = From;
    private static readonly DateOnly Day2 = From.AddDays(1);
    private static readonly DateOnly Day3 = From.AddDays(2);

    private DataBaseContext _context = null!;
    private PayrollCompletenessGate _gate = null!;

    private Guid _groupA;
    private Guid _groupB;
    private Guid _shiftA;
    private Guid _shiftB;
    private Guid _shiftUngrouped;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _gate = new PayrollCompletenessGate(_context, new ExportLogItemRepository(_context));

        _groupA = AddGroup("Alpha");
        _groupB = AddGroup("Beta");
        _shiftA = AddShift(_groupA);
        _shiftB = AddShift(_groupB);
        _shiftUngrouped = Guid.NewGuid();
        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task CompletePerson_HasNoBlocker()
    {
        var person = AddMember(101, _groupA);
        AddWork(person, Day1, _shiftA, WorkLockLevel.Closed);
        SealPeriod(_groupA);
        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(From, Until, null, CancellationToken.None);

        result.IsComplete.ShouldBeTrue();
        result.Blockers.ShouldBeEmpty();
        result.BlockerTotal.ShouldBe(0);
    }

    [Test]
    public async Task OpenEndedMembership_IsClampedToThePeriod_AndNeverWalksOffTheCalendar()
    {
        var person = AddMember(105, _groupA);
        var lastDay = DateOnly.MaxValue;
        AddWork(person, lastDay, _shiftA, WorkLockLevel.Closed);
        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(lastDay, lastDay, null, CancellationToken.None);

        var blocker = result.Blockers.ShouldHaveSingleItem();
        blocker.Reason.ShouldBe(PayrollExportBlockReason.DayNotLocked);
        blocker.Date.ShouldBe(lastDay);
    }

    [Test]
    public async Task MembershipOfALongPastStart_RequiresOnlyTheDaysOfThePeriod()
    {
        var person = AddMember(106, _groupA);
        AddWork(person, Day1, _shiftA, WorkLockLevel.Closed);
        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(From, Until, null, CancellationToken.None);

        result.Blockers.Select(b => b.Date).ShouldBe(new DateOnly?[] { Day1, Day2, Day3 });
        result.Blockers.ShouldAllBe(b => b.Reason == PayrollExportBlockReason.DayNotLocked);
    }

    [Test]
    public async Task WorkBelowClosed_RaisesEntryNotClosed_WithTheGroupOfTheShift()
    {
        var person = AddClient(102);
        AddWork(person, Day2, _shiftA, WorkLockLevel.Approved);
        SealPeriod(null);
        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(From, Until, null, CancellationToken.None);

        var blocker = result.Blockers.ShouldHaveSingleItem();
        blocker.Reason.ShouldBe(PayrollExportBlockReason.EntryNotClosed);
        blocker.ClientId.ShouldBe(person);
        blocker.IdNumber.ShouldBe(102);
        blocker.Date.ShouldBe(Day2);
        blocker.GroupId.ShouldBe(_groupA);
        blocker.GroupName.ShouldBe("Alpha");
        blocker.RequiresGlobalClose.ShouldBeFalse();
        blocker.EntryCount.ShouldBe(1);
    }

    [Test]
    public async Task WorkOfAShiftInTwoGroups_NamesTheFirstGroupByName()
    {
        var shiftInBoth = AddShift(_groupB);
        _context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = _groupA, ShiftId = shiftInBoth });
        var person = AddClient(103);
        AddWork(person, Day1, shiftInBoth, WorkLockLevel.Confirmed);
        SealPeriod(null);
        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(From, Until, null, CancellationToken.None);

        result.Blockers.ShouldHaveSingleItem().GroupName.ShouldBe("Alpha");
    }

    [Test]
    public async Task SeveralOpenEntriesOnOneDay_RaiseOneBlockerWithTheCount()
    {
        var person = AddClient(104);
        AddWork(person, Day1, _shiftA, WorkLockLevel.None);
        AddWork(person, Day1, _shiftA, WorkLockLevel.Approved);
        AddBreak(person, Day1, WorkLockLevel.None);
        AddWork(person, Day1, _shiftA, WorkLockLevel.Closed);
        SealPeriod(null);
        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(From, Until, null, CancellationToken.None);

        var blocker = result.Blockers.ShouldHaveSingleItem();
        blocker.Reason.ShouldBe(PayrollExportBlockReason.EntryNotClosed);
        blocker.EntryCount.ShouldBe(3);
    }

    [Test]
    public async Task BreakBelowClosed_RaisesEntryNotClosed_WithoutGroup()
    {
        var person = AddClient(105);
        AddBreak(person, Day3, WorkLockLevel.Approved);
        SealPeriod(null);
        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(From, Until, null, CancellationToken.None);

        var blocker = result.Blockers.ShouldHaveSingleItem();
        blocker.Reason.ShouldBe(PayrollExportBlockReason.EntryNotClosed);
        blocker.Date.ShouldBe(Day3);
        blocker.GroupId.ShouldBeNull();
        blocker.GroupName.ShouldBeNull();
        blocker.RequiresGlobalClose.ShouldBeFalse();
    }

    [Test]
    public async Task DayWithAnEntryThatNoSealLocks_RaisesDayNotLocked_WithTheShiftGroup()
    {
        var person = AddClient(106);
        AddWork(person, Day2, _shiftA, WorkLockLevel.Closed);
        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(From, Until, null, CancellationToken.None);

        var blocker = result.Blockers.ShouldHaveSingleItem();
        blocker.Reason.ShouldBe(PayrollExportBlockReason.DayNotLocked);
        blocker.Date.ShouldBe(Day2);
        blocker.GroupId.ShouldBe(_groupA);
        blocker.RequiresGlobalClose.ShouldBeFalse();
        blocker.EntryCount.ShouldBe(1);
    }

    [Test]
    public async Task PersonWithoutGroupAndWithoutGlobalSeal_RequiresGlobalClose()
    {
        var person = AddClient(107);
        AddWork(person, Day2, _shiftUngrouped, WorkLockLevel.Closed);
        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(From, Until, null, CancellationToken.None);

        var blocker = result.Blockers.ShouldHaveSingleItem();
        blocker.Reason.ShouldBe(PayrollExportBlockReason.DayNotLocked);
        blocker.GroupId.ShouldBeNull();
        blocker.GroupName.ShouldBeNull();
        blocker.RequiresGlobalClose.ShouldBeTrue();
    }

    [Test]
    public async Task EmptyMembershipDayWithoutSeal_RaisesDayNotLocked_WithTheMembershipGroup()
    {
        var person = AddMember(108, _groupA);
        AddWork(person, Day1, _shiftA, WorkLockLevel.Closed);
        AddSeal(Day1, _groupA, WorkLockLevel.Closed);
        AddSeal(Day2, _groupA, WorkLockLevel.Closed);
        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(From, Until, null, CancellationToken.None);

        var blocker = result.Blockers.ShouldHaveSingleItem();
        blocker.Reason.ShouldBe(PayrollExportBlockReason.DayNotLocked);
        blocker.Date.ShouldBe(Day3);
        blocker.GroupId.ShouldBe(_groupA);
        blocker.GroupName.ShouldBe("Alpha");
        blocker.RequiresGlobalClose.ShouldBeFalse();
        blocker.EntryCount.ShouldBe(0);
    }

    [Test]
    public async Task MembershipStartingInThePeriod_OnlyRequiresTheActiveDays()
    {
        var person = AddClient(109);
        _context.Membership.Add(new Membership
        {
            Id = Guid.NewGuid(),
            ClientId = person,
            ValidFrom = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc),
        });
        _context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = _groupA, ClientId = person });
        AddWork(person, Day1, _shiftA, WorkLockLevel.Closed);
        AddSeal(Day1, _groupA, WorkLockLevel.Closed);
        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(From, Until, null, CancellationToken.None);

        result.Blockers.ShouldHaveSingleItem().Date.ShouldBe(Until);
    }

    [Test]
    public async Task ApprovedOnlyDayApproval_DoesNotCountAsSealed()
    {
        var person = AddMember(110, _groupA);
        AddWork(person, Day1, _shiftA, WorkLockLevel.Closed);
        foreach (var day in new[] { Day1, Day2, Day3 })
        {
            AddSeal(day, _groupA, WorkLockLevel.Approved);
        }

        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(From, Until, null, CancellationToken.None);

        result.BlockerTotal.ShouldBe(3);
        result.Blockers.ShouldAllBe(b => b.Reason == PayrollExportBlockReason.DayNotLocked);
        result.Blockers.Select(b => b.Date).ShouldBe(new DateOnly?[] { Day1, Day2, Day3 });
    }

    [Test]
    public async Task ApprovedGlobalSeal_DoesNotCountAsSealed()
    {
        var person = AddClient(111);
        AddWork(person, Day1, _shiftUngrouped, WorkLockLevel.Closed);
        AddSeal(Day1, null, WorkLockLevel.Approved);
        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(From, Until, null, CancellationToken.None);

        result.Blockers.ShouldHaveSingleItem().Reason.ShouldBe(PayrollExportBlockReason.DayNotLocked);
    }

    [Test]
    public async Task GlobalClosedSeal_LocksEveryone_IncludingAPersonWithoutGroup()
    {
        var ungrouped = AddClient(112);
        var member = AddMember(113, _groupB);
        AddWork(ungrouped, Day1, _shiftUngrouped, WorkLockLevel.Closed);
        AddBreak(ungrouped, Day2, WorkLockLevel.Closed);
        AddWork(member, Day3, _shiftB, WorkLockLevel.Closed);
        SealPeriod(null);
        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(From, Until, null, CancellationToken.None);

        result.IsComplete.ShouldBeTrue();
    }

    [Test]
    public async Task GroupSealOfTheWorkedShift_LocksANonMember()
    {
        var person = AddClient(114);
        AddWork(person, Day1, _shiftA, WorkLockLevel.Closed);
        AddSeal(Day1, _groupA, WorkLockLevel.Closed);
        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(From, Until, null, CancellationToken.None);

        result.IsComplete.ShouldBeTrue();
    }

    [Test]
    public async Task DayLockedByGroupA_ButWorkOfGroupBStillApproved_RaisesEntryNotClosed()
    {
        var person = AddMember(115, _groupA);
        AddWork(person, Day1, _shiftB, WorkLockLevel.Approved);
        SealPeriod(_groupA);
        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(From, Until, null, CancellationToken.None);

        var blocker = result.Blockers.ShouldHaveSingleItem();
        blocker.Reason.ShouldBe(PayrollExportBlockReason.EntryNotClosed);
        blocker.GroupId.ShouldBe(_groupB);
        blocker.GroupName.ShouldBe("Beta");
    }

    [Test]
    public async Task ScenarioDeletedAndCustomerRows_AreIgnored()
    {
        var scenarioPerson = AddClient(116);
        AddWork(scenarioPerson, Day1, _shiftA, WorkLockLevel.None, analyseToken: Guid.NewGuid());
        AddBreak(scenarioPerson, Day1, WorkLockLevel.None, analyseToken: Guid.NewGuid());
        var deletedPerson = AddClient(117);
        AddWork(deletedPerson, Day1, _shiftA, WorkLockLevel.None, isDeleted: true);
        var customer = AddClient(118, EntityTypeEnum.Customer);
        AddWork(customer, Day1, _shiftA, WorkLockLevel.None);
        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(From, Until, null, CancellationToken.None);

        result.IsComplete.ShouldBeTrue();
    }

    [Test]
    public async Task ScenarioMembershipAndScenarioShiftGroup_AreNotUsed()
    {
        var person = AddClient(119);
        _context.Membership.Add(new Membership
        {
            Id = Guid.NewGuid(),
            ClientId = person,
            ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = _groupA,
            ClientId = person,
            AnalyseToken = Guid.NewGuid(),
        });
        var scenarioShift = Guid.NewGuid();
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = _groupA,
            ShiftId = scenarioShift,
            ScenarioSourceGroupItemId = Guid.NewGuid(),
        });
        AddWork(person, Day1, scenarioShift, WorkLockLevel.Closed);
        AddSeal(Day1, _groupA, WorkLockLevel.Closed);
        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(From, Until, null, CancellationToken.None);

        var blocker = result.Blockers.ShouldHaveSingleItem();
        blocker.Reason.ShouldBe(PayrollExportBlockReason.DayNotLocked);
        blocker.Date.ShouldBe(Day1);
        blocker.RequiresGlobalClose.ShouldBeTrue();
    }

    [Test]
    public async Task PersonWithoutEntries_IsIgnored_EvenWithAnUnsealedMembership()
    {
        AddMember(120, _groupA);
        var outsidePeriod = AddMember(121, _groupA);
        AddWork(outsidePeriod, Until.AddDays(5), _shiftA, WorkLockLevel.None);
        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(From, Until, null, CancellationToken.None);

        result.IsComplete.ShouldBeTrue();
    }

    [Test]
    public async Task ClientIdFilter_ChecksOnlyTheGivenPersons()
    {
        var first = AddClient(122);
        var second = AddClient(123);
        AddWork(first, Day1, _shiftA, WorkLockLevel.Approved);
        AddWork(second, Day1, _shiftA, WorkLockLevel.Approved);
        SealPeriod(null);
        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(From, Until, [second], CancellationToken.None);

        result.Blockers.ShouldHaveSingleItem().ClientId.ShouldBe(second);
    }

    [Test]
    public async Task ExportOfAnOverlappingPeriod_RaisesOverlappingExport_PerPerson()
    {
        var person = AddMember(124, _groupA);
        var other = AddMember(125, _groupA);
        AddWork(person, Day1, _shiftA, WorkLockLevel.Closed);
        AddWork(other, Day1, _shiftA, WorkLockLevel.Closed);
        SealPeriod(_groupA);
        _context.ExportLogItem.Add(NewItem(person, new DateOnly(2026, 2, 20), new DateOnly(2026, 3, 3)));
        _context.ExportLogItem.Add(NewItem(person, new DateOnly(2026, 3, 4), new DateOnly(2026, 3, 20)));
        _context.ExportLogItem.Add(NewItem(other, From, Until));
        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(From, Until, null, CancellationToken.None);

        var blocker = result.Blockers.ShouldHaveSingleItem();
        blocker.Reason.ShouldBe(PayrollExportBlockReason.OverlappingExport);
        blocker.ClientId.ShouldBe(person);
        blocker.Date.ShouldBeNull();
        blocker.GroupId.ShouldBeNull();
        blocker.EntryCount.ShouldBe(0);
    }

    [Test]
    public async Task Blockers_AreOrderedByPersonName_ThenDay_ThenReason()
    {
        var zed = AddClient(126, name: "Zed");
        var abe = AddClient(127, name: "Abe");
        AddWork(zed, Day1, _shiftUngrouped, WorkLockLevel.Closed);
        AddWork(abe, Day2, _shiftUngrouped, WorkLockLevel.Approved);
        AddWork(abe, Day1, _shiftUngrouped, WorkLockLevel.Approved);
        _context.ExportLogItem.Add(NewItem(abe, new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 2)));
        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(From, Until, null, CancellationToken.None);

        result.Blockers
            .Select(b => (b.ClientId, b.Date, b.Reason))
            .ShouldBe(new (Guid, DateOnly?, PayrollExportBlockReason)[]
            {
                (abe, null, PayrollExportBlockReason.OverlappingExport),
                (abe, Day1, PayrollExportBlockReason.EntryNotClosed),
                (abe, Day1, PayrollExportBlockReason.DayNotLocked),
                (abe, Day2, PayrollExportBlockReason.EntryNotClosed),
                (abe, Day2, PayrollExportBlockReason.DayNotLocked),
                (zed, Day1, PayrollExportBlockReason.DayNotLocked),
            });
    }

    [Test]
    public async Task BlockerList_IsCappedAt500_WhileTheTotalCountsAll()
    {
        for (var i = 0; i < PersonsAboveCap; i++)
        {
            var person = AddClient(1000 + i);
            AddWork(person, Day1, _shiftUngrouped, WorkLockLevel.Approved);
        }

        SealPeriod(null);
        await _context.SaveChangesAsync();

        var result = await _gate.CheckAsync(From, Until, null, CancellationToken.None);

        PayrollExportConstants.MaxReportedBlockers.ShouldBe(500);
        result.Blockers.Count.ShouldBe(PayrollExportConstants.MaxReportedBlockers);
        result.BlockerTotal.ShouldBe(PersonsAboveCap);
        result.IsComplete.ShouldBeFalse();
    }

    private Guid AddGroup(string name)
    {
        var id = Guid.NewGuid();
        _context.Group.Add(new Group { Id = id, Name = name });
        return id;
    }

    private Guid AddShift(Guid groupId)
    {
        var shiftId = Guid.NewGuid();
        _context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = shiftId });
        return shiftId;
    }

    private Guid AddClient(int idNumber, EntityTypeEnum type = EntityTypeEnum.Employee, string? name = null)
    {
        var clientId = Guid.NewGuid();
        _context.Client.Add(new Client
        {
            Id = clientId,
            Type = type,
            IdNumber = idNumber,
            Name = name ?? $"Employee{idNumber}",
            FirstName = "Test",
        });
        return clientId;
    }

    private Guid AddMember(int idNumber, Guid groupId)
    {
        var clientId = AddClient(idNumber);
        _context.Membership.Add(new Membership
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ClientId = clientId });
        return clientId;
    }

    private void AddWork(
        Guid clientId,
        DateOnly date,
        Guid shiftId,
        WorkLockLevel lockLevel,
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
            LockLevel = lockLevel,
            AnalyseToken = analyseToken,
            IsDeleted = isDeleted,
        });
    }

    private void AddBreak(Guid clientId, DateOnly date, WorkLockLevel lockLevel, Guid? analyseToken = null)
    {
        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AbsenceId = Guid.NewGuid(),
            CurrentDate = date,
            StartTime = new TimeOnly(0, 0),
            EndTime = new TimeOnly(23, 59),
            WorkTime = 8m,
            LockLevel = lockLevel,
            AnalyseToken = analyseToken,
        });
    }

    private void AddSeal(DateOnly date, Guid? groupId, WorkLockLevel level)
    {
        _context.SealedDay.Add(new SealedDay
        {
            Id = Guid.NewGuid(),
            Date = date,
            GroupId = groupId,
            Level = level,
            SealedAt = DateTime.UtcNow,
            SealedBy = "tester",
        });
    }

    private void SealPeriod(Guid? groupId)
    {
        for (var day = From; day <= Until; day = day.AddDays(1))
        {
            AddSeal(day, groupId, WorkLockLevel.Closed);
        }
    }

    private static ExportLogItem NewItem(Guid clientId, DateOnly start, DateOnly end)
    {
        return new ExportLogItem
        {
            Id = Guid.NewGuid(),
            ExportLogId = Guid.NewGuid(),
            ClientId = clientId,
            StartDate = start,
            EndDate = end,
            Format = FormatDatev,
            Revision = 1,
            ContentHash = Guid.NewGuid().ToString("N"),
            EntryCount = 1,
            EntriesJson = "[]",
        };
    }
}
