// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for get_ai_memories dedup coherence with the per-turn ambient memory injection (P3):
/// memories already surfaced via SkillExecutionContext.InjectedMemoryIds are filtered out of both the
/// overview sample and the hybrid-search branch, with an appended count note so the model does not
/// think there were no matches; behavior is unchanged when no ids are known.
/// </summary>

using Klacks.Api.Application.Skills;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetAiMemoriesSkillTests
{
    private static readonly Guid AgentId = Guid.NewGuid();

    private IAgentMemoryRepository _memoryRepo = null!;
    private IAgentRepository _agentRepo = null!;
    private IEmbeddingService _embedding = null!;
    private GetAiMemoriesSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _memoryRepo = Substitute.For<IAgentMemoryRepository>();
        _agentRepo = Substitute.For<IAgentRepository>();
        _embedding = Substitute.For<IEmbeddingService>();
        _agentRepo.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(new Agent { Id = AgentId });
        _skill = new GetAiMemoriesSkill(_memoryRepo, _agentRepo, _embedding);
    }

    private static SkillExecutionContext Ctx(IReadOnlyList<Guid>? injectedMemoryIds = null) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "Admin" },
        InjectedMemoryIds = injectedMemoryIds,
    };

    private static AgentMemory Memory(Guid id, string key, int importance = 5) => new()
    {
        Id = id,
        Key = key,
        Content = $"content-{key}",
        Category = "fact",
        Importance = importance,
    };

    [Test]
    public async Task Overview_FiltersInjectedFromSample_AndAddsCountNote()
    {
        var injectedId = Guid.NewGuid();
        var allMemories = new List<AgentMemory>
        {
            Memory(injectedId, "already-in-context", importance: 9),
            Memory(Guid.NewGuid(), "fresh-one", importance: 8),
        };
        _memoryRepo.GetAllAsync(AgentId, Arg.Any<CancellationToken>()).Returns(allMemories);

        var result = await _skill.ExecuteAsync(Ctx(new[] { injectedId }), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        dynamic data = result.Data!;
        ((int)data.Count).ShouldBe(2);
        ((IEnumerable<object>)data.Sample).Count().ShouldBe(1);
        result.Message.ShouldContain("1 additional matches are already in your context above.");
    }

    [Test]
    public async Task Overview_WithoutInjectedIds_BehavesUnchanged()
    {
        var allMemories = new List<AgentMemory>
        {
            Memory(Guid.NewGuid(), "a"),
            Memory(Guid.NewGuid(), "b"),
        };
        _memoryRepo.GetAllAsync(AgentId, Arg.Any<CancellationToken>()).Returns(allMemories);

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        dynamic data = result.Data!;
        ((IEnumerable<object>)data.Sample).Count().ShouldBe(2);
        result.Message.ShouldNotContain("already in your context");
    }

    [Test]
    public async Task HybridSearch_FiltersInjected_AndAddsCountNote()
    {
        var injectedId = Guid.NewGuid();
        var freshId = Guid.NewGuid();
        _embedding.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 0.1f });
        _memoryRepo.HybridSearchAsync(AgentId, "term", Arg.Any<float[]?>(), 15, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<MemorySearchResult>
            {
                new(injectedId, "content-a", "already-in-context", "fact", 5, 0.9f, false),
                new(freshId, "content-b", "fresh-one", "fact", 5, 0.8f, false),
            });
        _memoryRepo.GetPinnedAsync(AgentId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new List<AgentMemory>());

        var result = await _skill.ExecuteAsync(
            Ctx(new[] { injectedId }), new Dictionary<string, object> { ["searchQuery"] = "term" });

        dynamic data = result.Data!;
        ((int)data.Count).ShouldBe(1);
        result.Message.ShouldContain("1 additional matches are already in your context above.");
    }

    [Test]
    public async Task HybridSearch_WithoutInjectedIds_BehavesUnchanged()
    {
        var idA = Guid.NewGuid();
        _embedding.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 0.1f });
        _memoryRepo.HybridSearchAsync(AgentId, "term", Arg.Any<float[]?>(), 15, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<MemorySearchResult> { new(idA, "content-a", "a", "fact", 5, 0.9f, false) });
        _memoryRepo.GetPinnedAsync(AgentId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new List<AgentMemory>());

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["searchQuery"] = "term" });

        dynamic data = result.Data!;
        ((int)data.Count).ShouldBe(1);
        result.Message.ShouldNotContain("already in your context");
    }
}
