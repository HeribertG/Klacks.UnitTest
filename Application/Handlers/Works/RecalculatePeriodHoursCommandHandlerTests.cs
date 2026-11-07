// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// RecalculatePeriodHoursCommandHandler stores the period hours of all clients once per real pay period its window
/// touches, never over the requested window itself, while the breaks are reprocessed and the reload is announced over
/// the requested window. A window the pay-period split refuses is refused before anything is written. Frozen client
/// periods are skipped and counted; the admin override needs a reason, includes them and writes one audit row with the
/// number of distinct frozen clients before anything is recalculated; a reason longer than the audit column is refused first.
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
public class RecalculatePeriodHoursCommandHandlerTests
{
    private static readonly DateOnly WindowStart = new(2026, 6, 10);
    private static readonly DateOnly WindowEnd = new(2026, 7, 5);
    private static readonly (DateOnly StartDate, DateOnly EndDate) JunePeriod = (new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
    private static readonly (DateOnly StartDate, DateOnly EndDate) JulyPeriod = (new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

    private const string AdminId = "admin-id";
    private const string AdminName = "Ada Admin";
    private static readonly Guid FirstFrozenClient = Guid.NewGuid();
    private static readonly Guid SecondFrozenClient = Guid.NewGuid();

    private IPeriodHoursService _periodHoursService = null!;
    private IWorkNotificationService _notificationService = null!;
    private IBreakMacroService _breakMacroService = null!;
    private IPeriodAuditLogRepository _auditLogRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IUserService _userService = null!;

    [SetUp]
    public void SetUp()
    {
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _notificationService = Substitute.For<IWorkNotificationService>();
        _breakMacroService = Substitute.For<IBreakMacroService>();
        _auditLogRepository = Substitute.For<IPeriodAuditLogRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _userService = Substitute.For<IUserService>();
        _userService.GetIdString().Returns(AdminId);
        _userService.GetDisplayName().Returns(AdminName);
        _periodHoursService.GetPeriodsInRangeAsync(WindowStart, WindowEnd)
            .Returns(new List<(DateOnly StartDate, DateOnly EndDate)> { JunePeriod, JulyPeriod });
    }

    [Test]
    public async Task WindowOverTwoPayPeriods_RecalculatesAllClientsOncePerPayPeriod_NeverOverTheWindow()
    {
        var groupId = Guid.NewGuid();

        var result = await NewHandler().Handle(
            new RecalculatePeriodHoursCommand(WindowStart, WindowEnd, groupId), CancellationToken.None);

        result.ShouldBe(new PeriodHoursRecalculationResult(true, 0));
        Received.InOrder(() =>
        {
            _breakMacroService.ReprocessAllBreaksAsync(WindowStart, WindowEnd);
            _periodHoursService.RecalculateAllClientsAsync(JunePeriod.StartDate, JunePeriod.EndDate, groupId);
            _periodHoursService.RecalculateAllClientsAsync(JulyPeriod.StartDate, JulyPeriod.EndDate, groupId);
            _notificationService.NotifyPeriodHoursRecalculated(WindowStart, WindowEnd, null);
        });
        await _periodHoursService.DidNotReceive().RecalculateAllClientsAsync(WindowStart, WindowEnd, groupId);
    }

    [Test]
    public async Task ARefusedWindow_IsRefusedBeforeTheBreaksAreReprocessed_AndNothingIsWritten()
    {
        _periodHoursService.GetPeriodsInRangeAsync(WindowStart, WindowEnd)
            .Returns<IReadOnlyList<(DateOnly StartDate, DateOnly EndDate)>>(_ => throw new InvalidRequestException("too many periods"));

        await Should.ThrowAsync<InvalidRequestException>(() => NewHandler().Handle(
            new RecalculatePeriodHoursCommand(WindowStart, WindowEnd, null), CancellationToken.None));

        await _breakMacroService.DidNotReceiveWithAnyArgs().ReprocessAllBreaksAsync(default, default);
        await _periodHoursService.DidNotReceiveWithAnyArgs().RecalculateAllClientsAsync(default, default);
    }

    [Test]
    public async Task WithoutConfirmation_FrozenClientPeriodsAreSkippedAndCounted_AndNothingIsAudited()
    {
        StubFrozen(includeFrozen: false);

        var result = await NewHandler().Handle(
            new RecalculatePeriodHoursCommand(WindowStart, WindowEnd), CancellationToken.None);

        result.ShouldBe(new PeriodHoursRecalculationResult(true, 3));
        await _auditLogRepository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _periodHoursService.DidNotReceive().RecalculateAllClientsAsync(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), true);
    }

    [Test]
    public async Task WithConfirmation_FrozenPeriodsAreIncluded_AndOneAuditRowCountsTheDistinctFrozenClients()
    {
        var groupId = Guid.NewGuid();
        StubFrozen(includeFrozen: true, groupId);

        var result = await NewHandler().Handle(
            new RecalculatePeriodHoursCommand(WindowStart, WindowEnd, groupId, ConfirmFrozen: true, Reason: "  correction  "),
            CancellationToken.None);

        result.ShouldBe(new PeriodHoursRecalculationResult(true, 0));
        await _auditLogRepository.Received(1).AddAsync(
            Arg.Is<PeriodAuditLog>(log => log.Action == PeriodAuditAction.RecalculateExportedPeriod
                && log.StartDate == WindowStart
                && log.EndDate == WindowEnd
                && log.GroupId == groupId
                && log.Reason == "correction"
                && log.AffectedCount == 2
                && log.PerformedBy == AdminId
                && log.PerformedByName == AdminName),
            CancellationToken.None);
        await _unitOfWork.Received(1).CompleteAsync();
        Received.InOrder(() =>
        {
            _unitOfWork.CompleteAsync();
            _breakMacroService.ReprocessAllBreaksAsync(WindowStart, WindowEnd);
            _periodHoursService.RecalculateAllClientsAsync(JunePeriod.StartDate, JunePeriod.EndDate, groupId, null, true);
        });
    }

    [Test]
    public async Task WithConfirmation_AFailingAuditWrite_StopsTheOverrideBeforeAnythingIsRecalculated()
    {
        StubFrozen(includeFrozen: true);
        _unitOfWork.CompleteAsync().Returns(_ => Task.FromException(new DatabaseUpdateException("audit insert failed")));

        await Should.ThrowAsync<Exception>(() => NewHandler().Handle(
            new RecalculatePeriodHoursCommand(WindowStart, WindowEnd, ConfirmFrozen: true, Reason: "correction"),
            CancellationToken.None));

        await _breakMacroService.DidNotReceiveWithAnyArgs().ReprocessAllBreaksAsync(default, default);
        await _periodHoursService.DidNotReceiveWithAnyArgs().RecalculateAllClientsAsync(default, default);
    }

    [Test]
    public async Task WithConfirmation_AReasonLongerThanTheAuditColumn_IsRefusedBeforeAnythingIsWritten()
    {
        StubFrozen(includeFrozen: true);
        var reason = new string('x', PeriodAuditLimits.ReasonMaxLength + 1);

        await Should.ThrowAsync<InvalidRequestException>(() => NewHandler().Handle(
            new RecalculatePeriodHoursCommand(WindowStart, WindowEnd, ConfirmFrozen: true, Reason: reason),
            CancellationToken.None));

        await _auditLogRepository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _breakMacroService.DidNotReceiveWithAnyArgs().ReprocessAllBreaksAsync(default, default);
        await _periodHoursService.DidNotReceiveWithAnyArgs().RecalculateAllClientsAsync(default, default);
    }

    [Test]
    public async Task WithConfirmation_AReasonOfExactlyTheAuditColumnLength_IsAccepted()
    {
        StubFrozen(includeFrozen: true);
        var reason = new string('x', PeriodAuditLimits.ReasonMaxLength);

        var result = await NewHandler().Handle(
            new RecalculatePeriodHoursCommand(WindowStart, WindowEnd, ConfirmFrozen: true, Reason: reason),
            CancellationToken.None);

        result.ShouldBe(new PeriodHoursRecalculationResult(true, 0));
        await _auditLogRepository.ReceivedWithAnyArgs(1).AddAsync(default!, default);
    }

    [Test]
    public async Task WithConfirmation_AndNoFrozenPeriod_NothingIsAudited()
    {
        var result = await NewHandler().Handle(
            new RecalculatePeriodHoursCommand(WindowStart, WindowEnd, ConfirmFrozen: true, Reason: "correction"),
            CancellationToken.None);

        result.ShouldBe(new PeriodHoursRecalculationResult(true, 0));
        await _auditLogRepository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [TestCase(null)]
    [TestCase("   ")]
    public async Task WithConfirmation_ABlankReason_IsRefusedBeforeAnythingIsWritten(string? reason)
    {
        await Should.ThrowAsync<InvalidRequestException>(() => NewHandler().Handle(
            new RecalculatePeriodHoursCommand(WindowStart, WindowEnd, ConfirmFrozen: true, Reason: reason),
            CancellationToken.None));

        await _breakMacroService.DidNotReceiveWithAnyArgs().ReprocessAllBreaksAsync(default, default);
        await _periodHoursService.DidNotReceiveWithAnyArgs().RecalculateAllClientsAsync(default, default);
    }

    private void StubFrozen(bool includeFrozen, Guid? groupId = null)
    {
        _periodHoursService.RecalculateAllClientsAsync(JunePeriod.StartDate, JunePeriod.EndDate, groupId, null, includeFrozen)
            .Returns(new HashSet<Guid> { FirstFrozenClient });
        _periodHoursService.RecalculateAllClientsAsync(JulyPeriod.StartDate, JulyPeriod.EndDate, groupId, null, includeFrozen)
            .Returns(new HashSet<Guid> { FirstFrozenClient, SecondFrozenClient });
        _periodHoursService.GetFrozenClientIdsAsync(JunePeriod.StartDate, JunePeriod.EndDate, groupId, null)
            .Returns(new HashSet<Guid> { FirstFrozenClient });
        _periodHoursService.GetFrozenClientIdsAsync(JulyPeriod.StartDate, JulyPeriod.EndDate, groupId, null)
            .Returns(new HashSet<Guid> { FirstFrozenClient, SecondFrozenClient });
    }

    private RecalculatePeriodHoursCommandHandler NewHandler() => new(
        _periodHoursService,
        _notificationService,
        _breakMacroService,
        new FrozenPeriodOverrideAudit(_auditLogRepository, _unitOfWork, _userService),
        Substitute.For<ILogger<RecalculatePeriodHoursCommandHandler>>());
}
