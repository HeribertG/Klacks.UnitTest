// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ThoroughRecalculationOnRateRevisionsImportedHandler: window start prefers the earliest
/// changed revision ValidFrom and falls back to the earliest referencing contract's ValidFrom; empty
/// rule sets, unreferenced rules and windows without works never queue anything.
/// </summary>

using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Domain.Events;
using Klacks.Api.Infrastructure.Events.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Events.Handlers;

[TestFixture]
public class ThoroughRecalculationOnRateRevisionsImportedHandlerTests
{
    private ISurchargeRecalculationScope _scope = null!;
    private IThoroughRecalculationQueue _queue = null!;
    private ThoroughRecalculationOnRateRevisionsImportedHandler _sut = null!;

    private readonly Guid _ruleId = Guid.NewGuid();
    private readonly Guid _contractId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly DateOnly _latestWorkDate = new(2026, 10, 31);

    [SetUp]
    public void SetUp()
    {
        _scope = Substitute.For<ISurchargeRecalculationScope>();
        _queue = Substitute.For<IThoroughRecalculationQueue>();
        _sut = new ThoroughRecalculationOnRateRevisionsImportedHandler(
            _scope, _queue, NullLogger<ThoroughRecalculationOnRateRevisionsImportedHandler>.Instance);
    }

    [Test]
    public async Task Handle_ChangedRevisions_QueuesFromEarliestChangedValidFrom()
    {
        var earliestChanged = new DateOnly(2026, 7, 1);
        ArrangeReferencedRule(contractValidFrom: new DateOnly(2026, 1, 1));
        _scope.GetLatestWorkDateAsync(Arg.Any<IReadOnlyCollection<Guid>>(), earliestChanged, Arg.Any<CancellationToken>())
            .Returns(_latestWorkDate);

        await _sut.HandleAsync(new SchedulingRuleRateRevisionsImportedEvent([_ruleId], earliestChanged));

        _queue.Received(1).QueueRecalculation(
            earliestChanged, _latestWorkDate, null, null,
            Arg.Is<IReadOnlyCollection<Guid>>(c => c.Count == 1 && c.Contains(_clientId)));
    }

    [Test]
    public async Task Handle_OnlyPresetFieldsChanged_FallsBackToEarliestContractValidFrom()
    {
        var contractValidFrom = new DateOnly(2026, 2, 1);
        ArrangeReferencedRule(contractValidFrom);
        _scope.GetLatestWorkDateAsync(Arg.Any<IReadOnlyCollection<Guid>>(), contractValidFrom, Arg.Any<CancellationToken>())
            .Returns(_latestWorkDate);

        await _sut.HandleAsync(new SchedulingRuleRateRevisionsImportedEvent([_ruleId], null));

        _queue.Received(1).QueueRecalculation(
            contractValidFrom, _latestWorkDate, null, null,
            Arg.Is<IReadOnlyCollection<Guid>>(c => c.Count == 1 && c.Contains(_clientId)));
    }

    [Test]
    public async Task Handle_EmptyRuleSet_DoesNothing()
    {
        await _sut.HandleAsync(new SchedulingRuleRateRevisionsImportedEvent([], new DateOnly(2026, 7, 1)));

        _queue.DidNotReceiveWithAnyArgs().QueueRecalculation(default, default, null, null);
        await _scope.DidNotReceive().GetContractWindowsForRulesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_NoWorksInWindow_DoesNotQueue()
    {
        ArrangeReferencedRule(contractValidFrom: new DateOnly(2026, 2, 1));
        _scope.GetLatestWorkDateAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((DateOnly?)null);

        await _sut.HandleAsync(new SchedulingRuleRateRevisionsImportedEvent([_ruleId], new DateOnly(2026, 7, 1)));

        _queue.DidNotReceiveWithAnyArgs().QueueRecalculation(default, default, null, null);
    }

    [Test]
    public async Task Handle_ScopeThrows_SwallowsAndDoesNotQueue()
    {
        _scope.GetContractWindowsForRulesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns<Task<List<ContractRecalculationWindow>>>(_ => throw new InvalidOperationException("db down"));

        await Should.NotThrowAsync(() => _sut.HandleAsync(new SchedulingRuleRateRevisionsImportedEvent([_ruleId], null)));

        _queue.DidNotReceiveWithAnyArgs().QueueRecalculation(default, default, null, null);
    }

    private void ArrangeReferencedRule(DateOnly contractValidFrom)
    {
        _scope.GetContractWindowsForRulesAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(r => r.Contains(_ruleId)), Arg.Any<CancellationToken>())
            .Returns(new List<ContractRecalculationWindow> { new(_contractId, contractValidFrom, null) });
        _scope.GetClientIdsForContractsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { _clientId });
    }
}
