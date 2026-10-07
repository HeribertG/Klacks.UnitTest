// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for PayrollExportDataLoader against an in-memory EF Core database: absences of group members are
/// exported on days without any work (on-call days as one day, ordinary absences in hours), borrowed staff keep
/// the same-day-work attribution, and soft-deleted, scenario, unsealed and foreign-group absences stay out.
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

    private DataBaseContext _context = null!;
    private PayrollExportDataLoader _loader = null!;

    private readonly Guid _groupId = Guid.NewGuid();
    private readonly Guid _otherGroupId = Guid.NewGuid();
    private readonly Guid _groupShiftId = Guid.NewGuid();
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
        _context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = _groupId, ShiftId = _groupShiftId });
        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task LoadAsync_OnCallSundayWithoutWork_IsExportedAsOneDay()
    {
        var clientId = AddMember(101, _groupId);
        AddBreak(clientId, _onCallAbsenceId, OnCallSunday, workTime: 0m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(_groupId, FromDate, UntilDate);

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
        var clientId = AddMember(101, _groupId);
        AddBreak(clientId, _onCallAbsenceId, OnCallSunday, workTime: 0m);
        AddBreak(clientId, _onCallAbsenceId, OnCallSunday, workTime: 0m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(_groupId, FromDate, UntilDate);

        var entry = data.Employees.ShouldHaveSingleItem().Entries.ShouldHaveSingleItem();
        entry.Quantity.ShouldBe(1m);
        entry.Unit.ShouldBe(PayrollQuantityUnit.Days);
    }

    [Test]
    public async Task LoadAsync_VacationDayWithoutWork_IsExportedInHours()
    {
        var clientId = AddMember(101, _groupId);
        AddBreak(clientId, _vacationAbsenceId, VacationMonday, workTime: 8.4m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(_groupId, FromDate, UntilDate);

        var entry = data.Employees.ShouldHaveSingleItem().Entries.ShouldHaveSingleItem();
        entry.AbsenceId.ShouldBe(_vacationAbsenceId);
        entry.Quantity.ShouldBe(8.4m);
        entry.Unit.ShouldBe(PayrollQuantityUnit.Hours);
    }

    [Test]
    public async Task LoadAsync_BorrowedEmployeeWithSameDayGroupWork_KeepsAbsence()
    {
        var clientId = AddClient(201);
        AddWork(clientId, WorkDay);
        AddBreak(clientId, _vacationAbsenceId, WorkDay, workTime: 2m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(_groupId, FromDate, UntilDate);

        var employee = data.Employees.ShouldHaveSingleItem();
        employee.Entries.Count(e => e.Kind == PayrollEntryKind.WorkHours).ShouldBe(1);
        var absence = employee.Entries.Single(e => e.Kind == PayrollEntryKind.Absence);
        absence.Quantity.ShouldBe(2m);
    }

    [Test]
    public async Task LoadAsync_BreakOfClientOutsideGroup_IsNotExported()
    {
        var foreignClientId = AddMember(301, _otherGroupId);
        AddBreak(foreignClientId, _onCallAbsenceId, OnCallSunday, workTime: 0m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(_groupId, FromDate, UntilDate);

        data.Employees.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadAsync_SoftDeletedBreak_IsNotExported()
    {
        var clientId = AddMember(101, _groupId);
        AddBreak(clientId, _onCallAbsenceId, OnCallSunday, workTime: 0m, isDeleted: true);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(_groupId, FromDate, UntilDate);

        data.Employees.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadAsync_ScenarioBreak_IsNotExported()
    {
        var clientId = AddMember(101, _groupId);
        AddBreak(clientId, _onCallAbsenceId, OnCallSunday, workTime: 0m, analyseToken: Guid.NewGuid());
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(_groupId, FromDate, UntilDate);

        data.Employees.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadAsync_UnsealedBreak_IsNotExported()
    {
        var clientId = AddMember(101, _groupId);
        AddBreak(clientId, _onCallAbsenceId, OnCallSunday, workTime: 0m, lockLevel: WorkLockLevel.None);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(_groupId, FromDate, UntilDate);

        data.Employees.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadAsync_MembershipEndedBeforeBreak_IsNotExported()
    {
        var clientId = AddMember(101, _groupId, groupItemUntil: new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc));
        AddBreak(clientId, _onCallAbsenceId, OnCallSunday, workTime: 0m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(_groupId, FromDate, UntilDate);

        data.Employees.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadAsync_ScenarioMembership_DoesNotAttributeBreak()
    {
        var clientId = AddClient(101);
        AddMembership(clientId);
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = _groupId,
            ClientId = clientId,
            AnalyseToken = Guid.NewGuid(),
        });
        AddBreak(clientId, _onCallAbsenceId, OnCallSunday, workTime: 0m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(_groupId, FromDate, UntilDate);

        data.Employees.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadAsync_ScenarioWorkOfGroupShift_DoesNotAttributeBreak()
    {
        var clientId = AddClient(402);
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = _groupShiftId,
            CurrentDate = WorkDay,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            LockLevel = WorkLockLevel.None,
            AnalyseToken = Guid.NewGuid(),
        });
        AddBreak(clientId, _vacationAbsenceId, WorkDay, workTime: 8m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(_groupId, FromDate, UntilDate);

        data.Employees.ShouldBeEmpty();
    }
    [Test]
    public async Task LoadAsync_OnCallSunday_FlowsIntoDatevLugAsTagesanzahl()
    {
        var clientId = AddMember(101, _groupId);
        AddBreak(clientId, _onCallAbsenceId, OnCallSunday, workTime: 0m);
        await _context.SaveChangesAsync();
        var config = new PayrollExportGroupConfig
        {
            GroupId = _groupId,
            TargetSystem = PayrollExportConstants.FormatKeyDatevLug,
            Delimiter = PayrollExportConstants.DefaultDelimiter,
            Encoding = PayrollExportConstants.DefaultEncoding,
            AbsenceMappingJson = $"{{\"{_onCallAbsenceId}\":{{\"ausfallschluessel\":\"\",\"wageType\":\"4711\"}}}}",
        };

        var data = await _loader.LoadAsync(_groupId, FromDate, UntilDate);
        var result = new DatevLugBewegungsdatenFormatter().Format(data, config);

        var line = Encoding.GetEncoding(PayrollExportConstants.Windows1252CodePage)
            .GetString(result.Content)
            .Replace(PayrollExportConstants.LineEnding, string.Empty);
        line.ShouldBe("101;18012026;;4711;;1,00;;;;;");
        result.SkippedAbsenceCount.ShouldBe(0);
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

    private Guid AddClient(int idNumber)
    {
        var clientId = Guid.NewGuid();
        _context.Client.Add(new Client
        {
            Id = clientId,
            Type = EntityTypeEnum.Employee,
            IdNumber = idNumber,
            Name = $"Employee{idNumber}",
            FirstName = "Test",
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

    private Guid AddMember(int idNumber, Guid groupId, DateTime? groupItemUntil = null)
    {
        var clientId = AddClient(idNumber);
        AddMembership(clientId);
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ClientId = clientId,
            ValidUntil = groupItemUntil,
        });
        return clientId;
    }

    private void AddWork(Guid clientId, DateOnly date)
    {
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = _groupShiftId,
            CurrentDate = date,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            LockLevel = WorkLockLevel.Closed,
        });
    }

    private void AddBreak(
        Guid clientId,
        Guid absenceId,
        DateOnly date,
        decimal workTime,
        bool isDeleted = false,
        Guid? analyseToken = null,
        WorkLockLevel lockLevel = WorkLockLevel.Closed)
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
            LockLevel = lockLevel,
            AnalyseToken = analyseToken,
            IsDeleted = isDeleted,
        });
    }
}
