// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the importance scale of the memory write skills: values are clamped into 1-10 and an omitted
/// importance defaults to 5. The scale is shared by every memory writer, so a refactoring that names
/// the bounds must keep every writer on the same numbers.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AgentMemoryImportanceScaleTests
{
    private const int DefaultImportance = 5;
    private const int LowestImportance = 1;
    private const int HighestImportance = 10;

    private static readonly Guid CallerId = Guid.NewGuid();
    private static readonly Guid MemoryId = Guid.NewGuid();

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = CallerId,
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Roles.Admin }
    };

    private static IEmbeddingService Embedding()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f });
        return embedding;
    }

    private static IAgentRepository Agents()
    {
        var agentRepo = Substitute.For<IAgentRepository>();
        agentRepo.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(new Agent { Id = Guid.NewGuid() });
        return agentRepo;
    }

    private static async Task<AgentMemory> AddAiMemory(Dictionary<string, object> extra)
    {
        AgentMemory? stored = null;
        var memoryRepo = Substitute.For<IAgentMemoryRepository>();
        memoryRepo.AddAsync(Arg.Do<AgentMemory>(m => stored = m), Arg.Any<CancellationToken>());
        var skill = new AddAiMemorySkill(memoryRepo, Agents(), Embedding());
        var parameters = new Dictionary<string, object>
        {
            ["key"] = "note",
            ["content"] = "temporary fact",
            ["category"] = MemoryCategories.Fact,
            ["isPinned"] = false
        };
        foreach (var (name, value) in extra)
        {
            parameters[name] = value;
        }

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue();
        return stored!;
    }

    private static async Task<AgentMemory> AddPersonalMemory(Dictionary<string, object> extra)
    {
        AgentMemory? stored = null;
        var memoryRepo = Substitute.For<IAgentMemoryRepository>();
        memoryRepo.AddAsync(Arg.Do<AgentMemory>(m => stored = m), Arg.Any<CancellationToken>());
        var skill = new AddPersonalMemorySkill(memoryRepo, Agents(), Embedding());
        var parameters = new Dictionary<string, object>
        {
            ["key"] = "theme",
            ["content"] = "prefers dark mode",
            ["category"] = MemoryCategories.Preference
        };
        foreach (var (name, value) in extra)
        {
            parameters[name] = value;
        }

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue();
        return stored!;
    }

    private static async Task<AgentMemory> UpdateAiMemory(int importance)
    {
        var memory = new AgentMemory
        {
            Id = MemoryId,
            UserId = null,
            Category = MemoryCategories.Fact,
            Key = "note",
            Content = "fact",
            Importance = DefaultImportance
        };
        var memoryRepo = Substitute.For<IAgentMemoryRepository>();
        memoryRepo.GetByIdAsync(MemoryId, Arg.Any<CancellationToken>()).Returns(memory);
        var skill = new UpdateAiMemorySkill(memoryRepo, Embedding());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["memoryId"] = MemoryId.ToString(),
            ["importance"] = importance
        });

        result.Success.ShouldBeTrue();
        return memory;
    }

    [Test]
    public async Task AddAiMemory_WithoutImportance_DefaultsToFive()
    {
        var memory = await AddAiMemory([]);

        memory.Importance.ShouldBe(DefaultImportance);
    }

    [TestCase(99, HighestImportance)]
    [TestCase(11, HighestImportance)]
    [TestCase(10, HighestImportance)]
    [TestCase(1, LowestImportance)]
    [TestCase(0, LowestImportance)]
    [TestCase(-4, LowestImportance)]
    public async Task AddAiMemory_ImportanceIsClampedIntoOneToTen(int given, int expected)
    {
        var memory = await AddAiMemory(new Dictionary<string, object> { ["importance"] = given });

        memory.Importance.ShouldBe(expected);
    }

    [Test]
    public async Task AddPersonalMemory_WithoutImportance_DefaultsToFive()
    {
        var memory = await AddPersonalMemory([]);

        memory.Importance.ShouldBe(DefaultImportance);
    }

    [TestCase(99, HighestImportance)]
    [TestCase(11, HighestImportance)]
    [TestCase(10, HighestImportance)]
    [TestCase(1, LowestImportance)]
    [TestCase(0, LowestImportance)]
    [TestCase(-4, LowestImportance)]
    public async Task AddPersonalMemory_ImportanceIsClampedIntoOneToTen(int given, int expected)
    {
        var memory = await AddPersonalMemory(new Dictionary<string, object> { ["importance"] = given });

        memory.Importance.ShouldBe(expected);
    }

    [TestCase(99, HighestImportance)]
    [TestCase(11, HighestImportance)]
    [TestCase(10, HighestImportance)]
    [TestCase(1, LowestImportance)]
    [TestCase(0, LowestImportance)]
    [TestCase(-4, LowestImportance)]
    public async Task UpdateAiMemory_ImportanceIsClampedIntoOneToTen(int given, int expected)
    {
        var memory = await UpdateAiMemory(given);

        memory.Importance.ShouldBe(expected);
    }

    [Test]
    public void AgentMemory_NewInstance_StartsAtImportanceFive()
    {
        new AgentMemory().Importance.ShouldBe(DefaultImportance);
    }
}
