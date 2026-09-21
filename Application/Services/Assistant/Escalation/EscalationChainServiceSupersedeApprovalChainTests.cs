// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The delegation path's exit from an approval chain: when a planner delegates a condition whose approval
/// chain is still Running, the chain is superseded and its remaining stages cancelled so nobody else is
/// woken for a question already answered. Pins that only a Running ProactiveApproval chain of THAT
/// condition is touched, that the outcome reason is stored, and that an acknowledged chain - somebody
/// answered first - is left as it is. Same in-memory fake as the other chain-service tests.
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
public class EscalationChainServiceSupersedeApprovalChainTests
{
    private const int WindowMinutes = 30;
    private const string Reason = "the finding was delegated and thereby approved directly";

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
    private IEscalationNotifier _notifier = null!;
    private IAgentConditionLedgerService _ledger = null!;
    private EscalationChainService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new FakeEscalationChainRepository();
        _notifier = Substitute.For<IEscalationNotifier>();
        _ledger = Substitute.For<IAgentConditionLedgerService>();
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsEntity?)null);
        _ledger.TryApproveAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _notifier.NotifyStageAsync(Arg.Any<EscalationChain>(), Arg.Any<EscalationStage>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new EscalationNotificationResult(OfflineMessengerDeliveryOutcome.Sent, Guid.NewGuid(), "Inbox"));

        _sut = new EscalationChainService(
            _repository,
            Substitute.For<IEscalationRosterService>(),
            _notifier,
            settingsReader,
            _ledger,
            new SettableTimeProvider(StartedAtUtc),
            Substitute.For<ILogger<EscalationChainService>>());
    }

    private async Task<Guid> GivenRunningApprovalChainAsync(Guid? conditionId = null)
    {
        var deadline = ProactiveApprovalDeadline.Compute(StartedAtUtc, Roster.Count, WindowMinutes);
        var chainId = await _sut.StartConditionApprovalChainAsync(
            new StartConditionApprovalChainRequest(conditionId ?? ConditionId, GroupId, Roster, deadline));

        return chainId!.Value;
    }

    [Test]
    public async Task RunningApprovalChain_IsSuperseded_WithReason_AndRemainingStagesCancelled()
    {
        var chainId = await GivenRunningApprovalChainAsync();

        var superseded = await _sut.SupersedeConditionApprovalChainAsync(ConditionId, Reason);

        var chain = _repository.GetChain(chainId);
        Assert.Multiple(() =>
        {
            Assert.That(superseded, Is.True);
            Assert.That(chain.Status, Is.EqualTo(EscalationChainStatus.Superseded));
            Assert.That(chain.OutcomeReason, Is.EqualTo(Reason));
            Assert.That(chain.Stages.Select(s => s.Status), Has.None.EqualTo(EscalationStageStatus.Pending));
            Assert.That(chain.Stages.Select(s => s.Status), Has.None.EqualTo(EscalationStageStatus.Notified));
        });
    }

    [Test]
    public async Task NoChainForTheCondition_ReturnsFalse()
    {
        await GivenRunningApprovalChainAsync(conditionId: Guid.NewGuid());

        var superseded = await _sut.SupersedeConditionApprovalChainAsync(ConditionId, Reason);

        Assert.That(superseded, Is.False);
    }

    [Test]
    public async Task AlreadyAcknowledgedChain_IsLeftAlone()
    {
        var chainId = await GivenRunningApprovalChainAsync();
        Assert.That(
            await _sut.AcknowledgeChainAsync(chainId, Roster[0].UserId),
            Is.EqualTo(EscalationAcknowledgeOutcome.Acknowledged));

        var superseded = await _sut.SupersedeConditionApprovalChainAsync(ConditionId, Reason);

        Assert.Multiple(() =>
        {
            Assert.That(superseded, Is.False);
            Assert.That(_repository.GetChain(chainId).Status, Is.EqualTo(EscalationChainStatus.Acknowledged));
        });
    }

    [Test]
    public async Task ASecondSupersede_LosesTheCompareAndSwap()
    {
        await GivenRunningApprovalChainAsync();
        Assert.That(await _sut.SupersedeConditionApprovalChainAsync(ConditionId, Reason), Is.True);

        Assert.That(await _sut.SupersedeConditionApprovalChainAsync(ConditionId, Reason), Is.False);
    }
}
