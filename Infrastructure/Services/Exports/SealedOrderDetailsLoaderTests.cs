// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SealedOrderDetailsLoader against an in-memory EF Core database, verifying
/// the SealedOrder guard, inclusion of all lock levels via descendant resolution, sorting,
/// time formatting and the period-closed flags.
/// </summary>
using Shouldly;
using Klacks.Api.Application.Interfaces.Exports;
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
public class SealedOrderDetailsLoaderTests
{
    private DataBaseContext _context = null!;
    private IShiftDescendantResolver _descendantResolver = null!;
    private IPeriodClosedEntryFilter _periodClosedEntryFilter = null!;
    private IPeriodClosedLookup _lookup = null!;
    private SealedOrderDetailsLoader _loader = null!;

    private readonly Guid _sealedOrderId = Guid.NewGuid();
    private readonly Guid _childShiftId = Guid.NewGuid();
    private readonly Guid _plainShiftId = Guid.NewGuid();
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly Guid _closedEmployeeId = Guid.NewGuid();
    private readonly Guid _openEmployeeId = Guid.NewGuid();

    private static readonly DateOnly ClosedDate = new(2026, 1, 10);
    private static readonly DateOnly OpenDate = new(2026, 1, 12);

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _descendantResolver = Substitute.For<IShiftDescendantResolver>();
        _descendantResolver.ResolveAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, HashSet<Guid>>
            {
                [_sealedOrderId] = [_sealedOrderId, _childShiftId],
            });

        _lookup = Substitute.For<IPeriodClosedLookup>();
        _lookup.IsClosed(Arg.Any<Guid>(), Arg.Any<DateOnly>())
            .Returns(ci => ci.ArgAt<Guid>(0) == _closedEmployeeId && ci.ArgAt<DateOnly>(1) == ClosedDate);

        _periodClosedEntryFilter = Substitute.For<IPeriodClosedEntryFilter>();
        _periodClosedEntryFilter.BuildAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(_lookup);

        _loader = new SealedOrderDetailsLoader(_context, _descendantResolver, _periodClosedEntryFilter);

        _context.Client.Add(new Client
        {
            Id = _customerId,
            Type = EntityTypeEnum.Customer,
            IdNumber = 900,
            Name = "Customer",
            FirstName = "Corp",
            ExternalCustomerReference = "CUST-42",
        });
        _context.Client.Add(new Client
        {
            Id = _closedEmployeeId,
            Type = EntityTypeEnum.Employee,
            IdNumber = 101,
            Name = "Closed",
            FirstName = "Employee",
        });
        _context.Client.Add(new Client
        {
            Id = _openEmployeeId,
            Type = EntityTypeEnum.Employee,
            IdNumber = 102,
            Name = "Open",
            FirstName = "Employee",
        });

        _context.Shift.Add(new Shift
        {
            Id = _sealedOrderId,
            Status = ShiftStatus.SealedOrder,
            Name = "Night Watch",
            Abbreviation = "NW",
            SourceSystemId = "erp-main",
            ExternalOrderReference = "PO-4711",
            ClientId = _customerId,
            FromDate = new DateOnly(2026, 1, 1),
        });
        _context.Shift.Add(new Shift
        {
            Id = _plainShiftId,
            Status = ShiftStatus.OriginalShift,
            Name = "Not an order",
            Abbreviation = "NA",
            FromDate = new DateOnly(2026, 1, 1),
        });

        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ShiftId = _childShiftId,
            ClientId = _openEmployeeId,
            CurrentDate = OpenDate,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(17, 0),
            WorkTime = 8m,
            LockLevel = WorkLockLevel.None,
        });
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ShiftId = _sealedOrderId,
            ClientId = _closedEmployeeId,
            CurrentDate = ClosedDate,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            LockLevel = WorkLockLevel.Closed,
        });
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ShiftId = Guid.NewGuid(),
            ClientId = _openEmployeeId,
            CurrentDate = ClosedDate,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(17, 0),
            WorkTime = 8m,
            LockLevel = WorkLockLevel.None,
        });

        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task LoadAsync_ReturnsNull_WhenIdIsNotASealedOrder()
    {
        var result = await _loader.LoadAsync(_plainShiftId, null, null);

        result.ShouldBeNull();
    }

    [Test]
    public async Task LoadAsync_ReturnsOrderMetadataWithErpAndCustomerReferences()
    {
        var result = await _loader.LoadAsync(_sealedOrderId, null, null);

        result.ShouldNotBeNull();
        result.Id.ShouldBe(_sealedOrderId);
        result.Name.ShouldBe("Night Watch");
        result.Abbreviation.ShouldBe("NW");
        result.SourceSystemId.ShouldBe("erp-main");
        result.ExternalOrderReference.ShouldBe("PO-4711");
        result.CustomerId.ShouldBe(_customerId);
        result.CustomerNumber.ShouldBe(900);
        result.CustomerName.ShouldBe("Customer, Corp");
        result.CustomerExternalReference.ShouldBe("CUST-42");
    }

    [Test]
    public async Task LoadAsync_IncludesWorksOfAllLockLevels_FromDescendantShifts_SortedByDate()
    {
        var result = await _loader.LoadAsync(_sealedOrderId, null, null);

        result.ShouldNotBeNull();
        result.WorkEntries.Count.ShouldBe(2);
        result.WorkEntries[0].WorkDate.ShouldBe(ClosedDate);
        result.WorkEntries[0].LockLevel.ShouldBe((int)WorkLockLevel.Closed);
        result.WorkEntries[1].WorkDate.ShouldBe(OpenDate);
        result.WorkEntries[1].LockLevel.ShouldBe((int)WorkLockLevel.None);
    }

    [Test]
    public async Task LoadAsync_SetsPeriodClosedFlag_PerEmployeeAndDate()
    {
        var result = await _loader.LoadAsync(_sealedOrderId, null, null);

        result.ShouldNotBeNull();
        result.WorkEntries[0].PeriodClosed.ShouldBeTrue();
        result.WorkEntries[1].PeriodClosed.ShouldBeFalse();
    }

    [Test]
    public async Task LoadAsync_FormatsEmployeeNameAndTimes()
    {
        var result = await _loader.LoadAsync(_sealedOrderId, null, null);

        result.ShouldNotBeNull();
        var entry = result.WorkEntries[0];
        entry.EmployeeName.ShouldBe("Closed, Employee");
        entry.EmployeeIdNumber.ShouldBe(101);
        entry.StartTime.ShouldBe("08:00");
        entry.EndTime.ShouldBe("16:00");
        entry.Hours.ShouldBe(8m);
    }

    [Test]
    public async Task LoadAsync_AppliesDateRangeFilter()
    {
        var result = await _loader.LoadAsync(_sealedOrderId, ClosedDate, ClosedDate);

        result.ShouldNotBeNull();
        result.WorkEntries.Count.ShouldBe(1);
        result.WorkEntries[0].WorkDate.ShouldBe(ClosedDate);
    }

    [Test]
    public async Task LoadAsync_ReturnsEmptyWorkEntries_WhenRangeHasNoWorks()
    {
        var result = await _loader.LoadAsync(_sealedOrderId, new DateOnly(2030, 1, 1), new DateOnly(2030, 1, 31));

        result.ShouldNotBeNull();
        result.WorkEntries.ShouldBeEmpty();
    }
}
