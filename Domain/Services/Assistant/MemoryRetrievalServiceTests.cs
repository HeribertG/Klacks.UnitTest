// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MemoryRetrievalService's 1-hop expansion wiring (P5): expansion only runs when the
/// hybrid search left free per-turn slots, a failing expander never breaks the base retrieval result,
/// and every injected memory (pinned, hybrid and expansion) is reported back via InjectedMemoryIds so
/// a same-turn get_ai_memories call can dedup against it.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class MemoryRetrievalServiceTests
{
    private static readonly Guid AgentId = Guid.NewGuid();
    private static readonly ContextBudgetProfile Budget = new(
        MaxHistoryMessages: 20, MaxToolsForProvider: 50, MaxPinnedMemories: 10, MaxMemoriesPerTurn: 5, MaxToolResultChars: 8000);

    private IAgentMemoryRepository _memoryRepo = null!;
    private IEmbeddingService _embedding = null!;
    private IMemoryRetrievalExpander _expander = null!;
    private MemoryRetrievalService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _memoryRepo = Substitute.For<IAgentMemoryRepository>();
        _embedding = Substitute.For<IEmbeddingService>();
        _expander = Substitute.For<IMemoryRetrievalExpander>();
        _embedding.IsAvailable.Returns(false);
        _memoryRepo.GetPinnedAsync(AgentId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new List<AgentMemory>());
        _memoryRepo.HybridSearchAsync(AgentId, Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<MemorySearchResult>());

        _sut = new MemoryRetrievalService(_memoryRepo, _embedding, _expander, NullLogger<MemoryRetrievalService>.Instance);
    }

    [Test]
    public async Task NoMemories_ReturnsEmptyResult_AndSkipsExpansion()
    {
        var result = await _sut.RetrieveRelevantMemoriesAsync(AgentId, "hello", budgetProfile: Budget);

        result.PromptText.ShouldBe(string.Empty);
        result.InjectedMemoryIds.ShouldBeEmpty();
        await _expander.DidNotReceive().ExpandAsync(
            Arg.Any<Guid>(), Arg.Any<IReadOnlyList<AgentMemory>>(), Arg.Any<IReadOnlyList<MemorySearchResult>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HybridResultsFillAllSlots_SkipsExpansion()
    {
        var hits = Enumerable.Range(0, Budget.MaxMemoriesPerTurn)
            .Select(i => new MemorySearchResult(Guid.NewGuid(), $"c{i}", $"k{i}", "fact", 5, 0.9f, false))
            .ToList();
        _memoryRepo.HybridSearchAsync(AgentId, Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(hits);

        var result = await _sut.RetrieveRelevantMemoriesAsync(AgentId, "hello there", budgetProfile: Budget);

        result.PromptText.ShouldNotContain("[RELATED]");
        await _expander.DidNotReceive().ExpandAsync(
            Arg.Any<Guid>(), Arg.Any<IReadOnlyList<AgentMemory>>(), Arg.Any<IReadOnlyList<MemorySearchResult>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExpanderThrows_BaseResultStillReturned()
    {
        var hit = new MemorySearchResult(Guid.NewGuid(), "content", "key", "fact", 5, 0.9f, false);
        _memoryRepo.HybridSearchAsync(AgentId, Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<MemorySearchResult> { hit });
        _expander.ExpandAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<AgentMemory>>(), Arg.Any<IReadOnlyList<MemorySearchResult>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<AgentMemory>>(_ => throw new InvalidOperationException("boom"));

        var result = await _sut.RetrieveRelevantMemoriesAsync(AgentId, "hello there", budgetProfile: Budget);

        result.PromptText.ShouldContain("[RELEVANT]");
        result.PromptText.ShouldNotContain("[RELATED]");
        result.InjectedMemoryIds.ShouldContain(hit.Id);
    }

    [Test]
    public async Task ExpansionMemories_AreRenderedAsRelated_AndAddedToInjectedIds()
    {
        var hit = new MemorySearchResult(Guid.NewGuid(), "content", "key", "fact", 5, 0.9f, false);
        var expanded = new AgentMemory { Id = Guid.NewGuid(), Key = "neighbour", Content = "neighbour-content", Category = "fact" };
        _memoryRepo.HybridSearchAsync(AgentId, Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<MemorySearchResult> { hit });
        _expander.ExpandAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<AgentMemory>>(), Arg.Any<IReadOnlyList<MemorySearchResult>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentMemory> { expanded });

        var result = await _sut.RetrieveRelevantMemoriesAsync(AgentId, "hello there", budgetProfile: Budget);

        result.PromptText.ShouldContain("[RELATED]");
        result.PromptText.ShouldContain("neighbour-content");
        result.InjectedMemoryIds.ShouldContain(expanded.Id);
        result.InjectedMemoryIds.ShouldContain(hit.Id);
    }
}
