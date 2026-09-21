// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The failure this fixture pins: an acknowledgement that arrives after its approval chain has been
/// force-exhausted used to win the stage-level compare-and-swap, lose the chain-level one, and return
/// true - so the caller saw a success while StampApprovalIfApprovalChainAsync was never reached and
/// nothing was ever executed. The approval simply evaporated. Two routes lead into it and both are
/// covered here: the chain has already ended when the click arrives, and the chain ends between the
/// stage transition and the chain transition.
/// The second one needs a seam, because a single-threaded in-memory double cannot interleave by itself:
/// ChainResolvedMidAcknowledgeRepository overrides the one method and exhausts the chain right after the
/// stage transition won, which is exactly the ordering the production race produces.
/// Assertions deliberately avoid reading a stage's Status off the returned references where the timing is
/// the point; the fake hands out live objects.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Escalation;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant.Escalation;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Services.Assistant.Escalation;

[TestFixture]
public class EscalationChainServiceLateAcknowledgeTests
{
    private const string FirstUserId = "planner-first";
    private const string SecondUserId = "planner-second";
    private const int WindowMinutes = 30;
    private const string ExhaustReason = "deadline passed before every stage could be resolved";

    private static readonly Guid ConditionId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly DateTime StartedAtUtc = new(2026, 9, 21, 22, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyList<EscalationRosterCandidate> Roster =
    [
        new(FirstUserId, "First Planner"),
        new(SecondUserId, "Second Planner")
    ];

    private ChainResolvedMidAcknowledgeRepository _repository = null!;
    private IEscalationNotifier _notifier = null!;
    private IAgentConditionLedgerService _ledger = null!;
    private SettableTimeProvider _timeProvider = null!;
    private EscalationChainService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new ChainResolvedMidAcknowledgeRepository();
        _notifier = Substitute.For<IEscalationNotifier>();
        _ledger = Substitute.For<IAgentConditionLedgerService>();
        _timeProvider = new SettableTimeProvider(StartedAtUtc);

        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsEntity?)null);

        _notifier.NotifyStageAsync(Arg.Any<EscalationChain>(), Arg.Any<EscalationStage>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new EscalationNotificationResult(OfflineMessengerDeliveryOutcome.Sent, Guid.NewGuid(), "Inbox"));
        _ledger.TryApproveAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        _sut = new EscalationChainService(
            _repository,
            Substitute.For<IEscalationRosterService>(),
            _notifier,
            settingsReader,
            _ledger,
            _timeProvider,
            Substitute.For<ILogger<EscalationChainService>>());
    }

    [Test]
    public async Task AcknowledgeChainAsync_ChainAlreadyExhausted_ReportsChainAlreadyResolvedAndStampsNothing()
    {
        var chainId = await StartChainAsync();
        Assert.That(await _sut.ForceExhaustAsync(chainId, ExhaustReason), Is.True);
        _notifier.ClearReceivedCalls();

        var outcome = await _sut.AcknowledgeChainAsync(chainId, FirstUserId);

        Assert.That(outcome, Is.EqualTo(EscalationAcknowledgeOutcome.ChainAlreadyResolved));
        Assert.That(_repository.GetChain(chainId).Status, Is.EqualTo(EscalationChainStatus.Exhausted));
        Assert.That(_repository.GetChain(chainId).AcknowledgedByUserId, Is.Null);

        await _ledger.DidNotReceiveWithAnyArgs().TryApproveAsync(default, default, default);
        await _notifier.DidNotReceiveWithAnyArgs().NotifyHandoffAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task ForceExhaustAsync_CancelsEveryStageStillWaitingAndClosesTheNotifiedOnesInbox()
    {
        var chainId = await StartChainAsync();
        _notifier.ClearReceivedCalls();

        Assert.That(await _sut.ForceExhaustAsync(chainId, ExhaustReason), Is.True);

        Assert.That(_repository.GetStage(chainId, FirstUserId).Status, Is.EqualTo(EscalationStageStatus.Cancelled));
        Assert.That(_repository.GetStage(chainId, SecondUserId).Status, Is.EqualTo(EscalationStageStatus.Cancelled));

        await _notifier.Received(1).NotifyExhaustedAsync(
            Arg.Is<EscalationChain>(c => c.Id == chainId),
            Arg.Is<IReadOnlyList<EscalationStage>>(stages => stages.Count == 1 && stages[0].UserId == FirstUserId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ForceExhaustAsync_OnAnAlreadyExhaustedChain_LosesTheCompareAndSwapAndNotifiesNobody()
    {
        var chainId = await StartChainAsync();
        Assert.That(await _sut.ForceExhaustAsync(chainId, ExhaustReason), Is.True);
        _notifier.ClearReceivedCalls();

        Assert.That(await _sut.ForceExhaustAsync(chainId, ExhaustReason), Is.False);

        await _notifier.DidNotReceiveWithAnyArgs().NotifyExhaustedAsync(default!, default!, default);
    }

    [Test]
    public async Task AcknowledgeChainAsync_ChainExhaustedBetweenStageAndChainTransition_ReportsChainAlreadyResolved()
    {
        var chainId = await StartChainAsync();
        _repository.ExhaustAfterStageWin = chainId;
        _notifier.ClearReceivedCalls();

        var outcome = await _sut.AcknowledgeChainAsync(chainId, FirstUserId);

        Assert.That(
            outcome,
            Is.EqualTo(EscalationAcknowledgeOutcome.ChainAlreadyResolved),
            "Winning the stage guard is not an approval: without the chain transition nothing is stamped, so "
            + "reporting this as success is the bug that made a freed remediation vanish silently.");
        Assert.That(_repository.GetChain(chainId).Status, Is.EqualTo(EscalationChainStatus.Exhausted));
        Assert.That(_repository.GetChain(chainId).AcknowledgedByUserId, Is.Null);
        Assert.That(
            _repository.GetStage(chainId, FirstUserId).Status,
            Is.EqualTo(EscalationStageStatus.Acknowledged),
            "The stage keeps the reply as an honest record of what the person did.");

        await _ledger.DidNotReceiveWithAnyArgs().TryApproveAsync(default, default, default);
        await _notifier.DidNotReceiveWithAnyArgs().NotifyHandoffAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task AcknowledgeAsync_ReplyPathOnAResolvedChain_ReportsChainAlreadyResolved()
    {
        var chainId = await StartChainAsync();
        var stage = _repository.GetStage(chainId, FirstUserId);

        // A Notified stage under a chain that is no longer Running - the shape rows written before the
        // exhaust started cancelling its stages still have in production.
        Assert.That(await _repository.TrySupersedeChainAsync(chainId, ExhaustReason), Is.True);
        Assert.That(stage.Status, Is.EqualTo(EscalationStageStatus.Notified));

        var outcome = await _sut.AcknowledgeAsync(FirstUserId);

        Assert.That(outcome, Is.EqualTo(EscalationAcknowledgeOutcome.ChainAlreadyResolved));
        Assert.That(stage.Status, Is.EqualTo(EscalationStageStatus.Notified), "Nothing was transitioned by a refused reply.");
        await _ledger.DidNotReceiveWithAnyArgs().TryApproveAsync(default, default, default);
    }

    private async Task<Guid> StartChainAsync()
    {
        var deadlineUtc = ProactiveApprovalDeadline.Compute(StartedAtUtc, Roster.Count, WindowMinutes);

        return (await _sut.StartConditionApprovalChainAsync(
            new StartConditionApprovalChainRequest(ConditionId, GroupId, Roster, deadlineUtc)))!.Value;
    }

    /// <summary>
    /// Resolves the chain right after a stage acknowledgement won, reproducing the one interleaving a
    /// single-threaded double cannot reach on its own: stage transition won, chain transition then lost.
    /// Arms once per assignment of ExhaustAfterStageWin so the exhaust inside it cannot recurse.
    /// </summary>
    private sealed class ChainResolvedMidAcknowledgeRepository : FakeEscalationChainRepository
    {
        public Guid? ExhaustAfterStageWin { get; set; }

        public override async Task<bool> TryAcknowledgeStageAsync(
            Guid stageId, DateTime respondedAtUtc, CancellationToken cancellationToken = default)
        {
            var won = await base.TryAcknowledgeStageAsync(stageId, respondedAtUtc, cancellationToken);
            if (!won || ExhaustAfterStageWin is not Guid chainId)
            {
                return won;
            }

            ExhaustAfterStageWin = null;
            await TryExhaustChainAsync(chainId, ExhaustReason, cancellationToken);

            return true;
        }
    }
}
