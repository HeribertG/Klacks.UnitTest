// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ThoroughRecalculationOnSchedulingRuleChangedHandler: the window spans from the earliest
/// referencing contract's ValidFrom to the latest existing work date, unreferenced rules and windows
/// without works never queue anything.
/// </summary>

using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Domain.Events;
using Klacks.Api.Infrastructure.Events.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Events.Handlers;

[TestFixture]
public class ThoroughRecalculationOnSchedulingRuleChangedHandlerTests
{
    private ISurchargeRecalculationScope _scope = null!;
    private IThoroughRecalculationQueue _queue = null!;
    private ThoroughRecalculationOnSchedulingRuleChangedHandler _sut = null!;

    private readonly Guid _ruleId = Guid.NewGuid();
    private readonly Guid _contractA = Guid.NewGuid();
    private readonly Guid _contractB = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _scope = Substitute.For<ISurchargeRecalculationScope>();
        _queue = Substitute.For<IThoroughRecalculationQueue>();
        _sut = new ThoroughRecalculationOnSchedulingRuleChangedHandler(
            _scope, _queue, NullLogger<ThoroughRecalculationOnSchedulingRuleChangedHandler>.Instance);
    }

    [Test]
    public async Task Handle_ReferencingContracts_QueuesFromEarliestContractValidFromToLatestWorkDate()
    {
        var earliestFrom = new DateOnly(2026, 1, 1);
        var latestWorkDate = new DateOnly(2026, 9, 30);
        _scope.GetContractWindowsForRulesAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(r => r.Count == 1 && r.Contains(_ruleId)), Arg.Any<CancellationToken>())
            .Returns(new List<ContractRecalculationWindow>
            {
                new(_contractA, new DateOnly(2026, 4, 1), null),
                new(_contractB, earliestFrom, new DateOnly(2026, 12, 31)),
            });
        _scope.GetClientIdsForContractsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(c => c.Contains(_contractA) && c.Contains(_contractB)), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { _clientId });
        _scope.GetLatestWorkDateAsync(Arg.Any<IReadOnlyCollection<Guid>>(), earliestFrom, Arg.Any<CancellationToken>())
            .Returns(latestWorkDate);

        await _sut.HandleAsync(new SchedulingRuleChangedEvent(_ruleId));

        _queue.Received(1).QueueRecalculation(
            earliestFrom, latestWorkDate, null, null,
            Arg.Is<IReadOnlyCollection<Guid>>(c => c.Count == 1 && c.Contains(_clientId)));
    }

    [Test]
    public async Task Handle_RuleNotReferencedByAnyContract_DoesNotQueue()
    {
        _scope.GetContractWindowsForRulesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ContractRecalculationWindow>());

        await _sut.HandleAsync(new SchedulingRuleChangedEvent(_ruleId));

        _queue.DidNotReceiveWithAnyArgs().QueueRecalculation(default, default, null, null);
        await _scope.DidNotReceive().GetLatestWorkDateAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_NoWorksInWindow_DoesNotQueue()
    {
        _scope.GetContractWindowsForRulesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ContractRecalculationWindow> { new(_contractA, new DateOnly(2026, 4, 1), null) });
        _scope.GetClientIdsForContractsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { _clientId });
        _scope.GetLatestWorkDateAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((DateOnly?)null);

        await _sut.HandleAsync(new SchedulingRuleChangedEvent(_ruleId));

        _queue.DidNotReceiveWithAnyArgs().QueueRecalculation(default, default, null, null);
    }

    [Test]
    public async Task Handle_ScopeThrows_SwallowsAndDoesNotQueue()
    {
        _scope.GetContractWindowsForRulesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns<Task<List<ContractRecalculationWindow>>>(_ => throw new InvalidOperationException("db down"));

        await Should.NotThrowAsync(() => _sut.HandleAsync(new SchedulingRuleChangedEvent(_ruleId)));

        _queue.DidNotReceiveWithAnyArgs().QueueRecalculation(default, default, null, null);
    }
}
