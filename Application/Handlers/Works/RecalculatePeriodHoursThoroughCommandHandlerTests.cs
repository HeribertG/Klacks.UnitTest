// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// RecalculatePeriodHoursThoroughCommandHandler counts the frozen client periods of the window before queuing. Without
/// confirmation it queues without the override and reports the count; with confirmation and a reason it queues with the
/// override and audits the distinct frozen clients once the request was accepted; a blank reason is refused before
/// anything is queued. An unrestricted caller queues without a client scope; a group-restricted caller (planner) queues
/// with the clients they may see as the scope, and when they may see nobody nothing is queued and the answer says so
/// without any completion broadcast (an empty scope would mean every client to the queue). Without the admin role a
/// window over PayPeriodLimits.MaxPeriodsForPlannerRecalculation pay periods is refused before anything is queued.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class RecalculatePeriodHoursThoroughCommandHandlerTests
{
    private static readonly DateOnly WindowStart = new(2099, 1, 10);
    private static readonly DateOnly WindowEnd = new(2099, 2, 5);
    private static readonly (DateOnly StartDate, DateOnly EndDate) January = (new DateOnly(2099, 1, 1), new DateOnly(2099, 1, 31));
    private static readonly (DateOnly StartDate, DateOnly EndDate) February = (new DateOnly(2099, 2, 1), new DateOnly(2099, 2, 28));
    private static readonly Guid FrozenClient = Guid.NewGuid();
    private static readonly Guid OtherFrozenClient = Guid.NewGuid();
    private static readonly Guid VisibleClient = Guid.NewGuid();

    private IThoroughRecalculationQueue _queue = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IPeriodAuditLogRepository _auditLogRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IUserService _userService = null!;

    [SetUp]
    public void SetUp()
    {
        _queue = Substitute.For<IThoroughRecalculationQueue>();
        _queue.QueueRecalculation(default, default, default, default, default, default).ReturnsForAnyArgs(true);
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _periodHoursService.GetPeriodsInRangeAsync(WindowStart, WindowEnd)
            .Returns(new List<(DateOnly StartDate, DateOnly EndDate)> { January, February });
        _periodHoursService.GetFrozenClientIdsAsync(January.StartDate, January.EndDate, null, null)
            .Returns(new HashSet<Guid> { FrozenClient, OtherFrozenClient });
        _periodHoursService.GetFrozenClientIdsAsync(February.StartDate, February.EndDate, null, null)
            .Returns(new HashSet<Guid> { FrozenClient });
        _auditLogRepository = Substitute.For<IPeriodAuditLogRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _userService = Substitute.For<IUserService>();
        _periodHoursService.GetRestrictedCallerClientIdsAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>())
            .Returns((IReadOnlyList<Guid>?)null);
    }

    [Test]
    public async Task ARestrictedCaller_QueuesWithTheClientsTheyMaySeeAsTheScope()
    {
        _periodHoursService.GetRestrictedCallerClientIdsAsync(WindowStart, WindowEnd, null)
            .Returns(new List<Guid> { VisibleClient, FrozenClient });

        var result = await NewHandler().Handle(
            new RecalculatePeriodHoursThoroughCommand(WindowStart, WindowEnd), CancellationToken.None);

        result.ShouldBe(new PeriodHoursRecalculationResult(true, 3));
        _queue.Received(1).QueueRecalculation(
            WindowStart,
            WindowEnd,
            null,
            null,
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { VisibleClient, FrozenClient })),
            false,
            ownWorkChangesOnly: true);
    }

    [Test]
    public async Task ARestrictedCallerWhoSeesNobody_QueuesNothing_AndSaysThereWasNothingToRecalculate()
    {
        var groupId = Guid.NewGuid();
        var token = Guid.NewGuid();
        _periodHoursService.GetRestrictedCallerClientIdsAsync(WindowStart, WindowEnd, groupId)
            .Returns(new List<Guid>());

        var result = await NewHandler().Handle(
            new RecalculatePeriodHoursThoroughCommand(WindowStart, WindowEnd, groupId, token), CancellationToken.None);

        result.ShouldBe(new PeriodHoursRecalculationResult(true, 0, NothingToRecalculate: true));
        _queue.DidNotReceiveWithAnyArgs().QueueRecalculation(default, default, default, default, default, default);
    }

    [Test]
    public async Task ANonAdmin_WindowOverThePlannerCap_IsRefusedBeforeAnythingIsQueued()
    {
        var periods = MonthlyPeriods(PayPeriodLimits.MaxPeriodsForPlannerRecalculation + 1);
        _periodHoursService.GetPeriodsInRangeAsync(periods[0].StartDate, periods[^1].EndDate).Returns(periods);

        await Should.ThrowAsync<InvalidRequestException>(() => NewHandler().Handle(
            new RecalculatePeriodHoursThoroughCommand(periods[0].StartDate, periods[^1].EndDate), CancellationToken.None));

        _queue.DidNotReceiveWithAnyArgs().QueueRecalculation(default, default, default, default, default, default);
        await _periodHoursService.DidNotReceiveWithAnyArgs().GetFrozenClientIdsAsync(default, default);
    }

    [Test]
    public async Task ANonAdmin_WindowOfExactlyThePlannerCap_IsQueued()
    {
        var periods = MonthlyPeriods(PayPeriodLimits.MaxPeriodsForPlannerRecalculation);
        _periodHoursService.GetPeriodsInRangeAsync(periods[0].StartDate, periods[^1].EndDate).Returns(periods);

        var result = await NewHandler().Handle(
            new RecalculatePeriodHoursThoroughCommand(periods[0].StartDate, periods[^1].EndDate), CancellationToken.None);

        result.Accepted.ShouldBeTrue();
        _queue.ReceivedWithAnyArgs(1).QueueRecalculation(default, default, default, default, default, default);
    }

    [Test]
    public async Task AnAdmin_WindowOverThePlannerCap_IsQueued()
    {
        _userService.IsAdmin().Returns(true);
        var periods = MonthlyPeriods(PayPeriodLimits.MaxPeriodsForPlannerRecalculation + 1);
        _periodHoursService.GetPeriodsInRangeAsync(periods[0].StartDate, periods[^1].EndDate).Returns(periods);

        var result = await NewHandler().Handle(
            new RecalculatePeriodHoursThoroughCommand(periods[0].StartDate, periods[^1].EndDate), CancellationToken.None);

        result.Accepted.ShouldBeTrue();
        _queue.ReceivedWithAnyArgs(1).QueueRecalculation(default, default, default, default, default, default);
    }

    [Test]
    public async Task WithoutConfirmation_QueuesWithoutTheOverride_AndReportsTheFrozenClientPeriods()
    {
        var result = await NewHandler().Handle(
            new RecalculatePeriodHoursThoroughCommand(WindowStart, WindowEnd), CancellationToken.None);

        result.ShouldBe(new PeriodHoursRecalculationResult(true, 3));
        _queue.Received(1).QueueRecalculation(WindowStart, WindowEnd, null, null, null, false, ownWorkChangesOnly: false);
        await _auditLogRepository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Test]
    public async Task WithConfirmation_QueuesWithTheOverride_AndAuditsTheDistinctFrozenClients()
    {
        var result = await NewHandler().Handle(
            new RecalculatePeriodHoursThoroughCommand(WindowStart, WindowEnd, ConfirmFrozen: true, Reason: "late correction"),
            CancellationToken.None);

        result.ShouldBe(new PeriodHoursRecalculationResult(true, 0));
        _queue.Received(1).QueueRecalculation(WindowStart, WindowEnd, null, null, null, true);
        await _auditLogRepository.Received(1).AddAsync(
            Arg.Is<PeriodAuditLog>(log => log.Action == PeriodAuditAction.RecalculateExportedPeriod
                && log.AffectedCount == 2
                && log.Reason == "late correction"),
            CancellationToken.None);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task WithConfirmation_ARejectedRequest_IsNotAudited()
    {
        _queue.QueueRecalculation(default, default, default, default, default, default).ReturnsForAnyArgs(false);

        var result = await NewHandler().Handle(
            new RecalculatePeriodHoursThoroughCommand(WindowStart, WindowEnd, ConfirmFrozen: true, Reason: "late correction"),
            CancellationToken.None);

        result.Accepted.ShouldBeFalse();
        await _auditLogRepository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Test]
    public async Task WithConfirmation_ABlankReason_IsRefusedBeforeAnythingIsQueued()
    {
        await Should.ThrowAsync<InvalidRequestException>(() => NewHandler().Handle(
            new RecalculatePeriodHoursThoroughCommand(WindowStart, WindowEnd, ConfirmFrozen: true, Reason: " "),
            CancellationToken.None));

        _queue.DidNotReceiveWithAnyArgs().QueueRecalculation(default, default, default, default, default, default);
    }

    [Test]
    public async Task WithConfirmation_AReasonLongerThanTheAuditColumn_IsRefusedBeforeAnythingIsQueued()
    {
        await Should.ThrowAsync<InvalidRequestException>(() => NewHandler().Handle(
            new RecalculatePeriodHoursThoroughCommand(
                WindowStart, WindowEnd, ConfirmFrozen: true, Reason: new string('x', PeriodAuditLimits.ReasonMaxLength + 1)),
            CancellationToken.None));

        _queue.DidNotReceiveWithAnyArgs().QueueRecalculation(default, default, default, default, default, default);
    }

    private RecalculatePeriodHoursThoroughCommandHandler NewHandler() => new(
        _queue,
        _periodHoursService,
        new FrozenPeriodOverrideAudit(_auditLogRepository, _unitOfWork, Substitute.For<IUserService>()),
        _userService,
        Substitute.For<ILogger<RecalculatePeriodHoursThoroughCommandHandler>>());

    private static List<(DateOnly StartDate, DateOnly EndDate)> MonthlyPeriods(int count) =>
        Enumerable.Range(0, count)
            .Select(offset =>
            {
                var start = new DateOnly(2090, 1, 1).AddMonths(offset);
                return (start, start.AddMonths(1).AddDays(-1));
            })
            .ToList();
}
