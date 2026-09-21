// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The deadline guard inside EscalationChainService.AdvanceAsync. Before it existed, AdvanceAsync
/// computed a wave for any Running chain it was handed, and EscalationWaveCalculator answers a
/// non-positive remaining budget with a PARALLEL wave (B4) - so a chain that had drifted far past its
/// deadline, because a sweep stalled or an instance restarted, woke every roster member still Pending
/// on it at once. The guard narrows B4 to a grace window around the deadline: inside it the last-chance
/// parallel wave still runs, beyond it the chain is exhausted and nobody is notified. Same in-memory
/// repository fake as the reference case, same caveat about ExecuteUpdate semantics.
/// Since the exhaust and the cancellation of the chain's remaining stages happen in one repository
/// transaction, the beyond-grace case additionally leaves no stage behind that a late reply could still
/// win - which is why the assertions below expect Cancelled where they once expected Pending.
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
public class EscalationChainServiceDeadlineGuardTests
{
    private const string FirstUserId = "planner-first";
    private const string SecondUserId = "planner-second";
    private const string ThirdUserId = "planner-third";
    private const int WindowMinutes = 30;
    private const int StaleMinutesPastDeadline = 120;
    private const int WithinGraceSeconds = 30;

    private static readonly Guid ConditionId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly DateTime StartedAtUtc = new(2026, 9, 21, 22, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyList<EscalationRosterCandidate> Roster =
    [
        new(FirstUserId, "First Planner"),
        new(SecondUserId, "Second Planner"),
        new(ThirdUserId, "Third Planner")
    ];

    private FakeEscalationChainRepository _repository = null!;
    private IEscalationNotifier _notifier = null!;
    private SettableTimeProvider _timeProvider = null!;
    private EscalationChainService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new FakeEscalationChainRepository();
        _notifier = Substitute.For<IEscalationNotifier>();
        _timeProvider = new SettableTimeProvider(StartedAtUtc);

        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsEntity?)null);

        _notifier.NotifyStageAsync(Arg.Any<EscalationChain>(), Arg.Any<EscalationStage>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new EscalationNotificationResult(OfflineMessengerDeliveryOutcome.Sent, Guid.NewGuid(), "Inbox"));

        _sut = new EscalationChainService(
            _repository,
            Substitute.For<IEscalationRosterService>(),
            _notifier,
            settingsReader,
            Substitute.For<IAgentConditionLedgerService>(),
            _timeProvider,
            Substitute.For<ILogger<EscalationChainService>>());
    }

    private async Task<Guid> StartChainAsync()
    {
        var deadlineUtc = Klacks.Api.Domain.Services.Assistant.ProactiveApprovalDeadline.Compute(
            StartedAtUtc, Roster.Count, WindowMinutes);

        return (await _sut.StartConditionApprovalChainAsync(
            new StartConditionApprovalChainRequest(ConditionId, GroupId, Roster, deadlineUtc)))!.Value;
    }

    [Test]
    public async Task AdvanceAsync_ChainFarPastItsDeadline_IsExhaustedAndEveryRemainingStageIsCancelled()
    {
        var chainId = await StartChainAsync();
        var firstStage = _repository.GetStage(chainId, FirstUserId);
        _notifier.ClearReceivedCalls();

        _timeProvider.Now = _repository.GetChain(chainId).DeadlineUtc.AddMinutes(StaleMinutesPastDeadline);
        Assert.That(await _repository.TryExpireStageAsync(firstStage.Id), Is.True);
        await _sut.AdvanceAsync(chainId);

        var chain = _repository.GetChain(chainId);
        Assert.That(chain.Status, Is.EqualTo(EscalationChainStatus.Exhausted));
        Assert.That(chain.AcknowledgedByUserId, Is.Null);
        Assert.That(
            _repository.GetStage(chainId, SecondUserId).Status,
            Is.EqualTo(EscalationStageStatus.Cancelled),
            "An exhaust cancels every stage still Pending or Notified - a stage left Pending under a dead chain "
            + "is exactly what let a late acknowledgement win its stage guard and then vanish without a stamp.");
        Assert.That(_repository.GetStage(chainId, ThirdUserId).Status, Is.EqualTo(EscalationStageStatus.Cancelled));

        await _notifier.DidNotReceiveWithAnyArgs().NotifyStageAsync(default!, default!, default, default);
        await _notifier.DidNotReceiveWithAnyArgs().NotifyExhaustedAsync(default!, default!, default);
    }

    [Test]
    public async Task AdvanceAsync_FreshChain_StillHandsOverToTheNextRankAlone()
    {
        var chainId = await StartChainAsync();
        var firstStage = _repository.GetStage(chainId, FirstUserId);
        _notifier.ClearReceivedCalls();

        _timeProvider.Now = firstStage.DueAtUtc!.Value;
        Assert.That(await _repository.TryExpireStageAsync(firstStage.Id), Is.True);
        await _sut.AdvanceAsync(chainId);

        Assert.That(_repository.GetChain(chainId).Status, Is.EqualTo(EscalationChainStatus.Running));
        Assert.That(_repository.GetStage(chainId, SecondUserId).Status, Is.EqualTo(EscalationStageStatus.Notified));
        Assert.That(_repository.GetStage(chainId, ThirdUserId).Status, Is.EqualTo(EscalationStageStatus.Pending));

        await _notifier.Received(1).NotifyStageAsync(
            Arg.Any<EscalationChain>(), Arg.Is<EscalationStage>(s => s.UserId == SecondUserId), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AdvanceAsync_ChainJustPastItsDeadlineWithinTheGrace_StillRunsTheLastChanceParallelWave()
    {
        var chainId = await StartChainAsync();
        var firstStage = _repository.GetStage(chainId, FirstUserId);
        _notifier.ClearReceivedCalls();

        _timeProvider.Now = _repository.GetChain(chainId).DeadlineUtc.AddSeconds(WithinGraceSeconds);
        Assert.That(await _repository.TryExpireStageAsync(firstStage.Id), Is.True);
        await _sut.AdvanceAsync(chainId);

        Assert.That(_repository.GetChain(chainId).Status, Is.EqualTo(EscalationChainStatus.Running));
        Assert.That(_repository.GetStage(chainId, SecondUserId).Status, Is.EqualTo(EscalationStageStatus.Notified));
        Assert.That(
            _repository.GetStage(chainId, ThirdUserId).Status,
            Is.EqualTo(EscalationStageStatus.Notified),
            "Inside the grace window B4 is unchanged: the remaining budget is non-positive, so every pending rank goes at once.");
    }
}
