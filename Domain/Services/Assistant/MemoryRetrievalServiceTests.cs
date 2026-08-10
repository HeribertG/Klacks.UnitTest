// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MemoryRetrievalService's 1-hop expansion wiring (P5): expansion only runs when the
/// hybrid search left free per-turn slots, a failing expander never breaks the base retrieval result,
/// and every injected memory (pinned, hybrid and expansion) is reported back via InjectedMemoryIds so
/// a same-turn get_ai_memories call can dedup against it.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class MemoryRetrievalServiceTests
{
    private static readonly Guid AgentId = Guid.NewGuid();

    // The update runs on a background task, so the assertion has to wait for it rather than assume it
    // already ran. Generous enough not to flake on a loaded build agent, short enough to fail fast.
    private static readonly TimeSpan BackgroundUpdateTimeout = TimeSpan.FromSeconds(5);

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

        _sut = new MemoryRetrievalService(
            _memoryRepo, _embedding, _expander, BuildScopeFactory(_memoryRepo), NullLogger<MemoryRetrievalService>.Instance);
    }

    /// <summary>
    /// The access-count update runs on its own scope so it never shares the request DbContext. The
    /// scope hands back the same repository substitute, so assertions on it are unaffected.
    /// </summary>
    private static IServiceScopeFactory BuildScopeFactory(IAgentMemoryRepository repository)
    {
        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IAgentMemoryRepository)).Returns(repository);
        scope.ServiceProvider.Returns(provider);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    /// <summary>
    /// The access-count update is fire-and-forget. Running it on the injected repository put a second
    /// operation on the request-scoped DbContext while the turn was still querying it, which EF rejects
    /// ("A second operation was started on this context instance") and the task's own catch swallowed —
    /// the counts were lost silently. It must therefore go through a scope of its own, never through the
    /// request repository.
    /// </summary>
    [Test]
    public async Task AccessCountUpdate_RunsOnItsOwnScope_NeverOnTheRequestRepository()
    {
        var scopedRepo = Substitute.For<IAgentMemoryRepository>();
        var updateObserved = new TaskCompletionSource();
        scopedRepo
            .UpdateAccessCountsAsync(Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                updateObserved.TrySetResult();
                return Task.CompletedTask;
            });

        var hit = new MemorySearchResult(Guid.NewGuid(), "content", "key", "fact", 5, 0.9f, false);
        _memoryRepo.HybridSearchAsync(AgentId, Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<MemorySearchResult> { hit });

        var sut = new MemoryRetrievalService(
            _memoryRepo, _embedding, _expander, BuildScopeFactory(scopedRepo), NullLogger<MemoryRetrievalService>.Instance);

        await sut.RetrieveRelevantMemoriesAsync(AgentId, "hello there", budgetProfile: Budget);

        var completed = await Task.WhenAny(updateObserved.Task, Task.Delay(BackgroundUpdateTimeout));
        completed.ShouldBe(updateObserved.Task, "the access-count update never reached the scoped repository");

        await scopedRepo.Received(1).UpdateAccessCountsAsync(
            Arg.Is<List<Guid>>(ids => ids.Contains(hit.Id)), Arg.Any<CancellationToken>());
        await _memoryRepo.DidNotReceive().UpdateAccessCountsAsync(
            Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>());
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
