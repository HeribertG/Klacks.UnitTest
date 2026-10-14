// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins that a person cancelling a chain closes the open inbox question of every woken (Notified) stage, like an
/// exhaust does, and that a cancel which loses the race (chain already ended) closes nothing.
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
public class EscalationChainServiceCancelClosesInboxTests
{
    private const string FirstUserId = "planner-first";
    private const string SecondUserId = "planner-second";
    private const int WindowMinutes = 30;
    private const string CancelReason = "Shift already covered.";

    private static readonly Guid ConditionId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly DateTime StartedAtUtc = new(2026, 9, 21, 22, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyList<EscalationRosterCandidate> Roster =
    [
        new(FirstUserId, "First Planner"),
        new(SecondUserId, "Second Planner")
    ];

    private FakeEscalationChainRepository _repository = null!;
    private IEscalationNotifier _notifier = null!;
    private EscalationChainService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new FakeEscalationChainRepository();
        _notifier = Substitute.For<IEscalationNotifier>();
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
            new SettableTimeProvider(StartedAtUtc),
            Substitute.For<ILogger<EscalationChainService>>());
    }

    [Test]
    public async Task CancelAsync_ClosesTheOpenInboxQuestionOfTheNotifiedStage()
    {
        var chainId = await StartChainAsync();
        _notifier.ClearReceivedCalls();

        var cancelled = await _sut.CancelAsync(chainId, FirstUserId, "First Planner", CancelReason);

        Assert.That(cancelled, Is.True);
        await _notifier.Received(1).CloseOpenQuestionsAsync(
            Arg.Is<IReadOnlyList<EscalationStage>>(stages => stages.Count == 1 && stages[0].UserId == FirstUserId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CancelAsync_ChainAlreadyEnded_ClosesNothing()
    {
        var chainId = await StartChainAsync();
        await _sut.ForceExhaustAsync(chainId, "deadline");
        _notifier.ClearReceivedCalls();

        var cancelled = await _sut.CancelAsync(chainId, FirstUserId, "First Planner", CancelReason);

        Assert.That(cancelled, Is.False);
        await _notifier.DidNotReceiveWithAnyArgs().CloseOpenQuestionsAsync(default!, default);
    }

    private async Task<Guid> StartChainAsync()
    {
        var deadlineUtc = ProactiveApprovalDeadline.Compute(StartedAtUtc, Roster.Count, WindowMinutes);

        return (await _sut.StartConditionApprovalChainAsync(
            new StartConditionApprovalChainRequest(ConditionId, GroupId, Roster, deadlineUtc)))!.Value;
    }
}
