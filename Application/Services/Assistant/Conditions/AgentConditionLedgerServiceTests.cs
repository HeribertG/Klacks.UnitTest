// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for AgentConditionLedgerService: the lifecycle guard over the full 7x7 status matrix, the
/// upsert/re-arm rules, and resolve detection. The expected legal transitions are restated literally in
/// this file rather than read from AgentConditionStateMachine, so the matrix test checks the production
/// table instead of agreeing with it.
///
/// Scope note: these run against FakeAgentConditionRepository, which reproduces the repository's
/// observable contract but not ExecuteUpdateAsync, its transaction, or real row-level concurrency. What
/// is proven here is that a lost claim is handled correctly by the service - it reports false and writes
/// no audit event. That a claim can be lost at all under genuine concurrency is proven against
/// PostgreSQL in Klacks.IntegrationTest/Infrastructure/Repositories/AgentConditionRepositoryCasTests.cs.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Conditions;

[TestFixture]
public class AgentConditionLedgerServiceTests
{
    private const string Kind = "empty_container";
    private const string OtherKind = "open_order";
    private const string Severity = "high";
    private const string Payload = "{\"containerId\":\"x\"}";
    private const string StalePayload = "{\"shiftId\":\"x\"}";
    private const string RefreshedPayload = "{\"shiftId\":\"x\",\"isoWeekdays\":[1,3]}";

    private static readonly DateTime StartUtc = new(2026, 8, 24, 6, 0, 0, DateTimeKind.Utc);

    private static readonly (AgentConditionStatus From, AgentConditionStatus To)[] LegalTransitions =
    [
        (AgentConditionStatus.Detected, AgentConditionStatus.Reported),
        (AgentConditionStatus.Detected, AgentConditionStatus.Resolved),
        (AgentConditionStatus.Reported, AgentConditionStatus.Prepared),
        (AgentConditionStatus.Reported, AgentConditionStatus.Rejected),
        (AgentConditionStatus.Reported, AgentConditionStatus.Resolved),
        (AgentConditionStatus.Reported, AgentConditionStatus.Escalated),
        (AgentConditionStatus.Prepared, AgentConditionStatus.Executed),
        (AgentConditionStatus.Prepared, AgentConditionStatus.Rejected),
        (AgentConditionStatus.Prepared, AgentConditionStatus.Resolved),
        (AgentConditionStatus.Prepared, AgentConditionStatus.Escalated)
    ];

    private FakeAgentConditionRepository _repository = null!;
    private SettableTimeProvider _timeProvider = null!;
    private AgentConditionLedgerService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new FakeAgentConditionRepository();
        _timeProvider = new SettableTimeProvider(StartUtc);
        _service = new AgentConditionLedgerService(
            _repository,
            _timeProvider,
            Substitute.For<ILogger<AgentConditionLedgerService>>());
    }

    private static IEnumerable<TestCaseData> AllStatusPairs()
    {
        foreach (var from in Enum.GetValues<AgentConditionStatus>())
        {
            foreach (var to in Enum.GetValues<AgentConditionStatus>())
            {
                yield return new TestCaseData(from, to).SetName($"Transition_{from}_To_{to}");
            }
        }
    }

    [Test]
    public void TheMatrixUnderTestCoversEveryStatusPair()
    {
        var statusCount = Enum.GetValues<AgentConditionStatus>().Length;

        statusCount.ShouldBe(7);
        AllStatusPairs().Count().ShouldBe(49);
        LegalTransitions.Length.ShouldBe(10);
    }

    [Test]
    public void OpenAndTerminalStatuses_PartitionTheEnum_WithNoOverlapAndNoGap()
    {
        var open = AgentConditionStateMachine.OpenStatuses;
        var terminal = AgentConditionStateMachine.TerminalStatuses;

        open.Intersect(terminal).ShouldBeEmpty();
        open.Concat(terminal).OrderBy(status => (int)status)
            .ShouldBe(Enum.GetValues<AgentConditionStatus>().OrderBy(status => (int)status));
        terminal.ShouldAllBe(status => !AgentConditionStateMachine.IsOpen(status));
        open.ShouldAllBe(status => AgentConditionStateMachine.IsOpen(status));
    }

    [Test]
    public void TerminalStatuses_AreExactlyTheStatusesWithNoOutgoingTransition()
    {
        foreach (var status in AgentConditionStateMachine.TerminalStatuses)
        {
            LegalTransitions.ShouldNotContain(pair => pair.From == status);
        }

        foreach (var status in AgentConditionStateMachine.OpenStatuses)
        {
            LegalTransitions.ShouldContain(pair => pair.From == status);
        }
    }

    [TestCaseSource(nameof(AllStatusPairs))]
    public async Task TryTransition_AppliesExactlyTheLegalPairsAndRejectsEveryOtherOne(
        AgentConditionStatus fromStatus,
        AgentConditionStatus toStatus)
    {
        var condition = _repository.Seed(Kind, "fp-matrix", fromStatus, StartUtc);
        var isLegal = LegalTransitions.Contains((fromStatus, toStatus));

        if (isLegal)
        {
            var moved = await _service.TryTransitionAsync(condition.Id, fromStatus, toStatus);

            moved.ShouldBeTrue();
            _repository.Stored(condition.Id).Status.ShouldBe(toStatus);
            _repository.EventsFor(condition.Id).Single().EventType.ShouldBe(toStatus.ToString());
        }
        else
        {
            await Should.ThrowAsync<InvalidRequestException>(
                () => _service.TryTransitionAsync(condition.Id, fromStatus, toStatus));

            _repository.Stored(condition.Id).Status.ShouldBe(fromStatus);
            _repository.EventsFor(condition.Id).ShouldBeEmpty();
        }
    }

    [Test]
    public async Task TryTransition_FillsTheTimestampThatFollowsFromTheTargetStatus()
    {
        var resolving = _repository.Seed(Kind, "fp-resolve", AgentConditionStatus.Detected, StartUtc);
        var escalating = _repository.Seed(Kind, "fp-escalate", AgentConditionStatus.Reported, StartUtc);
        var executing = _repository.Seed(Kind, "fp-execute", AgentConditionStatus.Prepared, StartUtc);
        var rejecting = _repository.Seed(Kind, "fp-reject", AgentConditionStatus.Prepared, StartUtc);

        _timeProvider.Now = StartUtc.AddMinutes(5);

        await _service.TryTransitionAsync(resolving.Id, AgentConditionStatus.Detected, AgentConditionStatus.Resolved);
        await _service.TryTransitionAsync(escalating.Id, AgentConditionStatus.Reported, AgentConditionStatus.Escalated);
        await _service.TryTransitionAsync(executing.Id, AgentConditionStatus.Prepared, AgentConditionStatus.Executed);
        await _service.TryTransitionAsync(rejecting.Id, AgentConditionStatus.Prepared, AgentConditionStatus.Rejected);

        _repository.Stored(resolving.Id).ResolvedAtUtc.ShouldBe(StartUtc.AddMinutes(5));
        _repository.Stored(escalating.Id).EscalatedAtUtc.ShouldBe(StartUtc.AddMinutes(5));
        _repository.Stored(executing.Id).HandledAtUtc.ShouldBe(StartUtc.AddMinutes(5));
        _repository.Stored(rejecting.Id).HandledAtUtc.ShouldBe(StartUtc.AddMinutes(5));
    }

    [Test]
    public async Task TryTransition_CarriesCallerSuppliedFieldsAndAuditDetail()
    {
        var condition = _repository.Seed(Kind, "fp-fields", AgentConditionStatus.Prepared, StartUtc);
        var scenarioId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var moved = await _service.TryTransitionAsync(
            condition.Id,
            AgentConditionStatus.Prepared,
            AgentConditionStatus.Rejected,
            userId,
            "not this time",
            new AgentConditionTransitionFields(
                ScenarioId: scenarioId,
                RejectReason: AgentConditionRejectReason.WrongThisTime,
                RejectedByUserId: userId));

        moved.ShouldBeTrue();

        var stored = _repository.Stored(condition.Id);
        stored.ScenarioId.ShouldBe(scenarioId);
        stored.RejectReason.ShouldBe(AgentConditionRejectReason.WrongThisTime);
        stored.RejectedByUserId.ShouldBe(userId);

        var auditEvent = _repository.EventsFor(condition.Id).Single();
        auditEvent.UserId.ShouldBe(userId);
        auditEvent.Detail.ShouldBe("not this time");
    }

    [Test]
    public async Task TryTransition_SecondClaimOfTheSameMoveLoses_AndWritesNoSecondEvent()
    {
        var condition = _repository.Seed(Kind, "fp-claim", AgentConditionStatus.Detected, StartUtc);

        var firstClaim = await _service.TryTransitionAsync(
            condition.Id, AgentConditionStatus.Detected, AgentConditionStatus.Reported);
        var secondClaim = await _service.TryTransitionAsync(
            condition.Id, AgentConditionStatus.Detected, AgentConditionStatus.Reported);

        firstClaim.ShouldBeTrue();
        secondClaim.ShouldBeFalse();
        _repository.Stored(condition.Id).Status.ShouldBe(AgentConditionStatus.Reported);
        _repository.EventsFor(condition.Id).Count.ShouldBe(1);
    }

    [Test]
    public async Task UpsertDetected_FirstSightingOpensARowWithItsDetectionEvent()
    {
        var (condition, isNew) = await _service.UpsertDetectedAsync(
            Kind, "fp-new", Guid.NewGuid(), Guid.NewGuid(), Severity, Payload);

        isNew.ShouldBeTrue();
        condition.Status.ShouldBe(AgentConditionStatus.Detected);
        condition.DetectedAtUtc.ShouldBe(StartUtc);
        condition.LastSeenAtUtc.ShouldBe(StartUtc);
        condition.HandlingKind.ShouldBe(AgentConditionHandlingKind.None);
        _repository.EventsFor(condition.Id).Single().EventType.ShouldBe(AgentConditionStatus.Detected.ToString());
    }

    [Test]
    public async Task UpsertDetected_StoresTheFingerprintVerbatim_ItIsTheDetectorsToBuild()
    {
        const string fingerprint = "empty_container:11111111-1111-1111-1111-111111111111:2026-09-01";

        var (condition, _) = await _service.UpsertDetectedAsync(
            Kind, fingerprint, null, null, Severity, Payload);

        condition.Fingerprint.ShouldBe(fingerprint);
        _repository.Stored(condition.Id).Fingerprint.ShouldBe(fingerprint);
    }

    [Test]
    public async Task UpsertDetected_SecondSightingWithAnUnchangedPayload_MovesLastSeenOnly()
    {
        var (first, _) = await _service.UpsertDetectedAsync(Kind, "fp-again", null, null, Severity, Payload);

        _timeProvider.Now = StartUtc.AddMinutes(5);
        var (second, isNew) = await _service.UpsertDetectedAsync(Kind, "fp-again", null, null, Severity, Payload);

        isNew.ShouldBeFalse();
        second.Id.ShouldBe(first.Id);
        second.LastSeenAtUtc.ShouldBe(StartUtc.AddMinutes(5));
        second.DetectedAtUtc.ShouldBe(StartUtc);
        _repository.Stored(first.Id).PayloadJson.ShouldBe(Payload);
        _repository.Conditions.Count.ShouldBe(1);
        _repository.EventsFor(first.Id).Count.ShouldBe(1);
    }

    /// <summary>
    /// The Etappe 5 unlock: a row opened before a detector started capturing a field has to be able to
    /// pick that field up while it stays open, or it can never be remediated and therefore never leaves
    /// the queue. Everything that makes the row the SAME memory is asserted to be unchanged - the
    /// fingerprint above all, because a changed one would open a second row and split the history.
    /// </summary>
    [Test]
    public async Task UpsertDetected_SecondSightingWithAChangedPayload_RefreshesItAndLeavesTheRowsIdentityAndLifecycleAlone()
    {
        var seeded = _repository.Seed(Kind, "fp-refresh", AgentConditionStatus.Reported, StartUtc);
        seeded.PayloadJson = StalePayload;
        seeded.AttemptCount = 2;
        seeded.HandledAtUtc = StartUtc.AddMinutes(1);
        seeded.RejectReason = AgentConditionRejectReason.WrongThisTime;

        _timeProvider.Now = StartUtc.AddMinutes(5);
        var (condition, isNew) = await _service.UpsertDetectedAsync(
            Kind, "fp-refresh", null, null, Severity, RefreshedPayload);

        isNew.ShouldBeFalse();
        condition.Id.ShouldBe(seeded.Id);

        var stored = _repository.Stored(seeded.Id);
        stored.PayloadJson.ShouldBe(RefreshedPayload);
        condition.PayloadJson.ShouldBe(RefreshedPayload);

        stored.Fingerprint.ShouldBe("fp-refresh");
        stored.Status.ShouldBe(AgentConditionStatus.Reported);
        stored.AttemptCount.ShouldBe(2);
        stored.HandledAtUtc.ShouldBe(StartUtc.AddMinutes(1));
        stored.RejectReason.ShouldBe(AgentConditionRejectReason.WrongThisTime);
        stored.DetectedAtUtc.ShouldBe(StartUtc);
        _repository.EventsFor(seeded.Id).ShouldBeEmpty();
        _repository.Conditions.Count.ShouldBe(1);
    }

    /// <summary>
    /// Two ticks can share a timestamp - a test driving a fake clock always does, and so do two API
    /// instances scanning within the same clock resolution. A refresh gated on the timestamp alone would
    /// be dropped in exactly those cases, which is the failure this pins.
    /// </summary>
    [Test]
    public async Task UpsertDetected_ChangedPayloadWithoutTheClockAdvancing_StillRefreshes()
    {
        var seeded = _repository.Seed(Kind, "fp-sameclock", AgentConditionStatus.Reported, StartUtc);
        seeded.PayloadJson = StalePayload;

        var (_, isNew) = await _service.UpsertDetectedAsync(
            Kind, "fp-sameclock", null, null, Severity, RefreshedPayload);

        isNew.ShouldBeFalse();
        _repository.Stored(seeded.Id).PayloadJson.ShouldBe(RefreshedPayload);
        _repository.Stored(seeded.Id).LastSeenAtUtc.ShouldBe(StartUtc);
    }

    [Test]
    public async Task UpsertDetected_DetectorReportingNothingStructured_NeverClearsAPopulatedPayload()
    {
        var seeded = _repository.Seed(Kind, "fp-empty", AgentConditionStatus.Reported, StartUtc);
        seeded.PayloadJson = RefreshedPayload;

        _timeProvider.Now = StartUtc.AddMinutes(5);
        await _service.UpsertDetectedAsync(Kind, "fp-empty", null, null, Severity, "{}");

        _repository.Stored(seeded.Id).PayloadJson.ShouldBe(RefreshedPayload);
    }

    /// <summary>
    /// A terminal row is history. The fingerprint lookup never returns one, so a re-detection opens a
    /// fresh row and the old payload survives untouched - this pins that the refresh cannot reach back
    /// into a closed row through the re-arm path.
    /// </summary>
    [Test]
    public async Task UpsertDetected_ChangedPayloadAfterTheRowWentTerminal_LeavesTheHistoryRowUntouched()
    {
        var (first, _) = await _service.UpsertDetectedAsync(Kind, "fp-terminal", null, null, Severity, StalePayload);
        await _service.TryTransitionAsync(first.Id, AgentConditionStatus.Detected, AgentConditionStatus.Resolved);

        _timeProvider.Now = StartUtc.AddHours(1);
        var (second, isNew) = await _service.UpsertDetectedAsync(
            Kind, "fp-terminal", null, null, Severity, RefreshedPayload);

        isNew.ShouldBeTrue();
        second.Id.ShouldNotBe(first.Id);
        _repository.Stored(first.Id).PayloadJson.ShouldBe(StalePayload);
        _repository.Stored(first.Id).Status.ShouldBe(AgentConditionStatus.Resolved);
        _repository.Stored(second.Id).PayloadJson.ShouldBe(RefreshedPayload);
    }

    [Test]
    public async Task UpsertDetected_LosingTheInsertRace_ReturnsTheOtherInstancesRowAsKnown()
    {
        var winner = new AgentCondition
        {
            Id = Guid.NewGuid(),
            TriggerKind = Kind,
            Fingerprint = "fp-race",
            Severity = Severity,
            Status = AgentConditionStatus.Detected,
            DetectedAtUtc = StartUtc,
            LastSeenAtUtc = StartUtc,
            PayloadJson = Payload
        };
        _repository.LoseNextInsertTo(winner);

        _timeProvider.Now = StartUtc.AddMinutes(1);
        var (condition, isNew) = await _service.UpsertDetectedAsync(Kind, "fp-race", null, null, Severity, Payload);

        isNew.ShouldBeFalse();
        condition.Id.ShouldBe(winner.Id);
        condition.LastSeenAtUtc.ShouldBe(StartUtc.AddMinutes(1));
        _repository.Conditions.Count.ShouldBe(1);
    }

    [Test]
    public async Task UpsertDetected_AfterResolved_ReArmsIntoANewRowAndKeepsTheResolvedOneAsHistory()
    {
        var (first, _) = await _service.UpsertDetectedAsync(Kind, "fp-rearm", null, null, Severity, Payload);
        await _service.TryTransitionAsync(first.Id, AgentConditionStatus.Detected, AgentConditionStatus.Resolved);

        _timeProvider.Now = StartUtc.AddHours(1);
        var (second, isNew) = await _service.UpsertDetectedAsync(Kind, "fp-rearm", null, null, Severity, Payload);

        isNew.ShouldBeTrue();
        second.Id.ShouldNotBe(first.Id);
        second.Status.ShouldBe(AgentConditionStatus.Detected);
        second.DetectedAtUtc.ShouldBe(StartUtc.AddHours(1));

        _repository.Stored(first.Id).Status.ShouldBe(AgentConditionStatus.Resolved);
        _repository.Stored(first.Id).ResolvedAtUtc.ShouldBe(StartUtc);
        _repository.Conditions.Count.ShouldBe(2);
    }

    [Test]
    public async Task MarkResolved_ClosesOnlyTheRowsMissingFromTheCurrentSet()
    {
        var stillThere = _repository.Seed(Kind, "fp-a", AgentConditionStatus.Detected, StartUtc);
        var goneReported = _repository.Seed(Kind, "fp-b", AgentConditionStatus.Reported, StartUtc);
        var gonePrepared = _repository.Seed(Kind, "fp-c", AgentConditionStatus.Prepared, StartUtc);

        _timeProvider.Now = StartUtc.AddMinutes(10);
        var resolvedCount = await _service.MarkResolvedAsync(Kind, new HashSet<string> { "fp-a" });

        resolvedCount.ShouldBe(2);
        _repository.Stored(stillThere.Id).Status.ShouldBe(AgentConditionStatus.Detected);
        _repository.Stored(stillThere.Id).ResolvedAtUtc.ShouldBeNull();
        _repository.Stored(goneReported.Id).Status.ShouldBe(AgentConditionStatus.Resolved);
        _repository.Stored(gonePrepared.Id).Status.ShouldBe(AgentConditionStatus.Resolved);
        _repository.Stored(gonePrepared.Id).ResolvedAtUtc.ShouldBe(StartUtc.AddMinutes(10));
        _repository.EventsFor(goneReported.Id).Single().EventType.ShouldBe(AgentConditionStatus.Resolved.ToString());
    }

    [Test]
    public async Task MarkResolved_LeavesOtherKindsAndAlreadyTerminalRowsUntouched()
    {
        var otherKind = _repository.Seed(OtherKind, "fp-other", AgentConditionStatus.Detected, StartUtc);
        var alreadyExecuted = _repository.Seed(Kind, "fp-done", AgentConditionStatus.Executed, StartUtc);

        var resolvedCount = await _service.MarkResolvedAsync(Kind, new HashSet<string>());

        resolvedCount.ShouldBe(0);
        _repository.Stored(otherKind.Id).Status.ShouldBe(AgentConditionStatus.Detected);
        _repository.Stored(alreadyExecuted.Id).Status.ShouldBe(AgentConditionStatus.Executed);
        _repository.EventsFor(alreadyExecuted.Id).ShouldBeEmpty();
    }

    [Test]
    public async Task MarkResolved_EmptySet_ClosesEveryOpenRowOfTheKind()
    {
        _repository.Seed(Kind, "fp-1", AgentConditionStatus.Detected, StartUtc);
        _repository.Seed(Kind, "fp-2", AgentConditionStatus.Reported, StartUtc);

        var resolvedCount = await _service.MarkResolvedAsync(Kind, new HashSet<string>());

        resolvedCount.ShouldBe(2);
        _repository.Conditions.ShouldAllBe(c => c.Status == AgentConditionStatus.Resolved);
    }

    [TestCase(AgentConditionStatus.Reported)]
    [TestCase(AgentConditionStatus.Prepared)]
    public async Task TryReject_FromAStatusThatAllowsIt_TransitionsFromWhereTheRowActuallyIs(AgentConditionStatus from)
    {
        var condition = _repository.Seed(Kind, "fp-reject", from, StartUtc);
        var rejectedBy = Guid.NewGuid();
        _timeProvider.Now = StartUtc.AddHours(3);

        var rejected = await _service.TryRejectAsync(
            condition.Id, AgentConditionRejectReason.WrongThisTime, rejectedBy);

        rejected.ShouldBeTrue();
        var stored = _repository.Stored(condition.Id);
        stored.Status.ShouldBe(AgentConditionStatus.Rejected);
        stored.RejectReason.ShouldBe(AgentConditionRejectReason.WrongThisTime);
        stored.RejectedByUserId.ShouldBe(rejectedBy);
        stored.HandledAtUtc.ShouldBe(StartUtc.AddHours(3));
        var auditEvent = _repository.EventsFor(condition.Id).Single();
        auditEvent.EventType.ShouldBe(AgentConditionStatus.Rejected.ToString());
        auditEvent.UserId.ShouldBe(rejectedBy);
    }

    /// <summary>
    /// Detected is the case a reader is most likely to assume works, and it does not: the state machine
    /// grants Rejected only from Reported and Prepared, because a human can only reject what they were
    /// told about. A row stays in Detected when the tick's notification step threw before it could mark
    /// the row Reported, so this is reachable in production, not a theoretical status.
    /// </summary>
    [TestCase(AgentConditionStatus.Detected)]
    [TestCase(AgentConditionStatus.Executed)]
    [TestCase(AgentConditionStatus.Resolved)]
    [TestCase(AgentConditionStatus.Escalated)]
    public async Task TryReject_FromAStatusThatForbidsIt_ReportsFalseWithoutThrowingOrWriting(AgentConditionStatus from)
    {
        var condition = _repository.Seed(Kind, "fp-reject", from, StartUtc);

        var rejected = await _service.TryRejectAsync(
            condition.Id, AgentConditionRejectReason.GenerallyUnwanted, Guid.NewGuid());

        rejected.ShouldBeFalse();
        var stored = _repository.Stored(condition.Id);
        stored.Status.ShouldBe(from);
        stored.RejectReason.ShouldBeNull();
        stored.RejectedByUserId.ShouldBeNull();
        _repository.EventsFor(condition.Id).ShouldBeEmpty();
    }

    [Test]
    public async Task TryReject_UnknownCondition_ReportsFalseWithoutThrowing()
    {
        var rejected = await _service.TryRejectAsync(
            Guid.NewGuid(), AgentConditionRejectReason.AlreadyHandled, Guid.NewGuid());

        rejected.ShouldBeFalse();
    }

    [Test]
    public async Task TryReject_WithoutARejectingUser_StillRecordsTheReason()
    {
        var condition = _repository.Seed(Kind, "fp-reject", AgentConditionStatus.Reported, StartUtc);

        var rejected = await _service.TryRejectAsync(
            condition.Id, AgentConditionRejectReason.NoReason, rejectedByUserId: null);

        rejected.ShouldBeTrue();
        var stored = _repository.Stored(condition.Id);
        stored.Status.ShouldBe(AgentConditionStatus.Rejected);
        stored.RejectReason.ShouldBe(AgentConditionRejectReason.NoReason);
        stored.RejectedByUserId.ShouldBeNull();
    }

    [TestCase(AgentConditionStatus.Detected)]
    [TestCase(AgentConditionStatus.Reported)]
    [TestCase(AgentConditionStatus.Prepared)]
    [TestCase(AgentConditionStatus.Escalated)]
    public async Task TryDelegate_OnAPlannerRelevantRow_WritesTheGrantAndAnAuditEventWithoutChangingStatus(
        AgentConditionStatus status)
    {
        var condition = _repository.Seed(Kind, "fp-delegate", status, StartUtc);
        var delegatingUserId = Guid.NewGuid();
        _timeProvider.Now = StartUtc.AddHours(2);

        var delegated = await _service.TryDelegateAsync(condition.Id, ProactiveMaxAction.Prepare, delegatingUserId);

        delegated.ShouldBeTrue();
        var stored = _repository.Stored(condition.Id);
        stored.Status.ShouldBe(status);
        stored.DelegatedMaxAction.ShouldBe(ProactiveMaxAction.Prepare);
        stored.DelegatedByUserId.ShouldBe(delegatingUserId);
        var auditEvent = _repository.EventsFor(condition.Id).Single();
        auditEvent.EventType.ShouldBe("Delegated");
        auditEvent.UserId.ShouldBe(delegatingUserId);
        auditEvent.AtUtc.ShouldBe(StartUtc.AddHours(2));
    }

    [TestCase(AgentConditionStatus.Executed)]
    [TestCase(AgentConditionStatus.Rejected)]
    [TestCase(AgentConditionStatus.Resolved)]
    public async Task TryDelegate_OnATerminalNonPlannerRelevantRow_ReportsFalseWithoutWriting(AgentConditionStatus status)
    {
        var condition = _repository.Seed(Kind, "fp-delegate-terminal", status, StartUtc);

        var delegated = await _service.TryDelegateAsync(condition.Id, ProactiveMaxAction.Prepare, Guid.NewGuid());

        delegated.ShouldBeFalse();
        var stored = _repository.Stored(condition.Id);
        stored.DelegatedMaxAction.ShouldBeNull();
        stored.DelegatedByUserId.ShouldBeNull();
        _repository.EventsFor(condition.Id).ShouldBeEmpty();
    }

    [Test]
    public async Task TryDelegate_UnknownCondition_ReportsFalseWithoutThrowing()
    {
        var delegated = await _service.TryDelegateAsync(Guid.NewGuid(), ProactiveMaxAction.Execute, Guid.NewGuid());

        delegated.ShouldBeFalse();
    }
}
