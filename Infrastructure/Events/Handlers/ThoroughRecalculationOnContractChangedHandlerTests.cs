// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ThoroughRecalculationOnContractChangedHandler: scope/window determination, the cheap
/// no-works skip, the queue call and the defensive swallow of scope failures.
/// </summary>

using Klacks.Api.Domain.Events;
using Klacks.Api.Infrastructure.Events.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Events.Handlers;

[TestFixture]
public class ThoroughRecalculationOnContractChangedHandlerTests
{
    private ISurchargeRecalculationScope _scope = null!;
    private IThoroughRecalculationQueue _queue = null!;
    private ThoroughRecalculationOnContractChangedHandler _sut = null!;

    private readonly Guid _contractId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly DateOnly _from = new(2026, 3, 1);
    private readonly DateOnly _latestWorkDate = new(2026, 8, 15);

    [SetUp]
    public void SetUp()
    {
        _scope = Substitute.For<ISurchargeRecalculationScope>();
        _queue = Substitute.For<IThoroughRecalculationQueue>();
        _sut = new ThoroughRecalculationOnContractChangedHandler(
            _scope, _queue, NullLogger<ThoroughRecalculationOnContractChangedHandler>.Instance);
    }

    [Test]
    public async Task Handle_NoClientScope_ResolvesClientsFromContractAndQueuesUpToLatestWorkDate()
    {
        _scope.GetClientIdsForContractAsync(_contractId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { _clientId });
        _scope.GetLatestWorkDateAsync(Arg.Is<IReadOnlyCollection<Guid>>(c => c.Contains(_clientId)), _from, Arg.Any<CancellationToken>())
            .Returns(_latestWorkDate);
        _scope.HasWorksInWindowAsync(Arg.Any<IReadOnlyCollection<Guid>>(), _from, _latestWorkDate, Arg.Any<CancellationToken>())
            .Returns(true);

        await _sut.HandleAsync(new ContractChangedEvent(_contractId, null, _from, null));

        _queue.Received(1).QueueRecalculation(
            _from, _latestWorkDate, null, null,
            Arg.Is<IReadOnlyCollection<Guid>>(c => c.Count == 1 && c.Contains(_clientId)));
    }

    [Test]
    public async Task Handle_ExplicitClientAndWindow_QueuesWithoutContractLookup()
    {
        var until = new DateOnly(2026, 5, 31);
        _scope.HasWorksInWindowAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(c => c.Count == 1 && c.Contains(_clientId)), _from, until, Arg.Any<CancellationToken>())
            .Returns(true);

        await _sut.HandleAsync(new ContractChangedEvent(_contractId, _clientId, _from, until));

        _queue.Received(1).QueueRecalculation(
            _from, until, null, null,
            Arg.Is<IReadOnlyCollection<Guid>>(c => c.Count == 1 && c.Contains(_clientId)));
        await _scope.DidNotReceive().GetClientIdsForContractAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_NoClientsAssigned_DoesNotQueue()
    {
        _scope.GetClientIdsForContractAsync(_contractId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        await _sut.HandleAsync(new ContractChangedEvent(_contractId, null, _from, null));

        _queue.DidNotReceiveWithAnyArgs().QueueRecalculation(default, default, null, null);
    }

    [Test]
    public async Task Handle_NoWorksAtOrAfterWindowStart_DoesNotQueue()
    {
        _scope.GetClientIdsForContractAsync(_contractId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { _clientId });
        _scope.GetLatestWorkDateAsync(Arg.Any<IReadOnlyCollection<Guid>>(), _from, Arg.Any<CancellationToken>())
            .Returns((DateOnly?)null);

        await _sut.HandleAsync(new ContractChangedEvent(_contractId, null, _from, null));

        _queue.DidNotReceiveWithAnyArgs().QueueRecalculation(default, default, null, null);
    }

    [Test]
    public async Task Handle_NoWorksInExplicitWindow_DoesNotQueue()
    {
        var until = new DateOnly(2026, 5, 31);
        _scope.HasWorksInWindowAsync(Arg.Any<IReadOnlyCollection<Guid>>(), _from, until, Arg.Any<CancellationToken>())
            .Returns(false);

        await _sut.HandleAsync(new ContractChangedEvent(_contractId, _clientId, _from, until));

        _queue.DidNotReceiveWithAnyArgs().QueueRecalculation(default, default, null, null);
    }

    [Test]
    public async Task Handle_ScopeThrows_SwallowsAndDoesNotQueue()
    {
        _scope.GetClientIdsForContractAsync(_contractId, Arg.Any<CancellationToken>())
            .Returns<Task<List<Guid>>>(_ => throw new InvalidOperationException("db down"));

        await Should.NotThrowAsync(() => _sut.HandleAsync(new ContractChangedEvent(_contractId, null, _from, null)));

        _queue.DidNotReceiveWithAnyArgs().QueueRecalculation(default, default, null, null);
    }
}
