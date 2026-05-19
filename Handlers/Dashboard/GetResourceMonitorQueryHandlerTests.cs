// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for GetResourceMonitorQueryHandler Soll/Ist calculation.
/// </summary>
/// <param name="context">In-memory EF context seeded with contracts and works</param>
using Shouldly;
using Klacks.Api.Application.Handlers.Dashboard;
using Klacks.Api.Application.Queries.Dashboard;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Handlers.Dashboard;

[TestFixture]
public class GetResourceMonitorQueryHandlerTests
{
    private DataBaseContext _context = null!;
    private GetResourceMonitorQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, httpContextAccessor);
        var logger = Substitute.For<ILogger<GetResourceMonitorQueryHandler>>();
        _handler = new GetResourceMonitorQueryHandler(_context, logger);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task Handle_Returns365Days_ForNonLeapYear()
    {
        var result = await _handler.Handle(new GetResourceMonitorQuery(2026, null), CancellationToken.None);

        result.DailyData.Count().ShouldBe(365);
        result.DailyData.First().Date.ShouldBe(new DateOnly(2026, 1, 1));
        result.DailyData.Last().Date.ShouldBe(new DateOnly(2026, 12, 31));
    }

    [Test]
    public async Task Handle_MonFriEmployee_ShouldHoursOnlyOnWorkdays()
    {
        var contractId = Guid.NewGuid();
        await _context.Contract.AddAsync(new Contract
        {
            Id = contractId,
            GuaranteedHours = 40,
            PaymentInterval = PaymentInterval.Weekly,
            WorkOnMonday = true, WorkOnTuesday = true, WorkOnWednesday = true,
            WorkOnThursday = true, WorkOnFriday = true,
            WorkOnSaturday = false, WorkOnSunday = false,
            ValidFrom = new DateTime(2026, 1, 1)
        });
        await _context.ClientContract.AddAsync(new ClientContract
        {
            Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), ContractId = contractId,
            FromDate = new DateOnly(2026, 1, 1), UntilDate = null, IsActive = true
        });
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(new GetResourceMonitorQuery(2026, null), CancellationToken.None);

        result.DailyData.First(d => d.Date == new DateOnly(2026, 1, 5)).MaxCount.ShouldBe(1.0);
        result.DailyData.First(d => d.Date == new DateOnly(2026, 1, 3)).MaxCount.ShouldBe(0.0);
    }

    [Test]
    public async Task Handle_MonSunEmployee_ShouldHoursEveryDay()
    {
        var contractId = Guid.NewGuid();
        await _context.Contract.AddAsync(new Contract
        {
            Id = contractId,
            GuaranteedHours = 42,
            PaymentInterval = PaymentInterval.Weekly,
            WorkOnMonday = true, WorkOnTuesday = true, WorkOnWednesday = true,
            WorkOnThursday = true, WorkOnFriday = true,
            WorkOnSaturday = true, WorkOnSunday = true,
            ValidFrom = new DateTime(2026, 1, 1)
        });
        await _context.ClientContract.AddAsync(new ClientContract
        {
            Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), ContractId = contractId,
            FromDate = new DateOnly(2026, 1, 1), UntilDate = null, IsActive = true
        });
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(new GetResourceMonitorQuery(2026, null), CancellationToken.None);

        result.DailyData.First(d => d.Date == new DateOnly(2026, 1, 3)).MaxCount.ShouldBe(Math.Round(6.0 / 7.0, 2), 0.005);
        result.DailyData.First(d => d.Date == new DateOnly(2026, 1, 4)).MaxCount.ShouldBe(Math.Round(6.0 / 7.0, 2), 0.005);
    }

    [Test]
    public async Task Handle_MonthlyContract_CountsAsOneEmployeeOnWorkdays()
    {
        var contractId = Guid.NewGuid();
        await _context.Contract.AddAsync(new Contract
        {
            Id = contractId,
            GuaranteedHours = 173.33m,
            PaymentInterval = PaymentInterval.Monthly,
            WorkOnMonday = true, WorkOnTuesday = true, WorkOnWednesday = true,
            WorkOnThursday = true, WorkOnFriday = true,
            WorkOnSaturday = false, WorkOnSunday = false,
            ValidFrom = new DateTime(2026, 1, 1)
        });
        await _context.ClientContract.AddAsync(new ClientContract
        {
            Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), ContractId = contractId,
            FromDate = new DateOnly(2026, 1, 1), UntilDate = null, IsActive = true
        });
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(new GetResourceMonitorQuery(2026, null), CancellationToken.None);

        var monday = result.DailyData.First(d => d.Date == new DateOnly(2026, 1, 5));
        monday.MaxCount.ShouldBe(1.0);
    }

    [Test]
    public async Task Handle_DienstCount_CountsActiveShiftsPerDay()
    {
        await _context.Shift.AddRangeAsync(new[]
        {
            new Shift { Id = Guid.NewGuid(), FromDate = new DateOnly(2026, 1, 1), IsMonday = true },
            new Shift { Id = Guid.NewGuid(), FromDate = new DateOnly(2026, 1, 1), IsMonday = true },
            new Shift { Id = Guid.NewGuid(), FromDate = new DateOnly(2026, 1, 1), IsTuesday = true },
        });
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(new GetResourceMonitorQuery(2026, null), CancellationToken.None);

        result.DailyData.First(d => d.Date == new DateOnly(2026, 1, 5)).DienstCount.ShouldBe(2.0);
        result.DailyData.First(d => d.Date == new DateOnly(2026, 1, 6)).DienstCount.ShouldBe(1.0);
        result.DailyData.First(d => d.Date == new DateOnly(2026, 1, 4)).DienstCount.ShouldBe(0.0);
    }

    [Test]
    public async Task Handle_ExpiredContract_NotCountedAfterUntilDate()
    {
        var contractId = Guid.NewGuid();
        await _context.Contract.AddAsync(new Contract
        {
            Id = contractId, GuaranteedHours = 40, PaymentInterval = PaymentInterval.Weekly,
            WorkOnMonday = true, WorkOnTuesday = true, WorkOnWednesday = true,
            WorkOnThursday = true, WorkOnFriday = true,
            WorkOnSaturday = false, WorkOnSunday = false,
            ValidFrom = new DateTime(2026, 1, 1)
        });
        await _context.ClientContract.AddAsync(new ClientContract
        {
            Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), ContractId = contractId,
            FromDate = new DateOnly(2026, 1, 1),
            UntilDate = new DateOnly(2026, 1, 31),
            IsActive = true
        });
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(new GetResourceMonitorQuery(2026, null), CancellationToken.None);

        result.DailyData.First(d => d.Date == new DateOnly(2026, 2, 2)).MaxCount.ShouldBe(0);
    }

    [Test]
    public async Task Handle_InactiveContract_NotCounted()
    {
        var contractId = Guid.NewGuid();
        await _context.Contract.AddAsync(new Contract
        {
            Id = contractId, GuaranteedHours = 40, PaymentInterval = PaymentInterval.Weekly,
            WorkOnMonday = true, WorkOnTuesday = true, WorkOnWednesday = true,
            WorkOnThursday = true, WorkOnFriday = true,
            WorkOnSaturday = false, WorkOnSunday = false,
            ValidFrom = new DateTime(2026, 1, 1)
        });
        await _context.ClientContract.AddAsync(new ClientContract
        {
            Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), ContractId = contractId,
            FromDate = new DateOnly(2026, 1, 1), UntilDate = null, IsActive = false
        });
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(new GetResourceMonitorQuery(2026, null), CancellationToken.None);

        result.DailyData.First(d => d.Date == new DateOnly(2026, 1, 5)).MaxCount.ShouldBe(0.0);
    }
}
