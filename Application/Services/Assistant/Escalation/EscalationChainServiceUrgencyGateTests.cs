// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Covers the guard added when CoverAbsenceCommandHandler was wired to start a chain per absence day
/// regardless of how far out the shift is: a far-future report must not wake stage 1 with an
/// artificial "reply within MaxStageMinutes" deadline. The gate compares the deadline against what the
/// roster could reach even fully serial (roster size x MaxStageMinutes), using only values already in
/// EscalationTimeBudget - no separate "is this urgent" constant. Beyond that window the chain is registered
/// right away (all stages Pending, WakeNotBeforeUtc = deadline - window) instead of being dropped, so it shows
/// up in the intervention list and blocks a duplicate; the sweep wakes it later. Also covers the exhaust
/// close-out (an absence chain is reported even when nobody was woken, an approval chain is not) and the
/// re-run of a cover on a shift that already escalates (the running chain is pointed at the newer scenario).
/// </summary>

using Klacks.Api.Application.Services.Assistant.Escalation;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant.Escalation;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Services.Assistant.Escalation;

[TestFixture]
public class EscalationChainServiceUrgencyGateTests
{
    private static readonly Guid WorkId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateTime ReportedAtUtc = new(2026, 8, 16, 3, 0, 0, DateTimeKind.Utc);

    private FakeEscalationChainRepository _repository = null!;
    private IEscalationRosterService _rosterService = null!;
    private IEscalationNotifier _notifier = null!;
    private ISettingsReader _settingsReader = null!;
    private SettableTimeProvider _timeProvider = null!;
    private EscalationChainService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new FakeEscalationChainRepository();
        _rosterService = Substitute.For<IEscalationRosterService>();
        _notifier = Substitute.For<IEscalationNotifier>();
        _settingsReader = Substitute.For<ISettingsReader>();
        _timeProvider = new SettableTimeProvider(ReportedAtUtc);

        _rosterService.GetOrderedRosterAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(new List<EscalationRosterCandidate> { new("planner-a", "Planner A"), new("planner-b", "Planner B"), new("planner-c", "Planner C") });

        // Falls back to 5/30/2h -> reachable window = 3 x 30min = 90min.
        _settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsEntity?)null);

        _sut = new EscalationChainService(
            _repository, _rosterService, _notifier, _settingsReader, Substitute.For<IAgentConditionLedgerService>(), _timeProvider, Substitute.For<ILogger<EscalationChainService>>());
    }

    [Test]
    public async Task FarFutureShift_DeadlineBeyondReachableWindow_RegistersTheChainWithoutWakingAnybody()
    {
        // Shift three weeks out; deadline is far beyond 3 x 30min from now.
        var shiftStartUtc = ReportedAtUtc.AddDays(21);
        var expectedDeadlineUtc = shiftStartUtc.AddHours(-2);

        var chainId = await _sut.StartChainAsync(new StartEscalationChainRequest(
            WorkId, GroupId, ClientId, "Absent Employee", shiftStartUtc, AbsenceBreakId: null));

        Assert.That(chainId, Is.Not.Null);
        var chain = _repository.GetChain(chainId!.Value);
        Assert.That(chain.Status, Is.EqualTo(EscalationChainStatus.Running));
        Assert.That(chain.DeadlineUtc, Is.EqualTo(expectedDeadlineUtc));
        Assert.That(chain.WakeNotBeforeUtc, Is.EqualTo(expectedDeadlineUtc.AddMinutes(-90)));
        Assert.That(chain.Stages, Has.Count.EqualTo(3));
        Assert.That(chain.Stages.All(s => s.Status == EscalationStageStatus.Pending), Is.True);
        await _notifier.DidNotReceive().NotifyStageAsync(
            Arg.Any<EscalationChain>(), Arg.Any<EscalationStage>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FarFutureShift_RegisteredChain_BlocksASecondChainForTheSameShift()
    {
        var shiftStartUtc = ReportedAtUtc.AddDays(21);
        var request = new StartEscalationChainRequest(WorkId, GroupId, ClientId, "Absent Employee", shiftStartUtc, AbsenceBreakId: null);

        var first = await _sut.StartChainAsync(request);
        var second = await _sut.StartChainAsync(request);

        Assert.That(first, Is.Not.Null);
        Assert.That(second, Is.Null);
    }

    [Test]
    public async Task ReRunOnAShiftThatAlreadyEscalates_PointsTheRunningChainAtTheNewerScenario()
    {
        var shiftStartUtc = ReportedAtUtc.AddDays(21);
        var olderScenario = Guid.NewGuid();
        var newerScenario = Guid.NewGuid();

        var chainId = await _sut.StartChainAsync(new StartEscalationChainRequest(
            WorkId, GroupId, ClientId, "Absent Employee", shiftStartUtc, AbsenceBreakId: null, olderScenario));
        var again = await _sut.StartChainAsync(new StartEscalationChainRequest(
            WorkId, GroupId, ClientId, "Absent Employee", shiftStartUtc, AbsenceBreakId: null, newerScenario));

        Assert.That(again, Is.Null);
        Assert.That(_repository.GetChain(chainId!.Value).ScenarioId, Is.EqualTo(newerScenario));
    }

    [Test]
    public async Task ReRunWithoutAScenario_LeavesTheRunningChainsScenarioAlone()
    {
        var repository = Substitute.For<IEscalationChainRepository>();
        repository.AddAsync(Arg.Any<EscalationChain>(), Arg.Any<CancellationToken>()).Returns(false);
        var sut = new EscalationChainService(
            repository, _rosterService, _notifier, _settingsReader, Substitute.For<IAgentConditionLedgerService>(), _timeProvider,
            Substitute.For<ILogger<EscalationChainService>>());

        var result = await sut.StartChainAsync(new StartEscalationChainRequest(
            WorkId, GroupId, ClientId, "Absent Employee", ReportedAtUtc.AddDays(21), AbsenceBreakId: null));

        Assert.That(result, Is.Null);
        await repository.DidNotReceiveWithAnyArgs().TryUpdateRunningChainScenarioAsync(default, default, default);
    }

    [Test]
    public async Task RegisteredChain_ForceExhaustedBeforeAnybodyWasWoken_StillReportsTheAbsenceAndStampsTheTime()
    {
        var chainId = (await _sut.StartChainAsync(new StartEscalationChainRequest(
            WorkId, GroupId, ClientId, "Absent Employee", ReportedAtUtc.AddDays(21), AbsenceBreakId: null)))!.Value;
        var exhaustedAtUtc = ReportedAtUtc.AddDays(2);
        _timeProvider.Now = exhaustedAtUtc;

        Assert.That(await _sut.ForceExhaustAsync(chainId, "test"), Is.True);

        Assert.That(_repository.GetChain(chainId).ExhaustedAtUtc, Is.EqualTo(exhaustedAtUtc));
        await _notifier.Received(1).NotifyExhaustedAsync(
            Arg.Is<EscalationChain>(c => c.Id == chainId),
            Arg.Is<IReadOnlyList<EscalationStage>>(stages => stages.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EmptyRoster_AbsenceChain_IsExhaustedAndReported()
    {
        _rosterService.GetOrderedRosterAsync(GroupId, Arg.Any<CancellationToken>()).Returns(new List<EscalationRosterCandidate>());

        var chainId = await _sut.StartChainAsync(new StartEscalationChainRequest(
            WorkId, GroupId, ClientId, "Absent Employee", ReportedAtUtc.AddHours(3), AbsenceBreakId: null));

        Assert.That(_repository.GetChain(chainId!.Value).Status, Is.EqualTo(EscalationChainStatus.Exhausted));
        Assert.That(_repository.GetChain(chainId.Value).ExhaustedAtUtc, Is.EqualTo(ReportedAtUtc));
        await _notifier.Received(1).NotifyExhaustedAsync(
            Arg.Is<EscalationChain>(c => c.Id == chainId), Arg.Is<IReadOnlyList<EscalationStage>>(s => s.Count == 0), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EmptyRoster_ApprovalChain_IsExhaustedSilently()
    {
        var chainId = await _sut.StartConditionApprovalChainAsync(new StartConditionApprovalChainRequest(
            Guid.NewGuid(), GroupId, new List<EscalationRosterCandidate>(), ReportedAtUtc.AddHours(1)));

        Assert.That(_repository.GetChain(chainId!.Value).Status, Is.EqualTo(EscalationChainStatus.Exhausted));
        await _notifier.DidNotReceiveWithAnyArgs().NotifyExhaustedAsync(default!, default!, default);
    }

    [Test]
    public async Task DeadlineExactlyAtReachableWindow_ChainStarts()
    {
        // Deadline - now == 90min == reachable window exactly: the boundary must still start (>, not >=).
        var shiftStartUtc = ReportedAtUtc.AddHours(2).AddMinutes(90);
        _notifier.NotifyStageAsync(Arg.Any<EscalationChain>(), Arg.Any<EscalationStage>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new EscalationNotificationResult(OfflineMessengerDeliveryOutcome.Sent, Guid.NewGuid(), "Inbox"));

        var chainId = await _sut.StartChainAsync(new StartEscalationChainRequest(
            WorkId, GroupId, ClientId, "Absent Employee", shiftStartUtc, AbsenceBreakId: null));

        Assert.That(chainId, Is.Not.Null);
        Assert.That(_repository.GetChain(chainId!.Value).WakeNotBeforeUtc, Is.Null);
        Assert.That(_repository.GetChain(chainId.Value).Stages.Single(s => s.Rank == 1).Status, Is.EqualTo(EscalationStageStatus.Notified));
    }
}
