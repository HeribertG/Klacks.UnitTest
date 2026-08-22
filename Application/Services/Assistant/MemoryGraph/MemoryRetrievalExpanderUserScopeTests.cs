// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests that the silent 1-hop expansion never pulls a foreign personal memory into the turn. The
/// relation graph is built across all users of the agent, so a neighbour of a shared memory can be
/// another user's personal memory; loading it by id used to bypass every ownership filter and inject
/// it into the prompt without the reader ever calling an endpoint.
/// </summary>

using Klacks.Api.Application.Services.Assistant.MemoryGraph;
using Klacks.Api.Domain.Interfaces.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Services.Assistant.MemoryGraph;

[TestFixture]
public class MemoryRetrievalExpanderUserScopeTests
{
    private static (MemoryRetrievalExpander Expander, Guid AgentId, Guid HitId) Build(
        IReadOnlyList<AgentMemory> neighbours)
    {
        var agentId = Guid.NewGuid();
        var hitId = Guid.NewGuid();
        var relationRepo = Substitute.For<IMemoryRelationRepository>();
        var memoryRepo = Substitute.For<IAgentMemoryRepository>();

        relationRepo.NeighboursOfAsync(
                agentId, Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<double>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(neighbours.Select(m => m.Id).ToList());
        memoryRepo.GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(neighbours.ToList());

        return (new MemoryRetrievalExpander(relationRepo, memoryRepo, NullLogger<MemoryRetrievalExpander>.Instance),
            agentId, hitId);
    }

    [Test]
    public async Task ExpandAsync_DropsThePersonalNeighbourOfAnotherUser()
    {
        var foreignPersonal = new AgentMemory
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Key = "foreign", Content = "private note of another user"
        };
        var (expander, agentId, hitId) = Build(new[] { foreignPersonal });

        var result = await expander.ExpandAsync(
            agentId, Array.Empty<AgentMemory>(),
            new List<MemorySearchResult> { new(hitId, "hit", "hit", "fact", 5, 0.9f, false) },
            freeBudget: 3,
            userId: Guid.NewGuid());

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task ExpandAsync_KeepsSharedAndOwnNeighbours()
    {
        var reader = Guid.NewGuid();
        var shared = new AgentMemory { Id = Guid.NewGuid(), UserId = null, Key = "shared", Content = "company fact" };
        var own = new AgentMemory { Id = Guid.NewGuid(), UserId = reader, Key = "own", Content = "my note" };
        var (expander, agentId, hitId) = Build(new[] { shared, own });

        var result = await expander.ExpandAsync(
            agentId, Array.Empty<AgentMemory>(),
            new List<MemorySearchResult> { new(hitId, "hit", "hit", "fact", 5, 0.9f, false) },
            freeBudget: 3,
            userId: reader);

        result.Select(m => m.Id).ShouldBe(new[] { shared.Id, own.Id });
    }
}
