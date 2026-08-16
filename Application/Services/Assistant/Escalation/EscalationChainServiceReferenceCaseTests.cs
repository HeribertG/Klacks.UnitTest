// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The Prüfstein from docs/START-messenger-naechste-session.md and
/// docs/ENTWURF-eskalationskette-2026-08-16.md §6, played back against EscalationChainService
/// directly (not the background service, which only adds a DB-backed sweep loop around the same
/// calls - see EscalationChainBackgroundService.ExpireDueStagesAsync). Roster A(1)/B(2)/C(3), a
/// 2-hour prep buffer and the default 5-30 minute caps reproduce the Entwurf's own numbers exactly:
/// 03:00 report for a 06:00 shift -> deadline 04:00 -> 20-minute serial stages.
/// IEscalationChainRepository is a hand-written in-memory fake, not a database: the conditional
/// ExecuteUpdate's actual single-winner race semantics are unproven here on purpose, matching every
/// other ExecuteUpdateAsync-based repository in this suite (see
/// ProactiveTriggerDispatchRepositoryTests) - the EF in-memory provider does not support
/// ExecuteUpdateAsync, and this project has no SQLite/real-Postgres harness for it in Klacks.UnitTest.
/// What this test proves instead, and what nobody exercised for the original AgentPlan design (the
/// reason E62 failed silently): the ORCHESTRATION sequence - serial handoff timing, the rolling
/// deadline, and above all that C is never notified once B acknowledges.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Escalation;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant.Escalation;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Services.Assistant.Escalation;

[TestFixture]
public class EscalationChainServiceReferenceCaseTests
{
    private static readonly Guid WorkId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateTime ReportedAtUtc = new(2026, 8, 16, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ShiftStartUtc = new(2026, 8, 16, 6, 0, 0, DateTimeKind.Utc);

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
            .Returns(new List<EscalationRosterCandidate>
            {
                new("planner-a", "Planner A"),
                new("planner-b", "Planner B"),
                new("planner-c", "Planner C")
            });

        // No settings rows configured -> EscalationTimeBudgetReader falls back to 5/30/2h, exactly
        // the Entwurf's own worked numbers.
        _settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsEntity?)null);

        _notifier.NotifyStageAsync(Arg.Any<EscalationChain>(), Arg.Any<EscalationStage>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new EscalationNotificationResult(OfflineMessengerDeliveryOutcome.Sent, Guid.NewGuid(), "Messenger"));

        _sut = new EscalationChainService(
            _repository, _rosterService, _notifier, _settingsReader, _timeProvider, Substitute.For<ILogger<EscalationChainService>>());
    }

    [Test]
    public async Task ReferenceCase_AExpires_BAcknowledges_CIsNeverNotified()
    {
        // 03:00:00 - report arrives, chain starts. Deadline = 06:00 - 2h = 04:00.
        var chainId = await _sut.StartChainAsync(new StartEscalationChainRequest(
            WorkId, GroupId, ClientId, "Absent Employee", ShiftStartUtc, AbsenceBreakId: null));

        var stageA = _repository.GetStage(chainId, "planner-a");
        var stageB = _repository.GetStage(chainId, "planner-b");
        var stageC = _repository.GetStage(chainId, "planner-c");

        Assert.That(stageA.Status, Is.EqualTo(EscalationStageStatus.Notified), "A must be the only one notified at chain start.");
        Assert.That(stageB.Status, Is.EqualTo(EscalationStageStatus.Pending));
        Assert.That(stageC.Status, Is.EqualTo(EscalationStageStatus.Pending));
        Assert.That(stageA.DueAtUtc, Is.EqualTo(ReportedAtUtc.AddMinutes(20)), "T=60min / N=3 -> 20 minutes.");

        await _notifier.Received(1).NotifyStageAsync(
            Arg.Any<EscalationChain>(), Arg.Is<EscalationStage>(s => s.UserId == "planner-a"), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());

        // 03:20 - A never answered. The sweep would expire the due stage then call AdvanceAsync;
        // reproduced directly here since the background service adds no logic of its own for this.
        _timeProvider.Now = ReportedAtUtc.AddMinutes(20);
        Assert.That(await _repository.TryExpireStageAsync(stageA.Id), Is.True);
        await _sut.AdvanceAsync(chainId);

        stageB = _repository.GetStage(chainId, "planner-b");
        Assert.That(stageB.Status, Is.EqualTo(EscalationStageStatus.Notified), "B must be notified once A expires.");
        Assert.That(stageB.DueAtUtc, Is.EqualTo(_timeProvider.Now.AddMinutes(20)), "Rest 40min / 2 stages -> 20 minutes (B3 rolling deadline).");
        Assert.That(_repository.GetStage(chainId, "planner-c").Status, Is.EqualTo(EscalationStageStatus.Pending), "C must still be untouched.");

        // 03:26 - B replies "Ich übernehme".
        _timeProvider.Now = ReportedAtUtc.AddMinutes(26);
        var acknowledged = await _sut.AcknowledgeAsync("planner-b");

        Assert.That(acknowledged, Is.True);
        Assert.That(_repository.GetChain(chainId).Status, Is.EqualTo(EscalationChainStatus.Acknowledged));
        Assert.That(_repository.GetChain(chainId).AcknowledgedByUserId, Is.EqualTo("planner-b"));
        Assert.That(_repository.GetStage(chainId, "planner-b").Status, Is.EqualTo(EscalationStageStatus.Acknowledged));

        // The assertion E62 never had: C's stage was never handed to the notifier, at any point.
        await _notifier.DidNotReceive().NotifyStageAsync(
            Arg.Any<EscalationChain>(), Arg.Is<EscalationStage>(s => s.UserId == "planner-c"), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        Assert.That(_repository.GetStage(chainId, "planner-c").Status, Is.EqualTo(EscalationStageStatus.Cancelled), "C ends Cancelled, never Notified.");

        // The quiet handoff note goes to A (was notified, did not answer) and not to C (never notified).
        await _notifier.Received(1).NotifyHandoffAsync(
            Arg.Any<EscalationChain>(),
            Arg.Is<EscalationStage>(s => s.UserId == "planner-b"),
            Arg.Is<IReadOnlyList<EscalationStage>>(list => list.Any(s => s.UserId == "planner-a") && list.All(s => s.UserId != "planner-c")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ParallelProbe_LateReport_AllThreeNotifiedAtOnce()
    {
        // Meldung 03:50 für 06:00, Puffer 2h -> Deadline 04:00 -> T = 10min, T/3 < 5min -> parallel.
        _timeProvider.Now = ShiftStartUtc.AddHours(-2).AddMinutes(-10);

        var chainId = await _sut.StartChainAsync(new StartEscalationChainRequest(
            WorkId, GroupId, ClientId, "Absent Employee", ShiftStartUtc, AbsenceBreakId: null));

        Assert.That(_repository.GetStage(chainId, "planner-a").Status, Is.EqualTo(EscalationStageStatus.Notified));
        Assert.That(_repository.GetStage(chainId, "planner-b").Status, Is.EqualTo(EscalationStageStatus.Notified));
        Assert.That(_repository.GetStage(chainId, "planner-c").Status, Is.EqualTo(EscalationStageStatus.Notified));

        await _notifier.Received(3).NotifyStageAsync(
            Arg.Any<EscalationChain>(), Arg.Any<EscalationStage>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }
}
