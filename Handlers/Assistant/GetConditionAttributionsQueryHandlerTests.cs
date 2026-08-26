// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GetConditionAttributionsQueryHandler. Covers: an empty id list never reaches the scope
/// resolver or the repository; a caller who is not a planner gets an empty list without the ledger ever
/// being read; the resolved scope is forwarded to the repository verbatim, both for an admin and for a
/// restricted planner; several Executed rows on one entity collapse to the newest handling; and a row
/// without an EntityId is dropped instead of being mapped to Guid.Empty.
/// </summary>

using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class GetConditionAttributionsQueryHandlerTests
{
    private static readonly DateTime StartUtc = new(2026, 8, 26, 6, 0, 0, DateTimeKind.Utc);
    private const string UserId = "11111111-1111-1111-1111-111111111111";

    private IAgentConditionScopeResolver _scopeResolver = null!;
    private IAgentConditionRepository _conditionRepository = null!;
    private GetConditionAttributionsQueryHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _scopeResolver = Substitute.For<IAgentConditionScopeResolver>();
        _conditionRepository = Substitute.For<IAgentConditionRepository>();
        _sut = new GetConditionAttributionsQueryHandler(_scopeResolver, _conditionRepository);
    }

    private static AgentCondition Row(Guid? entityId, DateTime? handledAtUtc, string kind = AgentTriggerKinds.EmptyContainer) => new()
    {
        Id = Guid.NewGuid(),
        TriggerKind = kind,
        EntityId = entityId,
        HandledAtUtc = handledAtUtc,
        Status = AgentConditionStatus.Executed
    };

    private static GetConditionAttributionsQuery Query(params Guid[] entityIds) =>
        new() { UserId = UserId, EntityIds = entityIds };

    [Test]
    public async Task EmptyEntityIds_ShortCircuitsWithoutResolvingScopeOrReadingTheLedger()
    {
        var result = await _sut.Handle(Query(), CancellationToken.None);

        result.ShouldBeEmpty();
        await _scopeResolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
        await _conditionRepository.DidNotReceiveWithAnyArgs()
            .GetExecutedForEntitiesAsync(default!, default, default!, default);
    }

    [Test]
    public async Task NonPlanner_GetsAnEmptyListAndTheLedgerIsNeverRead()
    {
        _scopeResolver.ResolveAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.NotAPlanner());

        var result = await _sut.Handle(Query(Guid.NewGuid()), CancellationToken.None);

        result.ShouldBeEmpty();
        await _conditionRepository.DidNotReceiveWithAnyArgs()
            .GetExecutedForEntitiesAsync(default!, default, default!, default);
    }

    [Test]
    public async Task RestrictedPlanner_ForwardsTheResolvedScopeVerbatim()
    {
        var visibleRootId = Guid.NewGuid();
        var entityId = Guid.NewGuid();
        _scopeResolver.ResolveAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.Restricted(new HashSet<Guid> { visibleRootId }));
        _conditionRepository
            .GetExecutedForEntitiesAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<bool>(), Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await _sut.Handle(Query(entityId), CancellationToken.None);

        await _conditionRepository.Received(1).GetExecutedForEntitiesAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(entityId)),
            false,
            Arg.Is<IReadOnlySet<Guid>>(roots => roots.Count == 1 && roots.Contains(visibleRootId)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Admin_IsForwardedAsUnrestricted()
    {
        _scopeResolver.ResolveAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.Unrestricted());
        _conditionRepository
            .GetExecutedForEntitiesAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<bool>(), Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await _sut.Handle(Query(Guid.NewGuid()), CancellationToken.None);

        await _conditionRepository.Received(1).GetExecutedForEntitiesAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), true, Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MapsOneAttributionPerEntity_KeepingTheNewestHandling()
    {
        var entityId = Guid.NewGuid();
        var otherEntityId = Guid.NewGuid();
        _scopeResolver.ResolveAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.Unrestricted());
        _conditionRepository
            .GetExecutedForEntitiesAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<bool>(), Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([
                Row(entityId, StartUtc.AddHours(5)),
                Row(entityId, StartUtc.AddHours(1)),
                Row(otherEntityId, StartUtc.AddHours(2), AgentTriggerKinds.UnstaffedShift)
            ]);

        var result = await _sut.Handle(Query(entityId, otherEntityId), CancellationToken.None);

        result.Count.ShouldBe(2);
        var attribution = result.Single(a => a.EntityId == entityId);
        attribution.HandledAtUtc.ShouldBe(StartUtc.AddHours(5));
        attribution.TriggerKind.ShouldBe(AgentTriggerKinds.EmptyContainer);
        result.Single(a => a.EntityId == otherEntityId).TriggerKind.ShouldBe(AgentTriggerKinds.UnstaffedShift);
    }

    [Test]
    public async Task DropsRowsWithoutAnEntityId_InsteadOfMappingThemToEmptyGuid()
    {
        _scopeResolver.ResolveAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.Unrestricted());
        _conditionRepository
            .GetExecutedForEntitiesAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<bool>(), Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([Row(entityId: null, StartUtc)]);

        var result = await _sut.Handle(Query(Guid.NewGuid()), CancellationToken.None);

        result.ShouldBeEmpty();
    }
}
