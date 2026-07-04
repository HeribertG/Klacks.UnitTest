// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClientPeriodExportDataLoader against an in-memory EF Core database, verifying
/// the Employee/ExternEmp-only filter, date-range/lock-level filtering, and
/// Break/Expenses/WorkChange linkage.
/// </summary>
using Shouldly;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Exports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class ClientPeriodExportDataLoaderTests
{
    private DataBaseContext _context = null!;
    private ClientPeriodExportDataLoader _loader = null!;

    private readonly Guid _employeeClientId = Guid.NewGuid();
    private readonly Guid _externClientId = Guid.NewGuid();
    private readonly Guid _customerClientId = Guid.NewGuid();
    private readonly Guid _employeeWorkId = Guid.NewGuid();
    private readonly Guid _workChangeId = Guid.NewGuid();

    private static readonly DateOnly FromDate = new(2026, 1, 1);
    private static readonly DateOnly UntilDate = new(2026, 1, 31);

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _loader = new ClientPeriodExportDataLoader(_context);

        _context.Client.Add(new Client
        {
            Id = _employeeClientId,
            Type = EntityTypeEnum.Employee,
            IdNumber = 101,
            Name = "Employee",
            FirstName = "One",
        });
        _context.Client.Add(new Client
        {
            Id = _externClientId,
            Type = EntityTypeEnum.ExternEmp,
            IdNumber = 201,
            Name = "Extern",
            FirstName = "One",
        });
        _context.Client.Add(new Client
        {
            Id = _customerClientId,
            Type = EntityTypeEnum.Customer,
            IdNumber = 301,
            Name = "Customer",
            FirstName = "One",
        });

        _context.Work.Add(new Work
        {
            Id = _employeeWorkId,
            ClientId = _employeeClientId,
            ShiftId = Guid.NewGuid(),
            CurrentDate = new DateOnly(2026, 1, 10),
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8,
            Surcharges = 0,
            LockLevel = WorkLockLevel.Closed,
        });
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _externClientId,
            ShiftId = Guid.NewGuid(),
            CurrentDate = new DateOnly(2026, 1, 15),
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(17, 0),
            WorkTime = 8,
            LockLevel = WorkLockLevel.Closed,
        });
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _customerClientId,
            ShiftId = Guid.NewGuid(),
            CurrentDate = new DateOnly(2026, 1, 12),
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(17, 0),
            WorkTime = 8,
            LockLevel = WorkLockLevel.Closed,
        });
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _employeeClientId,
            ShiftId = Guid.NewGuid(),
            CurrentDate = new DateOnly(2025, 12, 31),
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(17, 0),
            WorkTime = 8,
            LockLevel = WorkLockLevel.Closed,
        });
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _employeeClientId,
            ShiftId = Guid.NewGuid(),
            CurrentDate = new DateOnly(2026, 1, 20),
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(17, 0),
            WorkTime = 8,
            LockLevel = WorkLockLevel.None,
        });

        var absenceId = Guid.NewGuid();
        _context.Absence.Add(new Absence
        {
            Id = absenceId,
            Name = new MultiLanguage { De = "Ferien" },
            Abbreviation = new MultiLanguage { De = "FER" },
            Description = new MultiLanguage { De = "Ferien" },
        });
        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = _employeeClientId,
            AbsenceId = absenceId,
            CurrentDate = new DateOnly(2026, 1, 10),
            StartTime = new TimeOnly(12, 0),
            EndTime = new TimeOnly(13, 0),
            WorkTime = 1,
            LockLevel = WorkLockLevel.Closed,
        });

        _context.Expenses.Add(new Expenses
        {
            Id = Guid.NewGuid(),
            WorkId = _employeeWorkId,
            Amount = 12.5m,
            Description = "Parking",
            Taxable = true,
        });

        _context.WorkChange.Add(new WorkChange
        {
            Id = _workChangeId,
            WorkId = _employeeWorkId,
            Type = WorkChangeType.ReplacementStart,
            ChangeTime = 2,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(10, 0),
            Description = "Replacement",
            ReplaceClientId = _externClientId,
            Surcharges = 0,
            ToInvoice = false,
        });

        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task LoadAsync_ReturnsOnlyEmployeeAndExternEmpGroups_ExcludingCustomer()
    {
        var result = await _loader.LoadAsync(FromDate, UntilDate);

        result.Clients.Count.ShouldBe(2);
        result.Clients.ShouldNotContain(c => c.ClientId == _customerClientId);
    }

    [Test]
    public async Task LoadAsync_IncludesEmployeeGroup_WithCorrectClientType()
    {
        var result = await _loader.LoadAsync(FromDate, UntilDate);

        var employeeGroup = result.Clients.Single(c => c.ClientId == _employeeClientId);
        employeeGroup.ClientType.ShouldBe(EntityTypeEnum.Employee);
        employeeGroup.ClientIdNumber.ShouldBe(101);
    }

    [Test]
    public async Task LoadAsync_IncludesExternEmpGroup_WithCorrectClientType()
    {
        var result = await _loader.LoadAsync(FromDate, UntilDate);

        var externGroup = result.Clients.Single(c => c.ClientId == _externClientId);
        externGroup.ClientType.ShouldBe(EntityTypeEnum.ExternEmp);
        externGroup.ClientIdNumber.ShouldBe(201);
    }

    [Test]
    public async Task LoadAsync_ExcludesWorkOutsideDateRangeAndNotClosed()
    {
        var result = await _loader.LoadAsync(FromDate, UntilDate);

        var employeeGroup = result.Clients.Single(c => c.ClientId == _employeeClientId);
        employeeGroup.WorkEntries.Count.ShouldBe(1);
        employeeGroup.WorkEntries.Single().WorkId.ShouldBe(_employeeWorkId);
    }

    [Test]
    public async Task LoadAsync_LinksBreakByClientAndDate()
    {
        var result = await _loader.LoadAsync(FromDate, UntilDate);

        var workEntry = result.Clients.Single(c => c.ClientId == _employeeClientId).WorkEntries.Single();
        workEntry.Breaks.Count.ShouldBe(1);
        workEntry.Breaks[0].AbsenceName.ShouldBe("Ferien");
    }

    [Test]
    public async Task LoadAsync_LinksExpensesByWorkId()
    {
        var result = await _loader.LoadAsync(FromDate, UntilDate);

        var workEntry = result.Clients.Single(c => c.ClientId == _employeeClientId).WorkEntries.Single();
        workEntry.Expenses.Count.ShouldBe(1);
        workEntry.Expenses[0].Amount.ShouldBe(12.5m);
    }

    [Test]
    public async Task LoadAsync_LinksWorkChangeByWorkId_WithReplaceEmployeeName()
    {
        var result = await _loader.LoadAsync(FromDate, UntilDate);

        var workEntry = result.Clients.Single(c => c.ClientId == _employeeClientId).WorkEntries.Single();
        workEntry.Changes.Count.ShouldBe(1);
        workEntry.Changes[0].ReplaceEmployeeName.ShouldBe("Extern, One");
    }

    [Test]
    public async Task LoadAsync_UsesRequestedDateRange_NotDerivedFromWorkDates()
    {
        var result = await _loader.LoadAsync(FromDate, UntilDate);

        result.StartDate.ShouldBe(FromDate);
        result.EndDate.ShouldBe(UntilDate);
    }

    [Test]
    public async Task LoadAsync_ReturnsEmptyClients_WhenNoWorkInRange()
    {
        var farFuture = new DateOnly(2030, 1, 1);
        var farFutureEnd = new DateOnly(2030, 1, 31);

        var result = await _loader.LoadAsync(farFuture, farFutureEnd);

        result.Clients.ShouldBeEmpty();
        result.StartDate.ShouldBe(farFuture);
        result.EndDate.ShouldBe(farFutureEnd);
    }
}
