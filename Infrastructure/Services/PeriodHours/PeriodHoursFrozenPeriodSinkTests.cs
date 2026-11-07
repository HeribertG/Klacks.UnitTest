// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Every path of PeriodHoursService that writes or removes cached real-plan period-hours rows leaves a frozen period
/// (sealed, exported, older than the latest two exports) untouched: single and bulk recalculation, recalculate and
/// notify, the in-place refresh, the cache invalidation and the removal of overlapping rows with other boundaries.
/// Only the admin override (includeFrozen) recalculates a frozen period. Open periods are written as before.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Services.Clients;
using Klacks.Api.Infrastructure.Services.PeriodHours;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static Klacks.UnitTest.Infrastructure.Services.PeriodHours.FrozenPeriodTestData;

namespace Klacks.UnitTest.Infrastructure.Services.PeriodHours;

[TestFixture]
public class PeriodHoursFrozenPeriodSinkTests
{
    private const decimal StaleHours = 42m;
    private const decimal WorkHours = 8m;

    private static readonly Guid FrozenClient = Guid.NewGuid();
    private static readonly Guid OpenClient = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private IWorkNotificationService _notificationService = null!;
    private PeriodHoursService _service = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        AddMember(FrozenClient);
        AddMember(OpenClient);
        FreezeJanuary(_context, FrozenClient, WorkHours);
        AddWork(_context, OpenClient, new DateOnly(2099, 1, 10), WorkLockLevel.None, WorkHours);
        _context.ClientPeriodHours.AddRange(
            CacheRow(FrozenClient, January.StartDate, January.EndDate),
            CacheRow(OpenClient, January.StartDate, January.EndDate));
        await _context.SaveChangesAsync();

        var groupFilter = Substitute.For<IClientGroupFilterService>();
        groupFilter.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(call => call.Arg<IQueryable<Client>>());
        var contractData = Substitute.For<IClientContractDataProvider>();
        contractData.GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<Guid, EffectiveContractData>());
        contractData.GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData());
        _notificationService = Substitute.For<IWorkNotificationService>();

        _service = new PeriodHoursService(
            _context,
            Substitute.For<ILogger<PeriodHoursService>>(),
            _notificationService,
            groupFilter,
            contractData,
            Substitute.For<IWeekConfiguration>(),
            new FrozenPeriodPolicy(_context));
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task RecalculatePeriodHours_LeavesTheFrozenRow_AndTheOverrideRecalculatesIt()
    {
        await _service.RecalculatePeriodHoursAsync(FrozenClient, January.StartDate, January.EndDate);
        (await HoursOf(FrozenClient, January)).ShouldBe(StaleHours);

        await _service.RecalculatePeriodHoursAsync(FrozenClient, January.StartDate, January.EndDate, includeFrozen: true);
        (await HoursOf(FrozenClient, January)).ShouldBe(WorkHours);
    }

    [Test]
    public async Task RecalculateAndNotify_ReturnsTheCachedValue_WithoutWritingOrNotifying()
    {
        var result = await _service.RecalculateAndNotifyAsync(FrozenClient, January.StartDate, January.EndDate, null);

        result.Hours.ShouldBe(StaleHours);
        (await HoursOf(FrozenClient, January)).ShouldBe(StaleHours);
        await _notificationService.DidNotReceiveWithAnyArgs().NotifyPeriodHoursUpdated(default!);
    }

    [Test]
    public async Task RecalculateAndNotify_StillWritesAndNotifiesAnOpenPeriod()
    {
        var result = await _service.RecalculateAndNotifyAsync(OpenClient, January.StartDate, January.EndDate, null);

        result.Hours.ShouldBe(WorkHours);
        (await HoursOf(OpenClient, January)).ShouldBe(WorkHours);
        await _notificationService.Received(1).NotifyPeriodHoursUpdated(
            Arg.Is<PeriodHoursNotificationDto>(n => n.ClientId == OpenClient && n.Hours == WorkHours));
    }

    [Test]
    public async Task RecalculateAllClients_SkipsTheFrozenClient_AndReportsIt()
    {
        var frozen = await _service.RecalculateAllClientsAsync(January.StartDate, January.EndDate);

        frozen.ShouldBe([FrozenClient]);
        (await HoursOf(FrozenClient, January)).ShouldBe(StaleHours);
        (await HoursOf(OpenClient, January)).ShouldBe(WorkHours);
    }

    [Test]
    public async Task RecalculateAllClients_WithOverride_RecalculatesTheFrozenClient_AndReportsIt()
    {
        var frozen = await _service.RecalculateAllClientsAsync(
            January.StartDate, January.EndDate, includeFrozen: true);

        frozen.ShouldBe([FrozenClient]);
        (await HoursOf(FrozenClient, January)).ShouldBe(WorkHours);
        (await HoursOf(OpenClient, January)).ShouldBe(WorkHours);
    }

    [Test]
    public async Task GetFrozenClientIds_SeesTheSameClientsAsTheBulkRecalculation()
    {
        var frozen = await _service.GetFrozenClientIdsAsync(January.StartDate, January.EndDate);

        frozen.ShouldBe([FrozenClient]);
    }

    [Test]
    public async Task RefreshCachedPeriodHours_LeavesTheFrozenRow_AndRefreshesTheOpenOne()
    {
        await _service.RefreshCachedPeriodHoursAsync(January.StartDate, January.EndDate);

        (await HoursOf(FrozenClient, January)).ShouldBe(StaleHours);
        (await HoursOf(OpenClient, January)).ShouldBe(WorkHours);
    }

    [Test]
    public async Task InvalidateCache_KeepsTheFrozenRow_AndDropsTheOpenOne()
    {
        await _service.InvalidateCacheAsync(FrozenClient, new DateOnly(2099, 1, 10));
        await _service.InvalidateCacheAsync(OpenClient, new DateOnly(2099, 1, 10));

        (await HoursOf(FrozenClient, January)).ShouldBe(StaleHours);
        (await _context.ClientPeriodHours.AnyAsync(p => p.ClientId == OpenClient)).ShouldBeFalse();
    }

    [Test]
    public async Task StoringAnOverlappingPeriodWithOtherBoundaries_KeepsTheFrozenRow()
    {
        var shiftedStart = new DateOnly(2099, 1, 15);
        var shiftedEnd = new DateOnly(2099, 2, 14);

        await _service.RecalculatePeriodHoursAsync(FrozenClient, shiftedStart, shiftedEnd);

        (await HoursOf(FrozenClient, January)).ShouldBe(StaleHours);
        (await _context.ClientPeriodHours.AnyAsync(p =>
            p.ClientId == FrozenClient && p.StartDate == shiftedStart && p.EndDate == shiftedEnd)).ShouldBeTrue();
    }

    [Test]
    public async Task StoringAnOverlappingPeriodWithOtherBoundaries_StillRemovesAnOpenMiscutRow()
    {
        var shiftedStart = new DateOnly(2099, 1, 15);
        var shiftedEnd = new DateOnly(2099, 2, 14);

        await _service.RecalculatePeriodHoursAsync(OpenClient, shiftedStart, shiftedEnd);

        (await _context.ClientPeriodHours.AnyAsync(p =>
            p.ClientId == OpenClient && p.StartDate == January.StartDate)).ShouldBeFalse();
    }

    private void AddMember(Guid clientId)
    {
        _context.Client.Add(new Client
        {
            Id = clientId,
            Name = "Frozen",
            FirstName = "Test",
            Membership = new Membership
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                ValidFrom = new DateTime(2098, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            }
        });
    }

    private static ClientPeriodHours CacheRow(Guid clientId, DateOnly startDate, DateOnly endDate) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        StartDate = startDate,
        EndDate = endDate,
        Hours = StaleHours,
        CalculatedAt = DateTime.UtcNow
    };

    private async Task<decimal> HoursOf(Guid clientId, (DateOnly StartDate, DateOnly EndDate) period)
    {
        var row = await _context.ClientPeriodHours.AsNoTracking().SingleAsync(p =>
            p.ClientId == clientId && p.StartDate == period.StartDate && p.EndDate == period.EndDate);
        return row.Hours;
    }
}
