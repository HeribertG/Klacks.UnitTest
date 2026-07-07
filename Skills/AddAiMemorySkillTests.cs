// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for add_ai_memory: durable company invariants (fact/learned_fact/system_knowledge)
/// are auto-pinned with a boosted importance so they survive the per-turn pinned cap, while an
/// explicit isPinned choice and personal categories still behave as before.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddAiMemorySkillTests
{
    private static readonly Guid AdminId = Guid.NewGuid();

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = AdminId,
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "Admin" }
    };

    private static (AddAiMemorySkill skill, Func<AgentMemory?> captured) BuildSkill()
    {
        AgentMemory? stored = null;
        var memoryRepo = Substitute.For<IAgentMemoryRepository>();
        memoryRepo.AddAsync(Arg.Do<AgentMemory>(m => stored = m), Arg.Any<CancellationToken>());
        var agentRepo = Substitute.For<IAgentRepository>();
        agentRepo.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(new Agent { Id = Guid.NewGuid() });
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f });
        return (new AddAiMemorySkill(memoryRepo, agentRepo, embedding), () => stored);
    }

    [Test]
    public async Task AddAiMemory_CompanyInvariant_WithoutPin_IsAutoPinnedAndImportanceBoosted()
    {
        var (skill, captured) = BuildSkill();

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["key"] = "group-naming",
            ["content"] = "Groups are named after cities and municipalities",
            ["category"] = MemoryCategories.LearnedFact,
            ["importance"] = 5
        });

        result.Success.ShouldBeTrue();
        var memory = captured();
        memory.ShouldNotBeNull();
        memory!.UserId.ShouldBeNull();
        memory.IsPinned.ShouldBeTrue();
        memory.Importance.ShouldBe(MemoryCategories.CompanyInvariantPinnedImportance);
    }

    [Test]
    public async Task AddAiMemory_CompanyInvariant_ExplicitUnpinned_IsRespected()
    {
        var (skill, captured) = BuildSkill();

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["key"] = "note",
            ["content"] = "temporary fact",
            ["category"] = MemoryCategories.Fact,
            ["importance"] = 3,
            ["isPinned"] = false
        });

        result.Success.ShouldBeTrue();
        var memory = captured();
        memory.ShouldNotBeNull();
        memory!.IsPinned.ShouldBeFalse();
        memory.Importance.ShouldBe(3);
    }

    [Test]
    public async Task AddAiMemory_PersonalCategory_IsUserScoped_AndNotAutoPinned()
    {
        var (skill, captured) = BuildSkill();

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["key"] = "theme",
            ["content"] = "prefers dark mode",
            ["category"] = MemoryCategories.Preference
        });

        result.Success.ShouldBeTrue();
        var memory = captured();
        memory.ShouldNotBeNull();
        memory!.UserId.ShouldBe(AdminId);
        memory.IsPinned.ShouldBeFalse();
    }
}
