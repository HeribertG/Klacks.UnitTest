// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the person-based PayrollExportDataLoader against an in-memory EF Core database: every person with
/// closed entries is exported exactly once whatever groups they belong to (or none), employees and external
/// employees are included, customers, scenario, soft-deleted and unsealed entries stay out, a break is exported once,
/// and an on-call absence is one day. WorkChanges follow the period-hours rules: corrections, travel and the
/// briefing rows of the container expansion add hours, a replacement moves hours from the work's employee to the
/// replacing person; break surcharges count; a soft-deleted person's closed entries are still exported; a container child
/// of the container's own person adds nothing of its own.
/// </summary>
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Infrastructure.Services.Exports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using System.Text;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class PayrollExportDataLoaderTests
{
    private static readonly DateOnly FromDate = new(2026, 1, 1);
    private static readonly DateOnly UntilDate = new(2026, 1, 31);
    private static readonly DateOnly OnCallSunday = new(2026, 1, 18);
    private static readonly DateOnly VacationMonday = new(2026, 1, 19);
    private static readonly DateOnly WorkDay = new(2026, 1, 20);
    private static readonly DateOnly OutsidePeriodDay = new(2026, 2, 3);

    private DataBaseContext _context = null!;
    private PayrollExportDataLoader _loader = null!;

    private readonly Guid _groupAId = Guid.NewGuid();
    private readonly Guid _groupBId = Guid.NewGuid();
    private readonly Guid _shiftAId = Guid.NewGuid();
    private readonly Guid _shiftBId = Guid.NewGuid();
    private readonly Guid _onCallAbsenceId = Guid.NewGuid();
    private readonly Guid _vacationAbsenceId = Guid.NewGuid();

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _loader = new PayrollExportDataLoader(_context);

        _context.Absence.Add(NewAbsence(_onCallAbsenceId, "PIK", isOnCall: true));
        _context.Absence.Add(NewAbsence(_vacationAbsenceId, "FER", isOnCall: false));
        _context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = _groupAId, ShiftId = _shiftAId });
        _context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = _groupBId, ShiftId = _shiftBId });
        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task LoadAsync_PersonWithWorkInTwoGroups_IsExportedOnceWithSummedHours()
    {
        var clientId = AddClient(101);
        AddWork(clientId, WorkDay, _shiftAId, workTime: 8m);
        AddWork(clientId, WorkDay, _shiftBId, workTime: 2m);
        AddWork(clientId, WorkDay.AddDays(1), _shiftBId, workTime: 4m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        var employee = data.Employees.ShouldHaveSingleItem();
        employee.ClientId.ShouldBe(clientId);
        employee.Entries.Count(e => e.Kind == PayrollEntryKind.WorkHours).ShouldBe(2);
        employee.Entries.Single(e => e.Date == WorkDay && e.Kind == PayrollEntryKind.WorkHours).Quantity.ShouldBe(10m);
        employee.Entries.Single(e => e.Date == WorkDay.AddDays(1)).Quantity.ShouldBe(4m);
    }

    [Test]
    public async Task LoadAsync_AContainerChildOfTheSamePerson_AddsNoHoursOrSurchargesOfItsOwn()
    {
        var clientId = AddClient(110);
        var containerId = AddWork(clientId, WorkDay, _shiftAId, workTime: 8m);
        var childId = AddWork(clientId, WorkDay, _shiftBId, workTime: 3m);
        await _context.SaveChangesAsync();
        var child = await _context.Work.SingleAsync(w => w.Id == childId);
        child.ParentWorkId = containerId;
        child.Surcharges = 1m;
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        var employee = data.Employees.ShouldHaveSingleItem();
        employee.Entries.Single(e => e.Date == WorkDay && e.Kind == PayrollEntryKind.WorkHours).Quantity.ShouldBe(8m);
        employee.Entries.ShouldNotContain(e => e.Kind == PayrollEntryKind.Surcharge);
    }

    [Test]
    public async Task LoadAsync_TravelTimeOnAnInnerServiceOfAContainer_AddsNoHours()
    {
        var clientId = AddClient(111);
        var containerId = AddWork(clientId, WorkDay, _shiftAId, workTime: 8m);
        var childId = AddWork(clientId, WorkDay, _shiftBId, workTime: 3m);
        AddWorkChange(childId, WorkChangeType.CorrectionStart, changeTime: 1m, description: "travel_time_before");
        await _context.SaveChangesAsync();
        var child = await _context.Work.SingleAsync(w => w.Id == childId);
        child.ParentWorkId = containerId;
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        data.Employees.ShouldHaveSingleItem().Entries
            .Single(e => e.Date == WorkDay && e.Kind == PayrollEntryKind.WorkHours).Quantity.ShouldBe(8m);
    }

    [Test]
    public async Task LoadAsync_PersonWithoutAnyGroup_IsIncluded()
    {
        var clientId = AddClient(102);
        AddWork(clientId, WorkDay, Guid.NewGuid(), workTime: 8m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        data.Employees.ShouldHaveSingleItem().ClientId.ShouldBe(clientId);
    }

    [Test]
    public async Task LoadAsync_ProvidesStructuredFirstAndLastName()
    {
        var clientId = AddClient(103, name: "  Muster, Meier ");
        AddWork(clientId, WorkDay, Guid.NewGuid(), workTime: 8m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        var employee = data.Employees.ShouldHaveSingleItem();
        employee.LastName.ShouldBe("Muster, Meier");
        employee.FirstName.ShouldBe("Test");
        employee.FullName.ShouldBe("Muster, Meier, Test");
    }

    [Test]
    public async Task LoadAsync_ExternEmp_IsIncluded()
    {
        var clientId = AddClient(103, EntityTypeEnum.ExternEmp);
        AddWork(clientId, WorkDay, _shiftAId, workTime: 8m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        data.Employees.ShouldHaveSingleItem().ClientId.ShouldBe(clientId);
    }

    [Test]
    public async Task LoadAsync_CustomerClient_IsNotExported()
    {
        var customerId = AddClient(104, EntityTypeEnum.Customer);
        AddWork(customerId, WorkDay, _shiftAId, workTime: 8m);
        AddBreak(customerId, _vacationAbsenceId, VacationMonday, workTime: 8m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        data.Employees.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadAsync_ScenarioWork_IsNotExported()
    {
        var clientId = AddClient(105);
        AddWork(clientId, WorkDay, _shiftAId, workTime: 8m, analyseToken: Guid.NewGuid());
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        data.Employees.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadAsync_NotClosedWork_IsNotExported()
    {
        var clientId = AddClient(106);
        AddWork(clientId, WorkDay, _shiftAId, workTime: 8m, lockLevel: WorkLockLevel.Approved);
        AddWork(clientId, WorkDay.AddDays(1), _shiftAId, workTime: 8m, lockLevel: WorkLockLevel.None);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        data.Employees.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadAsync_SoftDeletedWork_IsNotExported()
    {
        var clientId = AddClient(107);
        AddWork(clientId, WorkDay, _shiftAId, workTime: 8m, isDeleted: true);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        data.Employees.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadAsync_WorkOutsidePeriod_IsNotExported()
    {
        var clientId = AddClient(108);
        AddWork(clientId, OutsidePeriodDay, _shiftAId, workTime: 8m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        data.Employees.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadAsync_BreakOfPersonInTwoGroups_IsExportedOnce()
    {
        var clientId = AddClient(109);
        AddMembership(clientId);
        AddGroupMembership(clientId, _groupAId);
        AddGroupMembership(clientId, _groupBId);
        AddBreak(clientId, _vacationAbsenceId, VacationMonday, workTime: 8.4m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        var entry = data.Employees.ShouldHaveSingleItem().Entries.ShouldHaveSingleItem();
        entry.Kind.ShouldBe(PayrollEntryKind.Absence);
        entry.AbsenceId.ShouldBe(_vacationAbsenceId);
        entry.Quantity.ShouldBe(8.4m);
        entry.Unit.ShouldBe(PayrollQuantityUnit.Hours);
    }

    [Test]
    public async Task LoadAsync_BreakOfPersonWithoutGroup_IsExported()
    {
        var clientId = AddClient(110);
        AddBreak(clientId, _vacationAbsenceId, VacationMonday, workTime: 8m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        data.Employees.ShouldHaveSingleItem().Entries.ShouldHaveSingleItem().Quantity.ShouldBe(8m);
    }

    [Test]
    public async Task LoadAsync_OnCallSundayWithoutWork_IsExportedAsOneDay()
    {
        var clientId = AddClient(111);
        AddBreak(clientId, _onCallAbsenceId, OnCallSunday, workTime: 0m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        var employee = data.Employees.ShouldHaveSingleItem();
        employee.ClientId.ShouldBe(clientId);
        var entry = employee.Entries.ShouldHaveSingleItem();
        entry.Kind.ShouldBe(PayrollEntryKind.Absence);
        entry.Date.ShouldBe(OnCallSunday);
        entry.AbsenceId.ShouldBe(_onCallAbsenceId);
        entry.Quantity.ShouldBe(1m);
        entry.Unit.ShouldBe(PayrollQuantityUnit.Days);
    }

    [Test]
    public async Task LoadAsync_TwoOnCallBreaksOnSameDay_CountAsOneDay()
    {
        var clientId = AddClient(112);
        AddBreak(clientId, _onCallAbsenceId, OnCallSunday, workTime: 0m);
        AddBreak(clientId, _onCallAbsenceId, OnCallSunday, workTime: 0m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        var entry = data.Employees.ShouldHaveSingleItem().Entries.ShouldHaveSingleItem();
        entry.Quantity.ShouldBe(1m);
        entry.Unit.ShouldBe(PayrollQuantityUnit.Days);
    }

    [Test]
    public async Task LoadAsync_WorkAndBreakOnSameDay_KeepBoth()
    {
        var clientId = AddClient(113);
        AddWork(clientId, WorkDay, _shiftAId, workTime: 8m);
        AddBreak(clientId, _vacationAbsenceId, WorkDay, workTime: 2m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        var employee = data.Employees.ShouldHaveSingleItem();
        employee.Entries.Count(e => e.Kind == PayrollEntryKind.WorkHours).ShouldBe(1);
        employee.Entries.Single(e => e.Kind == PayrollEntryKind.Absence).Quantity.ShouldBe(2m);
    }

    [Test]
    public async Task LoadAsync_SoftDeletedBreak_IsNotExported()
    {
        var clientId = AddClient(114);
        AddBreak(clientId, _onCallAbsenceId, OnCallSunday, workTime: 0m, isDeleted: true);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        data.Employees.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadAsync_ScenarioBreak_IsNotExported()
    {
        var clientId = AddClient(115);
        AddBreak(clientId, _onCallAbsenceId, OnCallSunday, workTime: 0m, analyseToken: Guid.NewGuid());
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        data.Employees.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadAsync_UnsealedBreak_IsNotExported()
    {
        var clientId = AddClient(116);
        AddBreak(clientId, _onCallAbsenceId, OnCallSunday, workTime: 0m, lockLevel: WorkLockLevel.None);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        data.Employees.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadAsync_ClientIdFilter_RestrictsToTheGivenPersons()
    {
        var wanted = AddClient(117);
        var other = AddClient(118);
        AddWork(wanted, WorkDay, _shiftAId, workTime: 8m);
        AddWork(other, WorkDay, _shiftAId, workTime: 8m);
        AddBreak(other, _vacationAbsenceId, VacationMonday, workTime: 8m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, [wanted]);

        data.Employees.ShouldHaveSingleItem().ClientId.ShouldBe(wanted);
    }

    [Test]
    public async Task LoadAsync_EmptyClientIdFilter_ExportsNobody()
    {
        var clientId = AddClient(119);
        AddWork(clientId, WorkDay, _shiftAId, workTime: 8m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, []);

        data.Employees.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadAsync_PersonsAreOrderedByNameThenId()
    {
        var zed = AddClient(120, name: "Zed");
        var ann = AddClient(121, name: "Ann");
        AddWork(zed, WorkDay, _shiftAId, workTime: 8m);
        AddWork(ann, WorkDay, _shiftAId, workTime: 8m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        data.Employees.Select(e => e.ClientId).ShouldBe([ann, zed]);
    }

    [Test]
    public async Task LoadAsync_OnCallSunday_FlowsIntoDatevLugAsTagesanzahl()
    {
        var clientId = AddClient(101);
        AddBreak(clientId, _onCallAbsenceId, OnCallSunday, workTime: 0m);
        await _context.SaveChangesAsync();
        var config = new PayrollExportGroupConfig
        {
            TargetSystem = PayrollExportConstants.FormatKeyDatevLug,
            Delimiter = PayrollExportConstants.DefaultDelimiter,
            Encoding = PayrollExportConstants.DefaultEncoding,
            AbsenceMappingJson = $"{{\"{_onCallAbsenceId}\":{{\"ausfallschluessel\":\"\",\"wageType\":\"4711\"}}}}",
        };

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);
        var result = new DatevLugBewegungsdatenFormatter().Format(data, config);

        var content = Encoding.GetEncoding(PayrollExportConstants.Windows1252CodePage).GetString(result.Content);
        content.ShouldBe(";;01/2026\r\n101;;;4711;;;1,00;;;;\r\n");
        result.SkippedAbsenceCount.ShouldBe(0);
    }

    [Test]
    public async Task LoadAsync_CorrectionOnClosedWork_AddsItsHoursToTheDay()
    {
        var clientId = AddClient(130);
        var workId = AddWork(clientId, WorkDay, _shiftAId, workTime: 8m);
        AddWorkChange(workId, WorkChangeType.CorrectionEnd, 1.5m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        var entry = data.Employees.ShouldHaveSingleItem().Entries.ShouldHaveSingleItem();
        entry.Kind.ShouldBe(PayrollEntryKind.WorkHours);
        entry.Quantity.ShouldBe(9.5m);
    }

    [Test]
    public async Task LoadAsync_TravelOnClosedWork_AddsItsHoursAndSurcharges()
    {
        var clientId = AddClient(131);
        var workId = AddWork(clientId, WorkDay, _shiftAId, workTime: 8m);
        AddWorkChange(workId, WorkChangeType.TravelStart, 0.5m, surcharges: 0.25m);
        AddWorkChange(workId, WorkChangeType.TravelWithin, 0.25m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        var employee = data.Employees.ShouldHaveSingleItem();
        employee.Entries.Single(e => e.Kind == PayrollEntryKind.WorkHours).Quantity.ShouldBe(8.75m);
        employee.Entries.Single(e => e.Kind == PayrollEntryKind.Surcharge).Quantity.ShouldBe(0.25m);
    }

    [Test]
    public async Task LoadAsync_ContainerBriefingRows_CountLikeThePeriodHours()
    {
        var clientId = AddClient(132);
        var workId = AddWork(clientId, WorkDay, _shiftAId, workTime: 8m);
        AddWorkChange(workId, WorkChangeType.CorrectionStart, 0.5m, description: "briefing_time");
        AddWorkChange(workId, WorkChangeType.CorrectionEnd, 0.25m, description: "debriefing_time");
        AddWorkChange(workId, WorkChangeType.Briefing, 3m);
        AddWorkChange(workId, WorkChangeType.Debriefing, 3m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        data.Employees.ShouldHaveSingleItem().Entries.ShouldHaveSingleItem().Quantity.ShouldBe(8.75m);
    }

    [Test]
    public async Task LoadAsync_Replacement_MovesTheHoursToTheReplacingPersonOnThatDay()
    {
        var original = AddClient(133, name: "Aaa");
        var substitute = AddClient(134, EntityTypeEnum.ExternEmp, name: "Bbb");
        var workId = AddWork(original, WorkDay, _shiftAId, workTime: 8m);
        AddWorkChange(workId, WorkChangeType.ReplacementStart, 3m, replaceClientId: substitute, surcharges: 1m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        data.Employees.Select(e => e.ClientId).ShouldBe([original, substitute]);
        var originalEntries = data.Employees[0].Entries;
        originalEntries.Single(e => e.Kind == PayrollEntryKind.WorkHours).Quantity.ShouldBe(5m);
        originalEntries.Single(e => e.Kind == PayrollEntryKind.Surcharge).Quantity.ShouldBe(-1m);
        var substituteEmployee = data.Employees[1];
        substituteEmployee.IdNumber.ShouldBe(134);
        var hours = substituteEmployee.Entries.Single(e => e.Kind == PayrollEntryKind.WorkHours);
        hours.Date.ShouldBe(WorkDay);
        hours.Quantity.ShouldBe(3m);
        substituteEmployee.Entries.Single(e => e.Kind == PayrollEntryKind.Surcharge).Quantity.ShouldBe(1m);
    }

    [Test]
    public async Task LoadAsync_ReplacementWithClientFilterOnTheSubstitute_ExportsOnlyTheSubstitute()
    {
        var original = AddClient(135);
        var substitute = AddClient(136);
        var workId = AddWork(original, WorkDay, _shiftAId, workTime: 8m);
        AddWorkChange(workId, WorkChangeType.ReplacementWithin, 2m, replaceClientId: substitute);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, [substitute]);

        var employee = data.Employees.ShouldHaveSingleItem();
        employee.ClientId.ShouldBe(substitute);
        employee.Entries.ShouldHaveSingleItem().Quantity.ShouldBe(2m);
    }

    [Test]
    public async Task LoadAsync_WorkChangesOfUnclosedScenarioOrDeletedRows_AreIgnored()
    {
        var clientId = AddClient(137);
        var substitute = AddClient(138);
        var closedWorkId = AddWork(clientId, WorkDay, _shiftAId, workTime: 8m);
        var openWorkId = AddWork(clientId, WorkDay.AddDays(1), _shiftAId, workTime: 8m, lockLevel: WorkLockLevel.Approved);
        AddWorkChange(openWorkId, WorkChangeType.ReplacementStart, 2m, replaceClientId: substitute);
        AddWorkChange(closedWorkId, WorkChangeType.CorrectionEnd, 1m, isDeleted: true);
        AddWorkChange(closedWorkId, WorkChangeType.CorrectionEnd, 1m, analyseToken: Guid.NewGuid());
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        var employee = data.Employees.ShouldHaveSingleItem();
        employee.ClientId.ShouldBe(clientId);
        employee.Entries.ShouldHaveSingleItem().Quantity.ShouldBe(8m);
    }

    [Test]
    public async Task LoadAsync_BreakSurcharges_AreAddedToTheDaySurcharge()
    {
        var clientId = AddClient(139);
        AddBreak(clientId, _vacationAbsenceId, VacationMonday, workTime: 8m, surcharges: 0.5m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        var employee = data.Employees.ShouldHaveSingleItem();
        employee.Entries.Single(e => e.Kind == PayrollEntryKind.Absence).Quantity.ShouldBe(8m);
        employee.Entries.Single(e => e.Kind == PayrollEntryKind.Surcharge).Quantity.ShouldBe(0.5m);
        employee.Entries.ShouldNotContain(e => e.Kind == PayrollEntryKind.WorkHours);
    }

    [Test]
    public async Task LoadAsync_SoftDeletedPerson_ClosedEntriesAreStillExportedWithNameAndIdNumber()
    {
        var clientId = AddClient(140, name: "Gone", isDeleted: true);
        AddWork(clientId, WorkDay, _shiftAId, workTime: 8m);
        AddBreak(clientId, _vacationAbsenceId, VacationMonday, workTime: 4m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        var employee = data.Employees.ShouldHaveSingleItem();
        employee.ClientId.ShouldBe(clientId);
        employee.IdNumber.ShouldBe(140);
        employee.LastName.ShouldBe("Gone");
        employee.Entries.Single(e => e.Kind == PayrollEntryKind.WorkHours).Quantity.ShouldBe(8m);
        employee.Entries.Single(e => e.Kind == PayrollEntryKind.Absence).Quantity.ShouldBe(4m);
    }

    [Test]
    public async Task LoadAsync_SoftDeletedPerson_DeletedAndUnclosedEntriesStayOut()
    {
        var clientId = AddClient(141, isDeleted: true);
        AddWork(clientId, WorkDay, _shiftAId, workTime: 8m, isDeleted: true);
        AddWork(clientId, WorkDay.AddDays(1), _shiftAId, workTime: 8m, lockLevel: WorkLockLevel.Approved);
        AddWork(clientId, WorkDay.AddDays(2), _shiftAId, workTime: 8m, analyseToken: Guid.NewGuid());
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate, null);

        data.Employees.ShouldBeEmpty();
    }

    private static Absence NewAbsence(Guid id, string abbreviation, bool isOnCall)
    {
        return new Absence
        {
            Id = id,
            Name = new MultiLanguage { De = abbreviation },
            Abbreviation = new MultiLanguage { De = abbreviation },
            Description = new MultiLanguage { De = abbreviation },
            IsOnCall = isOnCall,
        };
    }

    private Guid AddClient(
        int idNumber,
        EntityTypeEnum type = EntityTypeEnum.Employee,
        string? name = null,
        bool isDeleted = false)
    {
        var clientId = Guid.NewGuid();
        _context.Client.Add(new Client
        {
            Id = clientId,
            Type = type,
            IdNumber = idNumber,
            Name = name ?? $"Employee{idNumber}",
            FirstName = "Test",
            IsDeleted = isDeleted,
        });
        return clientId;
    }

    private void AddMembership(Guid clientId)
    {
        _context.Membership.Add(new Membership
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
    }

    private void AddGroupMembership(Guid clientId, Guid groupId)
    {
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ClientId = clientId,
        });
    }

    private Guid AddWork(
        Guid clientId,
        DateOnly date,
        Guid shiftId,
        decimal workTime,
        bool isDeleted = false,
        Guid? analyseToken = null,
        WorkLockLevel lockLevel = WorkLockLevel.Closed)
    {
        var workId = Guid.NewGuid();
        _context.Work.Add(new Work
        {
            Id = workId,
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = date,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = workTime,
            LockLevel = lockLevel,
            AnalyseToken = analyseToken,
            IsDeleted = isDeleted,
        });
        return workId;
    }

    private void AddWorkChange(
        Guid workId,
        WorkChangeType type,
        decimal changeTime,
        Guid? replaceClientId = null,
        decimal surcharges = 0m,
        string description = "",
        bool isDeleted = false,
        Guid? analyseToken = null)
    {
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = workId,
            Type = type,
            ChangeTime = changeTime,
            Surcharges = surcharges,
            StartTime = new TimeOnly(16, 0),
            EndTime = new TimeOnly(17, 0),
            ReplaceClientId = replaceClientId,
            Description = description,
            IsDeleted = isDeleted,
            AnalyseToken = analyseToken,
        });
    }

    private void AddBreak(
        Guid clientId,
        Guid absenceId,
        DateOnly date,
        decimal workTime,
        bool isDeleted = false,
        Guid? analyseToken = null,
        WorkLockLevel lockLevel = WorkLockLevel.Closed,
        decimal surcharges = 0m)
    {
        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AbsenceId = absenceId,
            CurrentDate = date,
            StartTime = new TimeOnly(0, 0),
            EndTime = new TimeOnly(23, 59),
            WorkTime = workTime,
            Surcharges = surcharges,
            LockLevel = lockLevel,
            AnalyseToken = analyseToken,
            IsDeleted = isDeleted,
        });
    }
}
