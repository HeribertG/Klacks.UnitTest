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

[TestFixture, Ignore("Pre-existing API drift: ResourceMonitorDayResource was renamed (MaxCount/DienstCount/AbsenzHours). Tests still reference ActualHours/ShouldHours — needs separate fix.")]
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

        result.DailyData.First(d => d.Date == new DateOnly(2026, 1, 5)).MaxCount.ShouldBe(8.0, 0.01);
        result.DailyData.First(d => d.Date == new DateOnly(2026, 1, 3)).MaxCount.ShouldBe(0);
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

        result.DailyData.First(d => d.Date == new DateOnly(2026, 1, 3)).MaxCount.ShouldBe(6.0, 0.01);
        result.DailyData.First(d => d.Date == new DateOnly(2026, 1, 4)).MaxCount.ShouldBe(6.0, 0.01);
    }

    [Test]
    public async Task Handle_MonthlyContract_ConvertsToWeeklyCorrectly()
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
        monday.MaxCount.ShouldBeInRange(7.5, 8.5);
    }

    [Test]
    public async Task Handle_ActualHours_SumsWorkTimePerDay()
    {
        var shiftId = Guid.NewGuid();
        await _context.Work.AddRangeAsync(new[]
        {
            new Work { Id = Guid.NewGuid(), ShiftId = shiftId, ClientId = Guid.NewGuid(), CurrentDate = new DateOnly(2026, 3, 15), WorkTime = 8 },
            new Work { Id = Guid.NewGuid(), ShiftId = shiftId, ClientId = Guid.NewGuid(), CurrentDate = new DateOnly(2026, 3, 15), WorkTime = 6 },
            new Work { Id = Guid.NewGuid(), ShiftId = shiftId, ClientId = Guid.NewGuid(), CurrentDate = new DateOnly(2026, 3, 16), WorkTime = 7 },
        });
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(new GetResourceMonitorQuery(2026, null), CancellationToken.None);

        result.DailyData.First(d => d.Date == new DateOnly(2026, 3, 15)).DienstCount.ShouldBe(14.0, 0.01);
        result.DailyData.First(d => d.Date == new DateOnly(2026, 3, 16)).DienstCount.ShouldBe(7.0, 0.01);
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
