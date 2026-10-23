// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for SealedOrderListLoader against an in-memory EF Core database: the SealedOrder and
/// deleted guards, the date and customer filters, the ordering, the customer mapping and the Work counters
/// summed over the resolved descendant shifts. The search branch relies on Npgsql ILike and is not covered here.
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
public class SealedOrderListLoaderCharacterizationTests
{
    private DataBaseContext _context = null!;
    private IShiftDescendantResolver _descendantResolver = null!;
    private SealedOrderListLoader _loader = null!;

    private readonly Guid _customerId = Guid.NewGuid();
    private readonly Guid _employeeId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _descendantResolver = Substitute.For<IShiftDescendantResolver>();
        _descendantResolver.ResolveAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, HashSet<Guid>>());

        _loader = new SealedOrderListLoader(_context, _descendantResolver);

        _context.Client.Add(new Client
        {
            Id = _customerId,
            Type = EntityTypeEnum.Customer,
            IdNumber = 900,
            Name = "Customer",
            FirstName = "Corp",
        });
        _context.Client.Add(new Client
        {
            Id = _employeeId,
            Type = EntityTypeEnum.Employee,
            IdNumber = 101,
            Name = "Employee",
            FirstName = "Eve",
        });
        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task LoadAsync_NoSealedOrder_ReturnsEmptyWithoutResolvingDescendants()
    {
        AddShift(Guid.NewGuid(), "Plain", new DateOnly(2026, 1, 1), null, ShiftStatus.OriginalShift);

        var result = await _loader.LoadAsync(null, null, null, null);

        result.ShouldBeEmpty();
        await _descendantResolver.DidNotReceiveWithAnyArgs()
            .ResolveAsync(default!, default, default);
    }

    [Test]
    public async Task LoadAsync_DateRange_KeepsOverlappingSealedOrdersOrderedByFromDateDescThenName()
    {
        var beta = Guid.NewGuid();
        var alpha = Guid.NewGuid();
        var gamma = Guid.NewGuid();
        AddShift(beta, "Beta", new DateOnly(2026, 1, 1), null);
        AddShift(alpha, "Alpha", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        AddShift(gamma, "Gamma", new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        AddShift(Guid.NewGuid(), "EndedBefore", new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 31));
        AddShift(Guid.NewGuid(), "StartsAfter", new DateOnly(2026, 5, 1), null);
        AddShift(Guid.NewGuid(), "NotAnOrder", new DateOnly(2026, 2, 1), null, ShiftStatus.OriginalShift);
        AddShift(Guid.NewGuid(), "Deleted", new DateOnly(2026, 2, 1), null, isDeleted: true);

        var result = await _loader.LoadAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 30), null, null);

        result.Select(r => r.Id).ShouldBe([gamma, alpha, beta]);
    }

    [Test]
    public async Task LoadAsync_CustomerFilter_KeepsOnlyOrdersOfThatClient()
    {
        var ofCustomer = Guid.NewGuid();
        AddShift(ofCustomer, "Mine", new DateOnly(2026, 1, 1), null, clientId: _customerId);
        AddShift(Guid.NewGuid(), "Other", new DateOnly(2026, 1, 1), null, clientId: _employeeId);
        AddShift(Guid.NewGuid(), "NoClient", new DateOnly(2026, 1, 1), null);

        var result = await _loader.LoadAsync(null, null, _customerId, null);

        result.Select(r => r.Id).ShouldBe([ofCustomer]);
    }

    [Test]
    public async Task LoadAsync_CustomerAndNonCustomerClient_MapsCustomerFieldsOnlyForCustomers()
    {
        var customerOrder = Guid.NewGuid();
        var employeeOrder = Guid.NewGuid();
        AddShift(customerOrder, "B-Customer", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), clientId: _customerId);
        AddShift(employeeOrder, "A-Employee", new DateOnly(2026, 1, 1), null, clientId: _employeeId);

        var result = await _loader.LoadAsync(null, null, null, null);

        result.Count.ShouldBe(2);
        var employee = result[0];
        employee.Id.ShouldBe(employeeOrder);
        employee.Name.ShouldBe("A-Employee");
        employee.Abbreviation.ShouldBe("ABB");
        employee.SourceSystemId.ShouldBe("erp-main");
        employee.ExternalOrderReference.ShouldBe("PO-1");
        employee.CustomerId.ShouldBeNull();
        employee.CustomerNumber.ShouldBeNull();
        employee.CustomerName.ShouldBeNull();
        employee.TotalWorks.ShouldBe(0);
        employee.ClosedWorks.ShouldBe(0);

        var customer = result[1];
        customer.Id.ShouldBe(customerOrder);
        customer.FromDate.ShouldBe(new DateOnly(2026, 1, 1));
        customer.UntilDate.ShouldBe(new DateOnly(2026, 1, 31));
        customer.CustomerId.ShouldBe(_customerId);
        customer.CustomerNumber.ShouldBe(900);
        customer.CustomerName.ShouldBe("Customer, Corp");
    }

    [Test]
    public async Task LoadAsync_WithDateRange_CountsWorksOfDescendantsInsideTheRange()
    {
        var order = Guid.NewGuid();
        var child = Guid.NewGuid();
        AddShift(order, "Order", new DateOnly(2026, 1, 1), null);
        StubDescendants(order, child);
        AddWork(child, new DateOnly(2026, 1, 10), WorkLockLevel.None);
        AddWork(order, new DateOnly(2026, 1, 11), WorkLockLevel.Closed);
        AddWork(child, new DateOnly(2026, 1, 12), WorkLockLevel.Confirmed);
        AddWork(child, new DateOnly(2025, 12, 31), WorkLockLevel.Closed);
        AddWork(child, new DateOnly(2026, 2, 1), WorkLockLevel.Closed);
        AddWork(child, new DateOnly(2026, 1, 15), WorkLockLevel.Closed, isDeleted: true);
        AddWork(Guid.NewGuid(), new DateOnly(2026, 1, 15), WorkLockLevel.Closed);

        var result = await _loader.LoadAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), null, null);

        var item = result.ShouldHaveSingleItem();
        item.TotalWorks.ShouldBe(3);
        item.ClosedWorks.ShouldBe(1);
        await _descendantResolver.Received(1).ResolveAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(order)),
            Arg.Is(false),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LoadAsync_WithoutDateRange_CountsEveryNonDeletedWorkOfDescendants()
    {
        var order = Guid.NewGuid();
        var child = Guid.NewGuid();
        AddShift(order, "Order", new DateOnly(2026, 1, 1), null);
        StubDescendants(order, child);
        AddWork(child, new DateOnly(2025, 12, 31), WorkLockLevel.Closed);
        AddWork(child, new DateOnly(2026, 2, 1), WorkLockLevel.None);
        AddWork(child, new DateOnly(2026, 1, 15), WorkLockLevel.Closed, isDeleted: true);

        var result = await _loader.LoadAsync(null, null, null, null);

        var item = result.ShouldHaveSingleItem();
        item.TotalWorks.ShouldBe(2);
        item.ClosedWorks.ShouldBe(1);
    }

    [Test]
    public async Task LoadAsync_DescendantsWithoutWorks_ReportsZeroCounters()
    {
        var order = Guid.NewGuid();
        AddShift(order, "Order", new DateOnly(2026, 1, 1), null);
        StubDescendants(order, Guid.NewGuid());

        var result = await _loader.LoadAsync(null, null, null, null);

        var item = result.ShouldHaveSingleItem();
        item.TotalWorks.ShouldBe(0);
        item.ClosedWorks.ShouldBe(0);
    }

    private void StubDescendants(Guid order, Guid child)
    {
        _descendantResolver.ResolveAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, HashSet<Guid>>
            {
                [order] = [order, child],
            });
    }

    private void AddShift(
        Guid id,
        string name,
        DateOnly fromDate,
        DateOnly? untilDate,
        ShiftStatus status = ShiftStatus.SealedOrder,
        Guid? clientId = null,
        bool isDeleted = false)
    {
        _context.Shift.Add(new Shift
        {
            Id = id,
            Status = status,
            Name = name,
            Abbreviation = "ABB",
            SourceSystemId = "erp-main",
            ExternalOrderReference = "PO-1",
            ClientId = clientId,
            FromDate = fromDate,
            UntilDate = untilDate,
            IsDeleted = isDeleted,
        });
        _context.SaveChanges();
    }

    private void AddWork(Guid shiftId, DateOnly date, WorkLockLevel lockLevel, bool isDeleted = false)
    {
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            ClientId = _employeeId,
            CurrentDate = date,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            LockLevel = lockLevel,
            IsDeleted = isDeleted,
        });
        _context.SaveChanges();
    }
}
