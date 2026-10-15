// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The wake step of EscalationChainBackgroundService.RunCycleAsync: an absence chain registered ahead of time
/// (EscalationChain.WakeNotBeforeUtc, all stages Pending) is left alone until its wake instant, then its first stage
/// is woken exactly once - also when two sweep instances race, because only the instance whose TryClaimWakeAsync
/// moved the row advances the chain. A registered chain whose deadline has already passed is force-exhausted by the
/// earlier step instead of being woken. Wired with the real EscalationChainService over the in-memory repository
/// fake; the notifier is the only substitute. The claim/advance pairing itself is pinned with a substitute repository.
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
public class EscalationChainBackgroundServiceWakeTests
{
    private const string FirstUserId = "planner-first";
    private const string SecondUserId = "planner-second";

    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly DateTime ReportedAtUtc = new(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);

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
        _timeProvider = new SettableTimeProvider(ReportedAtUtc);

        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsEntity?)null);

        var rosterService = Substitute.For<IEscalationRosterService>();
        rosterService.GetOrderedRosterAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(new List<EscalationRosterCandidate> { new(FirstUserId, "First Planner"), new(SecondUserId, "Second Planner") });

        _notifier.NotifyStageAsync(Arg.Any<EscalationChain>(), Arg.Any<EscalationStage>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new EscalationNotificationResult(OfflineMessengerDeliveryOutcome.Sent, Guid.NewGuid(), "Inbox"));

        _chainService = new EscalationChainService(
            _repository,
            rosterService,
            _notifier,
            settingsReader,
            Substitute.For<IAgentConditionLedgerService>(),
            _timeProvider,
            Substitute.For<ILogger<EscalationChainService>>());

        _serviceProvider = BuildProvider(_repository, _chainService);
        _sut = NewSweep(_serviceProvider);
    }

    [TearDown]
    public async Task TearDown()
    {
        _sut.Dispose();
        await _serviceProvider.DisposeAsync();
    }

    [Test]
    public async Task RunCycleAsync_BeforeTheWakeInstant_LeavesTheRegisteredChainAlone()
    {
        var chainId = await RegisterFarFutureChainAsync();

        _timeProvider.Now = _repository.GetChain(chainId).WakeNotBeforeUtc!.Value.AddMinutes(-1);
        await _sut.RunCycleAsync(CancellationToken.None);

        Assert.That(_repository.GetChain(chainId).Stages.All(s => s.Status == EscalationStageStatus.Pending), Is.True);
        Assert.That(_repository.GetChain(chainId).WakeNotBeforeUtc, Is.Not.Null);
        await _notifier.DidNotReceiveWithAnyArgs().NotifyStageAsync(default!, default!, default, default);
    }

    [Test]
    public async Task RunCycleAsync_AtTheWakeInstant_WakesTheFirstStageOnce_AndASecondTickDoesNotWakeAgain()
    {
        var chainId = await RegisterFarFutureChainAsync();

        _timeProvider.Now = _repository.GetChain(chainId).WakeNotBeforeUtc!.Value;
        await _sut.RunCycleAsync(CancellationToken.None);
        await _sut.RunCycleAsync(CancellationToken.None);

        var chain = _repository.GetChain(chainId);
        Assert.That(chain.Status, Is.EqualTo(EscalationChainStatus.Running));
        Assert.That(chain.WakeNotBeforeUtc, Is.Null, "The claim clears the wake instant.");
        Assert.That(_repository.GetStage(chainId, FirstUserId).Status, Is.EqualTo(EscalationStageStatus.Notified));
        await _notifier.Received(1).NotifyStageAsync(
            Arg.Any<EscalationChain>(), Arg.Is<EscalationStage>(s => s.UserId == FirstUserId), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunCycleAsync_TwoSweepInstances_WakeTheRegisteredChainOnlyOnce()
    {
        var chainId = await RegisterFarFutureChainAsync();
        var otherProvider = BuildProvider(_repository, _chainService);
        using var otherSweep = NewSweep(otherProvider);

        _timeProvider.Now = _repository.GetChain(chainId).WakeNotBeforeUtc!.Value;
        await Task.WhenAll(_sut.RunCycleAsync(CancellationToken.None), otherSweep.RunCycleAsync(CancellationToken.None));

        await _notifier.Received(1).NotifyStageAsync(
            Arg.Any<EscalationChain>(), Arg.Any<EscalationStage>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await otherProvider.DisposeAsync();
    }

    [Test]
    public async Task RunCycleAsync_RegisteredChainAlreadyPastItsDeadline_IsForceExhaustedNotWoken()
    {
        var chainId = await RegisterFarFutureChainAsync();

        _timeProvider.Now = _repository.GetChain(chainId).DeadlineUtc.AddMinutes(1);
        await _sut.RunCycleAsync(CancellationToken.None);

        var chain = _repository.GetChain(chainId);
        Assert.That(chain.Status, Is.EqualTo(EscalationChainStatus.Exhausted));
        Assert.That(chain.OutcomeReason, Is.EqualTo(EscalationChainBackgroundService.OverdueOutcomeReason));
        Assert.That(chain.ExhaustedAtUtc, Is.EqualTo(_timeProvider.Now));
        await _notifier.DidNotReceiveWithAnyArgs().NotifyStageAsync(default!, default!, default, default);
        await _notifier.Received(1).NotifyExhaustedAsync(
            Arg.Is<EscalationChain>(c => c.Id == chainId),
            Arg.Is<IReadOnlyList<EscalationStage>>(stages => stages.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunCycleAsync_ClaimLost_DoesNotAdvanceTheChain()
    {
        var chainId = Guid.NewGuid();
        var (repository, chainService, provider) = SubstituteSweepParts();
        repository.GetChainIdsDueToWakeAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(new List<Guid> { chainId });
        repository.TryClaimWakeAsync(chainId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(false);
        using var sweep = NewSweep(provider);

        await sweep.RunCycleAsync(CancellationToken.None);

        await chainService.DidNotReceiveWithAnyArgs().AdvanceAsync(default, default);
        await provider.DisposeAsync();
    }

    [Test]
    public async Task RunCycleAsync_ClaimWon_AdvancesTheChainWithTheSweepInstant()
    {
        var chainId = Guid.NewGuid();
        var (repository, chainService, provider) = SubstituteSweepParts();
        repository.GetChainIdsDueToWakeAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(new List<Guid> { chainId });
        repository.TryClaimWakeAsync(chainId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        using var sweep = NewSweep(provider);

        await sweep.RunCycleAsync(CancellationToken.None);

        await repository.Received(1).TryClaimWakeAsync(chainId, ReportedAtUtc, Arg.Any<CancellationToken>());
        await chainService.Received(1).AdvanceAsync(chainId, Arg.Any<CancellationToken>());
        await provider.DisposeAsync();
    }

    [Test]
    public async Task RunCycleAsync_Order_ExhaustThenWakeThenExpireThenSupersede()
    {
        var chainId = Guid.NewGuid();
        var (repository, chainService, provider) = SubstituteSweepParts();
        repository.GetChainIdsDueToWakeAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(new List<Guid> { chainId });
        repository.TryClaimWakeAsync(chainId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        using var sweep = NewSweep(provider);

        await sweep.RunCycleAsync(CancellationToken.None);

        Received.InOrder(() =>
        {
            repository.GetOverdueRunningChainIdsAsync(ReportedAtUtc, Arg.Any<CancellationToken>());
            repository.GetChainIdsDueToWakeAsync(ReportedAtUtc, Arg.Any<CancellationToken>());
            repository.TryClaimWakeAsync(chainId, ReportedAtUtc, Arg.Any<CancellationToken>());
            chainService.AdvanceAsync(chainId, Arg.Any<CancellationToken>());
            repository.GetDueStagesAsync(ReportedAtUtc, Arg.Any<CancellationToken>());
            repository.GetRunningChainsWithAbsenceBreakAsync(Arg.Any<CancellationToken>());
        });
        await provider.DisposeAsync();
    }

    private (IEscalationChainRepository Repository, IEscalationChainService ChainService, ServiceProvider Provider) SubstituteSweepParts()
    {
        var repository = Substitute.For<IEscalationChainRepository>();
        repository.GetOverdueRunningChainIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        repository.GetChainIdsDueToWakeAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        repository.GetDueStagesAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(new List<EscalationStage>());
        repository.GetRunningChainsWithAbsenceBreakAsync(Arg.Any<CancellationToken>()).Returns(new List<EscalationChain>());
        var chainService = Substitute.For<IEscalationChainService>();
        return (repository, chainService, BuildProvider(repository, chainService));
    }

    private async Task<Guid> RegisterFarFutureChainAsync()
    {
        var chainId = await _chainService.StartChainAsync(new StartEscalationChainRequest(
            Guid.NewGuid(), GroupId, Guid.NewGuid(), "Absent Employee", ReportedAtUtc.AddDays(10), AbsenceBreakId: null));
        Assert.That(_repository.GetChain(chainId!.Value).WakeNotBeforeUtc, Is.Not.Null, "Precondition: the chain is only registered.");
        return chainId.Value;
    }

    private static ServiceProvider BuildProvider(IEscalationChainRepository repository, IEscalationChainService chainService)
    {
        var services = new ServiceCollection();
        services.AddSingleton(repository);
        services.AddSingleton(chainService);
        return services.BuildServiceProvider();
    }

    private EscalationChainBackgroundService NewSweep(IServiceProvider provider) =>
        new(provider,
            _timeProvider,
            Options.Create(new BackgroundServiceOptions { EscalationChain = true }),
            NullLogger<EscalationChainBackgroundService>.Instance);
}
