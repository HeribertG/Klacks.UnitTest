// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The bulk delete recalculates and persists the period hours after the overtime cascade, once per deleted Work's
/// client, scenario token and pay period, the state a single delete leaves, and drops no cached row: readers prefer a
/// cached row over the live sum and the payroll export reads only the cached rows. The persisted effect is covered by
/// WorkBulkDeletePeriodHoursPostgresTests (Klacks.IntegrationTest).
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class BulkDeleteWorksPeriodHoursCacheTests
{
    private static readonly DateOnly Day = new(2027, 4, 12);
    private static readonly DateOnly PeriodStart = new(2027, 4, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 4, 30);
    private static readonly DateOnly NextPeriodDay = new(2027, 5, 3);
    private static readonly DateOnly NextPeriodStart = new(2027, 5, 1);
    private static readonly DateOnly NextPeriodEnd = new(2027, 5, 31);

    private IWorkRepository _workRepository = null!;
    private IOvertimeCascadeService _overtimeCascadeService = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private Guid _clientId;
    private Guid _scenarioClientId;
    private Guid _scenarioToken;

    [SetUp]
    public void SetUp()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _overtimeCascadeService = Substitute.For<IOvertimeCascadeService>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _periodHoursService.GetPeriodBoundariesAsync(Day).Returns((PeriodStart, PeriodEnd));
        _periodHoursService.GetPeriodBoundariesAsync(Day.AddDays(1)).Returns((PeriodStart, PeriodEnd));
        _periodHoursService.GetPeriodBoundariesAsync(NextPeriodDay).Returns((NextPeriodStart, NextPeriodEnd));

        _clientId = Guid.NewGuid();
        _scenarioClientId = Guid.NewGuid();
        _scenarioToken = Guid.NewGuid();
    }

    [Test]
    public async Task ThePeriodHours_AreRecalculatedAfterTheCascade_OncePerClientTokenAndPayPeriod_AndNoRowIsDropped()
    {
        var works = GivenWorks(
            NewWork(_clientId, null, Day),
            NewWork(_clientId, null, Day.AddDays(1)),
            NewWork(_scenarioClientId, _scenarioToken, Day));

        await DeleteAsync(works);

        Received.InOrder(() =>
        {
            _overtimeCascadeService.ReprocessSuccessorsAsync(
                Arg.Is<IReadOnlyCollection<Work>>(deleted => deleted.Count == works.Count));
            _periodHoursService.RecalculatePeriodHoursAsync(_clientId, PeriodStart, PeriodEnd, null);
            _periodHoursService.RecalculatePeriodHoursAsync(_scenarioClientId, PeriodStart, PeriodEnd, _scenarioToken);
        });
        RecalculationCount().ShouldBe(2);
        await _periodHoursService.DidNotReceive().RecalculatePeriodHoursAsync(_scenarioClientId, PeriodStart, PeriodEnd, null);
        await _periodHoursService.DidNotReceiveWithAnyArgs().InvalidateCacheAsync(default, default, default);
    }

    [Test]
    public async Task AClientWithWorksInTwoPayPeriods_IsRecalculatedInEachPeriod_InTheOrderTheWorksWereFound()
    {
        var works = GivenWorks(NewWork(_clientId, null, Day), NewWork(_clientId, null, NextPeriodDay));

        await DeleteAsync(works);

        Received.InOrder(() =>
        {
            _periodHoursService.RecalculatePeriodHoursAsync(_clientId, PeriodStart, PeriodEnd, null);
            _periodHoursService.RecalculatePeriodHoursAsync(_clientId, NextPeriodStart, NextPeriodEnd, null);
        });
        RecalculationCount().ShouldBe(2);
    }

    private List<Work> GivenWorks(params Work[] works)
    {
        var list = works.ToList();
        _workRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>()).Returns(list);
        return list;
    }

    private Task<BulkWorksResponse> DeleteAsync(List<Work> works)
        => NewHandler().Handle(
            new BulkDeleteWorksCommand(new BulkDeleteWorksRequest { WorkIds = works.Select(w => w.Id).ToList() }),
            CancellationToken.None);

    private int RecalculationCount()
        => _periodHoursService.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(IPeriodHoursService.RecalculatePeriodHoursAsync));

    private static Work NewWork(Guid clientId, Guid? analyseToken, DateOnly date) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        ShiftId = Guid.NewGuid(),
        CurrentDate = date,
        AnalyseToken = analyseToken,
        LockLevel = WorkLockLevel.None
    };

    private static IClientVisibilityGuard AllWorksVisible()
    {
        var guard = Substitute.For<IClientVisibilityGuard>();
        guard.FilterVisibleAsync(
                Arg.Any<IReadOnlyCollection<Work>>(), Arg.Any<Func<Work, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<Work>>(0).ToList());
        return guard;
    }

    private BulkDeleteWorksCommandHandler NewHandler() => new(
        _workRepository, TestScheduleRowAccess.Over(AllWorksVisible()), new ScheduleMapper(),
        _periodHoursService, Substitute.For<IScheduleCompletionService>(), Substitute.For<IWorkNotificationFacade>(),
        _overtimeCascadeService, Substitute.For<IContainerWorkCascadeService>(), Substitute.For<IDayLockService>(),
        Substitute.For<IHttpContextAccessor>(), new ParentWorkLockGuard(new WorkLockLevelService()),
        Substitute.For<ILogger<BulkDeleteWorksCommandHandler>>());
}
