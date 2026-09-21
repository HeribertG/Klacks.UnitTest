// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The approval path through EscalationChainService: the caller hands over an already-resolved roster
/// and an absolute deadline, the service freezes them into a ProactiveApproval chain keyed by
/// ConditionId and drives the same wave machinery the absence path uses. Proves what the absence tests
/// cannot: no roster lookup, no prep-buffer arithmetic, no urgency gate, one Running chain per
/// condition, and that a chain nobody answers ends Exhausted (fail closed) rather than acknowledged by
/// default. Same in-memory fake as the reference case, same caveat about ExecuteUpdate semantics.
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
public class EscalationChainServiceConditionApprovalTests
{
    private const int StageCount = 3;
    private const int WindowMinutes = 30;

    private static readonly Guid ConditionId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly DateTime StartedAtUtc = new(2026, 9, 21, 22, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyList<EscalationRosterCandidate> Roster =
    [
        new("planner-last", "Last Planner"),
        new("planner-group", "Group Planner"),
        new("admin-1", "Admin One")
    ];

    private FakeEscalationChainRepository _repository = null!;
    private IEscalationRosterService _rosterService = null!;
    private IEscalationNotifier _notifier = null!;
    private ISettingsReader _settingsReader = null!;
    private IAgentConditionLedgerService _ledger = null!;
    private SettableTimeProvider _timeProvider = null!;
    private EscalationChainService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new FakeEscalationChainRepository();
        _rosterService = Substitute.For<IEscalationRosterService>();
        _notifier = Substitute.For<IEscalationNotifier>();
        _settingsReader = Substitute.For<ISettingsReader>();
        _ledger = Substitute.For<IAgentConditionLedgerService>();
        _timeProvider = new SettableTimeProvider(StartedAtUtc);

        _settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsEntity?)null);
        _ledger.TryApproveAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _notifier.NotifyStageAsync(Arg.Any<EscalationChain>(), Arg.Any<EscalationStage>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new EscalationNotificationResult(OfflineMessengerDeliveryOutcome.Sent, Guid.NewGuid(), "Inbox"));

        _sut = new EscalationChainService(
            _repository, _rosterService, _notifier, _settingsReader, _ledger, _timeProvider, Substitute.For<ILogger<EscalationChainService>>());
    }

    private static DateTime DefaultDeadline() =>
        Klacks.Api.Domain.Services.Assistant.ProactiveApprovalDeadline.Compute(StartedAtUtc, StageCount, WindowMinutes);

    private StartConditionApprovalChainRequest Request(
        IReadOnlyList<EscalationRosterCandidate>? roster = null, DateTime? deadlineUtc = null, Guid? groupId = null) =>
        new(ConditionId, groupId ?? GroupId, roster ?? Roster, deadlineUtc ?? DefaultDeadline());

    [Test]
    public async Task Start_FreezesCallerRosterInOrder_AsProactiveApprovalChain_AndNotifiesStageOneOnly()
    {
        var chainId = await _sut.StartConditionApprovalChainAsync(Request());

        Assert.That(chainId, Is.Not.Null);
        var chain = _repository.GetChain(chainId!.Value);

        Assert.That(chain.Purpose, Is.EqualTo(EscalationChainPurpose.ProactiveApproval));
        Assert.That(chain.ConditionId, Is.EqualTo(ConditionId));
        Assert.That(chain.GroupId, Is.EqualTo(GroupId));
        Assert.That(chain.WorkId, Is.Null);
        Assert.That(chain.ShiftStartUtc, Is.Null);
        Assert.That(chain.AbsentClientId, Is.Null);
        Assert.That(chain.DeadlineUtc, Is.EqualTo(DefaultDeadline()), "The caller's deadline is stored verbatim - no prep buffer.");

        var ranked = chain.Stages.OrderBy(s => s.Rank).Select(s => s.UserId).ToList();
        Assert.That(ranked, Is.EqualTo(new[] { "planner-last", "planner-group", "admin-1" }));

        Assert.That(_repository.GetStage(chainId.Value, "planner-last").Status, Is.EqualTo(EscalationStageStatus.Notified));
        Assert.That(_repository.GetStage(chainId.Value, "planner-group").Status, Is.EqualTo(EscalationStageStatus.Pending));
        Assert.That(_repository.GetStage(chainId.Value, "admin-1").Status, Is.EqualTo(EscalationStageStatus.Pending));

        await _rosterService.DidNotReceiveWithAnyArgs().GetOrderedRosterAsync(default, default);
    }

    [Test]
    public async Task Start_DeadlineFarBeyondReachableWindow_IsNotGated_CallerOwnsTheDeadline()
    {
        var farDeadline = StartedAtUtc.AddDays(14);

        var chainId = await _sut.StartConditionApprovalChainAsync(Request(deadlineUtc: farDeadline));

        Assert.That(chainId, Is.Not.Null);
        Assert.That(_repository.GetChain(chainId!.Value).DeadlineUtc, Is.EqualTo(farDeadline));
        await _notifier.Received(1).NotifyStageAsync(
            Arg.Any<EscalationChain>(), Arg.Any<EscalationStage>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Start_SecondRequestForSameCondition_ReturnsNull_LeavesFirstChainUntouched()
    {
        var first = await _sut.StartConditionApprovalChainAsync(Request());
        var second = await _sut.StartConditionApprovalChainAsync(Request());

        Assert.That(first, Is.Not.Null);
        Assert.That(second, Is.Null);
        Assert.That(_repository.GetChain(first!.Value).Status, Is.EqualTo(EscalationChainStatus.Running));
        await _notifier.Received(1).NotifyStageAsync(
            Arg.Any<EscalationChain>(), Arg.Any<EscalationStage>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Start_ApprovalChainAndAbsenceChain_DoNotCollide_OnEachOthersKey()
    {
        var approvalChainId = await _sut.StartConditionApprovalChainAsync(Request());

        _rosterService.GetOrderedRosterAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Roster);
        var absenceChainId = await _sut.StartChainAsync(new StartEscalationChainRequest(
            Guid.NewGuid(), GroupId, Guid.NewGuid(), "Absent Employee", StartedAtUtc.AddHours(3), AbsenceBreakId: null));

        Assert.That(approvalChainId, Is.Not.Null);
        Assert.That(absenceChainId, Is.Not.Null, "Two Running chains with null WorkId / null ConditionId respectively must both be allowed.");
        Assert.That(_repository.GetChain(absenceChainId!.Value).Purpose, Is.EqualTo(EscalationChainPurpose.AbsenceCoverage));
        Assert.That(_repository.GetChain(absenceChainId.Value).ConditionId, Is.Null);
    }

    [Test]
    public async Task Start_EmptyRoster_CreatesExhaustedChain_NotifiesNobody()
    {
        var chainId = await _sut.StartConditionApprovalChainAsync(Request(roster: Array.Empty<EscalationRosterCandidate>()));

        Assert.That(chainId, Is.Not.Null);
        Assert.That(_repository.GetChain(chainId!.Value).Status, Is.EqualTo(EscalationChainStatus.Exhausted));
        await _notifier.DidNotReceiveWithAnyArgs().NotifyStageAsync(default!, default!, default, default);
    }

    [Test]
    public async Task Start_FindingWithoutGroup_StoresNullGroup()
    {
        var chainId = await _sut.StartConditionApprovalChainAsync(new StartConditionApprovalChainRequest(
            ConditionId, GroupId: null, Roster, DefaultDeadline()));

        Assert.That(_repository.GetChain(chainId!.Value).GroupId, Is.Null);
    }

    [Test]
    public async Task NobodyAcknowledges_EveryStageExpires_ChainEndsExhausted_NeverAcknowledged()
    {
        var chainId = (await _sut.StartConditionApprovalChainAsync(Request()))!.Value;

        foreach (var userId in Roster.Select(c => c.UserId))
        {
            var stage = _repository.GetStage(chainId, userId);
            Assert.That(stage.Status, Is.EqualTo(EscalationStageStatus.Notified), $"{userId} must be Notified in turn.");

            _timeProvider.Now = stage.DueAtUtc!.Value;
            Assert.That(await _repository.TryExpireStageAsync(stage.Id), Is.True);
            await _sut.AdvanceAsync(chainId);
        }

        var chain = _repository.GetChain(chainId);
        Assert.That(chain.Status, Is.EqualTo(EscalationChainStatus.Exhausted));
        Assert.That(chain.AcknowledgedByUserId, Is.Null);
        Assert.That(chain.Stages.All(s => s.Status == EscalationStageStatus.Expired), Is.True);
        await _notifier.DidNotReceiveWithAnyArgs().NotifyHandoffAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task StageTwoAcknowledges_ChainAcknowledged_StageThreeNeverNotified()
    {
        var chainId = (await _sut.StartConditionApprovalChainAsync(Request()))!.Value;

        var first = _repository.GetStage(chainId, "planner-last");
        _timeProvider.Now = first.DueAtUtc!.Value;
        Assert.That(await _repository.TryExpireStageAsync(first.Id), Is.True);
        await _sut.AdvanceAsync(chainId);

        Assert.That(_repository.GetStage(chainId, "planner-group").Status, Is.EqualTo(EscalationStageStatus.Notified));

        var acknowledged = await _sut.AcknowledgeChainAsync(chainId, "planner-group");

        Assert.That(acknowledged, Is.True);
        Assert.That(_repository.GetChain(chainId).Status, Is.EqualTo(EscalationChainStatus.Acknowledged));
        Assert.That(_repository.GetChain(chainId).AcknowledgedByUserId, Is.EqualTo("planner-group"));
        Assert.That(_repository.GetStage(chainId, "admin-1").Status, Is.EqualTo(EscalationStageStatus.Cancelled));
        await _notifier.DidNotReceive().NotifyStageAsync(
            Arg.Any<EscalationChain>(), Arg.Is<EscalationStage>(s => s.UserId == "admin-1"), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SecondAcknowledgementOnResolvedChain_ReturnsFalse()
    {
        var chainId = (await _sut.StartConditionApprovalChainAsync(Request()))!.Value;

        Assert.That(await _sut.AcknowledgeChainAsync(chainId, "planner-last"), Is.True);
        Assert.That(await _sut.AcknowledgeChainAsync(chainId, "planner-last"), Is.False);
        Assert.That(await _sut.AcknowledgeChainAsync(chainId, "planner-group"), Is.False);
    }

    [Test]
    public async Task Acknowledge_OnAnApprovalChain_StampsTheAcknowledgerAsApproverExactlyOnce()
    {
        var approver = Guid.NewGuid();
        var chainId = (await _sut.StartConditionApprovalChainAsync(Request(roster: GuidRoster(approver))))!.Value;

        Assert.That(await _sut.AcknowledgeChainAsync(chainId, approver.ToString()), Is.True);
        Assert.That(await _sut.AcknowledgeChainAsync(chainId, approver.ToString()), Is.False, "The chain CAS makes the replay a no-op.");

        await _ledger.Received(1).TryApproveAsync(ConditionId, approver, Arg.Any<CancellationToken>());
        await _ledger.DidNotReceiveWithAnyArgs().TryTransitionAsync(default, default, default, default, default, default, default);
    }

    [Test]
    public async Task Acknowledge_OnAnApprovalChain_ReplyPathStampsTheSameApproval()
    {
        var approver = Guid.NewGuid();
        await _sut.StartConditionApprovalChainAsync(Request(roster: GuidRoster(approver)));

        Assert.That(await _sut.AcknowledgeAsync(approver.ToString()), Is.True);

        await _ledger.Received(1).TryApproveAsync(ConditionId, approver, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Acknowledge_OnAnAbsenceChain_NeverTouchesTheLedger()
    {
        var responder = Guid.NewGuid();
        _rosterService.GetOrderedRosterAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(GuidRoster(responder));
        var chainId = (await _sut.StartChainAsync(new StartEscalationChainRequest(
            Guid.NewGuid(), GroupId, Guid.NewGuid(), "Absent Employee", StartedAtUtc.AddHours(3), AbsenceBreakId: null)))!.Value;

        Assert.That(await _sut.AcknowledgeChainAsync(chainId, responder.ToString()), Is.True);

        await _ledger.DidNotReceiveWithAnyArgs().TryApproveAsync(default, default, default);
    }

    [Test]
    public async Task Acknowledge_WhenTheConditionNoLongerAcceptsAnApproval_ChainIsStillAcknowledged()
    {
        var approver = Guid.NewGuid();
        _ledger.TryApproveAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        var chainId = (await _sut.StartConditionApprovalChainAsync(Request(roster: GuidRoster(approver))))!.Value;

        Assert.That(await _sut.AcknowledgeChainAsync(chainId, approver.ToString()), Is.True);
        Assert.That(_repository.GetChain(chainId).Status, Is.EqualTo(EscalationChainStatus.Acknowledged));
    }

    private static IReadOnlyList<EscalationRosterCandidate> GuidRoster(Guid first) =>
    [
        new(first.ToString(), "First Approver"),
        new(Guid.NewGuid().ToString(), "Second Approver")
    ];
}
