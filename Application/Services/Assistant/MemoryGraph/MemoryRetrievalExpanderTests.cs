// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the silent 1-hop memory-retrieval expansion (P5): only free per-turn slots are
/// filled, already-pinned/hybrid-matched memories are never duplicated, and a candidate list larger
/// than the free budget is capped rather than evicting anything already selected.
/// </summary>

using Klacks.Api.Application.Services.Assistant.MemoryGraph;
using Klacks.Api.Domain.Interfaces.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Services.Assistant.MemoryGraph;

[TestFixture]
public class MemoryRetrievalExpanderTests
{
    [Test]
    public void BuildExpansionIds_ExcludesAlreadyPresentIds()
    {
        var pinned = Guid.NewGuid();
        var fresh = Guid.NewGuid();

        var result = MemoryRetrievalExpander.BuildExpansionIds(
            new[] { pinned, fresh }, new HashSet<Guid> { pinned }, slots: 5);

        result.ShouldBe(new[] { fresh });
    }

    [Test]
    public void BuildExpansionIds_DeduplicatesRepeatedCandidates()
    {
        var id = Guid.NewGuid();

        var result = MemoryRetrievalExpander.BuildExpansionIds(
            new[] { id, id }, new HashSet<Guid>(), slots: 5);

        result.ShouldBe(new[] { id });
    }

    [Test]
    public void BuildExpansionIds_NeverExceedsFreeSlots()
    {
        var candidates = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        var result = MemoryRetrievalExpander.BuildExpansionIds(candidates, new HashSet<Guid>(), slots: 2);

        result.Count.ShouldBe(2);
    }

    [Test]
    public void BuildExpansionIds_ZeroSlots_ReturnsEmpty()
    {
        var result = MemoryRetrievalExpander.BuildExpansionIds(
            new[] { Guid.NewGuid() }, new HashSet<Guid>(), slots: 0);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task ExpandAsync_NoHybridResults_SkipsWithoutCallingRepository()
    {
        var relationRepo = Substitute.For<IMemoryRelationRepository>();
        var memoryRepo = Substitute.For<IAgentMemoryRepository>();
        var expander = new MemoryRetrievalExpander(relationRepo, memoryRepo, NullLogger<MemoryRetrievalExpander>.Instance);

        var result = await expander.ExpandAsync(
            Guid.NewGuid(), Array.Empty<AgentMemory>(), Array.Empty<MemorySearchResult>(), freeBudget: 3);

        result.ShouldBeEmpty();
        await relationRepo.DidNotReceive().NeighboursOfAsync(
            Arg.Any<Guid>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<double>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExpandAsync_LoadsFullContentOfPickedNeighbours()
    {
        var agentId = Guid.NewGuid();
        var hitId = Guid.NewGuid();
        var neighbourId = Guid.NewGuid();
        var relationRepo = Substitute.For<IMemoryRelationRepository>();
        var memoryRepo = Substitute.For<IAgentMemoryRepository>();
        relationRepo.NeighboursOfAsync(agentId, Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<double>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { neighbourId });
        memoryRepo.GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentMemory> { new() { Id = neighbourId, Key = "n", Content = "n" } });
        var expander = new MemoryRetrievalExpander(relationRepo, memoryRepo, NullLogger<MemoryRetrievalExpander>.Instance);

        var result = await expander.ExpandAsync(
            agentId, Array.Empty<AgentMemory>(),
            new List<MemorySearchResult> { new(hitId, "hit", "hit", "fact", 5, 0.9f, false) },
            freeBudget: 3);

        result.Select(m => m.Id).ShouldBe(new[] { neighbourId });
    }
}
