// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The bulk delete drops the cached period-hours rows of every deleted Work's day after the overtime cascade, once per
/// client, day and scenario token, so readers fall back to the live sum instead of a row that still counts the deleted
/// Works. The persisted effect is covered by WorkBulkDeletePeriodHoursPostgresTests (Klacks.IntegrationTest).
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

    private IWorkRepository _workRepository = null!;
    private IOvertimeCascadeService _overtimeCascadeService = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private Guid _clientId;
    private Guid _scenarioClientId;
    private Guid _scenarioToken;
    private List<Work> _works = null!;

    [SetUp]
    public void SetUp()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _overtimeCascadeService = Substitute.For<IOvertimeCascadeService>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _periodHoursService.GetPeriodBoundariesAsync(Arg.Any<DateOnly>()).Returns((Day, Day));

        _clientId = Guid.NewGuid();
        _scenarioClientId = Guid.NewGuid();
        _scenarioToken = Guid.NewGuid();
        _works =
        [
            NewWork(_clientId, null),
            NewWork(_clientId, null),
            NewWork(_scenarioClientId, _scenarioToken)
        ];
        _workRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>()).Returns(_works);
    }

    [Test]
    public async Task TheCachedRowsOfEachDeletedDay_AreDroppedAfterTheCascade_OncePerClientDayAndToken()
    {
        await NewHandler().Handle(
            new BulkDeleteWorksCommand(new BulkDeleteWorksRequest { WorkIds = _works.Select(w => w.Id).ToList() }),
            CancellationToken.None);

        Received.InOrder(() =>
        {
            _overtimeCascadeService.ReprocessSuccessorsAsync(Arg.Any<IReadOnlyCollection<Work>>());
            _periodHoursService.InvalidateCacheAsync(_clientId, Day, null);
        });
        await _periodHoursService.Received(1).InvalidateCacheAsync(_clientId, Day, null);
        await _periodHoursService.Received(1).InvalidateCacheAsync(_scenarioClientId, Day, _scenarioToken);
        await _periodHoursService.DidNotReceive().InvalidateCacheAsync(_scenarioClientId, Day, null);
    }

    private static Work NewWork(Guid clientId, Guid? analyseToken) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        ShiftId = Guid.NewGuid(),
        CurrentDate = Day,
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
