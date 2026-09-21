// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The sweep order inside EscalationChainBackgroundService.RunCycleAsync: force-exhausting overdue
/// chains happens BEFORE due stages are expired. Two things make that order observable now. The outcome
/// reason, as before: both steps exhaust through the same conditional update, so whichever runs first
/// writes its own reason and the other one loses. And, since an exhaust cancels the chain's remaining
/// Pending/Notified stages inside its own transaction, the terminal status of the stage that was still
/// Notified at sweep time - Cancelled when the exhaust ran first, Expired when the expiry did - plus the
/// fact that GetDueStagesAsync finds nothing left afterwards.
/// The failure the swap was made for, a stale chain's expiry reaching AdvanceAsync while still Running
/// and being answered with EscalationWaveCalculator's parallel wave for a non-positive budget, is
/// prevented a second time and independently by AdvanceAsync's own deadline guard
/// (EscalationChainServiceDeadlineGuardTests) - the two fixes are meant to be independent, not to
/// replace each other.
/// Wired with the real EscalationChainService over the in-memory repository fake, so the sweep drives
/// the same code path production does; the notifier is the only substitute.
/// </summary>

using Klacks.Api.Application.Configuration;
using Klacks.Api.Application.Services.Assistant.Escalation;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant.Escalation;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant.Escalation;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant.Escalation;

[TestFixture]
public class EscalationChainBackgroundServiceSweepOrderTests
{
    private const string FirstUserId = "planner-first";
    private const string SecondUserId = "planner-second";
    private const string ThirdUserId = "planner-third";
    private const int WindowMinutes = 30;
    private const int StaleMinutesPastDeadline = 120;

    private const string OverdueOutcomeReason = EscalationChainBackgroundService.OverdueOutcomeReason;

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
    private EscalationChainService _chainService = null!;
    private ServiceProvider _serviceProvider = null!;
    private EscalationChainBackgroundService _sut = null!;

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

        _chainService = new EscalationChainService(
            _repository,
            Substitute.For<IEscalationRosterService>(),
            _notifier,
            settingsReader,
            Substitute.For<IAgentConditionLedgerService>(),
            _timeProvider,
            Substitute.For<ILogger<EscalationChainService>>());

        var services = new ServiceCollection();
        services.AddSingleton<IEscalationChainRepository>(_repository);
        services.AddSingleton<IEscalationChainService>(_chainService);
        _serviceProvider = services.BuildServiceProvider();

        _sut = new EscalationChainBackgroundService(
            _serviceProvider,
            _timeProvider,
            Options.Create(new BackgroundServiceOptions { EscalationChain = true }),
            NullLogger<EscalationChainBackgroundService>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        _sut.Dispose();
        await _serviceProvider.DisposeAsync();
    }

    [Test]
    public async Task RunCycleAsync_StaleChainWithADueStage_ExhaustsBeforeExpiringAndCancelsEveryStage()
    {
        var chainId = await StartChainAsync();
        _notifier.ClearReceivedCalls();

        _timeProvider.Now = _repository.GetChain(chainId).DeadlineUtc.AddMinutes(StaleMinutesPastDeadline);
        await _sut.RunCycleAsync(CancellationToken.None);

        var chain = _repository.GetChain(chainId);
        Assert.That(chain.Status, Is.EqualTo(EscalationChainStatus.Exhausted));
        Assert.That(
            chain.OutcomeReason,
            Is.EqualTo(OverdueOutcomeReason),
            "The overdue force-exhaust must win the conditional update, which only holds if it runs before the "
            + "stage expiry; with the old order AdvanceAsync's deadline guard would have written its own reason.");
        Assert.That(
            _repository.GetStage(chainId, FirstUserId).Status,
            Is.EqualTo(EscalationStageStatus.Cancelled),
            "The force-exhaust cancels the still-Notified rank 1 in its own transaction; Expired here would mean "
            + "the expiry step ran first.");
        Assert.That(_repository.GetStage(chainId, SecondUserId).Status, Is.EqualTo(EscalationStageStatus.Cancelled));
        Assert.That(_repository.GetStage(chainId, ThirdUserId).Status, Is.EqualTo(EscalationStageStatus.Cancelled));

        Assert.That(
            await _repository.GetDueStagesAsync(_timeProvider.Now),
            Is.Empty,
            "Nothing is left for the expiry step: every stage of the exhausted chain is already terminal.");

        await _notifier.DidNotReceiveWithAnyArgs().NotifyStageAsync(default!, default!, default, default);
    }

    [Test]
    public async Task RunCycleAsync_StaleChain_ClosesTheOpenInboxRowOfTheStageItCancels()
    {
        var chainId = await StartChainAsync();
        _notifier.ClearReceivedCalls();

        _timeProvider.Now = _repository.GetChain(chainId).DeadlineUtc.AddMinutes(StaleMinutesPastDeadline);
        await _sut.RunCycleAsync(CancellationToken.None);

        await _notifier.Received(1).NotifyExhaustedAsync(
            Arg.Is<EscalationChain>(c => c.Id == chainId),
            Arg.Is<IReadOnlyList<EscalationStage>>(stages => stages.Count == 1 && stages[0].UserId == FirstUserId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunCycleAsync_ChainStillInsideItsDeadline_HandsTheDueStageOverToTheNextRank()
    {
        var chainId = await StartChainAsync();
        _notifier.ClearReceivedCalls();

        _timeProvider.Now = _repository.GetStage(chainId, FirstUserId).DueAtUtc!.Value;
        await _sut.RunCycleAsync(CancellationToken.None);

        Assert.That(_repository.GetChain(chainId).Status, Is.EqualTo(EscalationChainStatus.Running));
        Assert.That(_repository.GetStage(chainId, SecondUserId).Status, Is.EqualTo(EscalationStageStatus.Notified));
        Assert.That(_repository.GetStage(chainId, ThirdUserId).Status, Is.EqualTo(EscalationStageStatus.Pending));
    }

    private async Task<Guid> StartChainAsync()
    {
        var deadlineUtc = ProactiveApprovalDeadline.Compute(StartedAtUtc, Roster.Count, WindowMinutes);

        return (await _chainService.StartConditionApprovalChainAsync(
            new StartConditionApprovalChainRequest(ConditionId, GroupId, Roster, deadlineUtc)))!.Value;
    }
}
