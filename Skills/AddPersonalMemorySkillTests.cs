// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for add_personal_memory: the skill always scopes the stored memory to the
/// calling user (never company-wide) and coerces any non-personal category to user_info,
/// so a normal user can never inject shared knowledge.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddPersonalMemorySkillTests
{
    private static readonly Guid CallerId = Guid.NewGuid();

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = CallerId,
        TenantId = Guid.NewGuid(),
        UserName = "employee",
        UserPermissions = new List<string>()
    };

    private static (AddPersonalMemorySkill skill, Func<AgentMemory?> captured) BuildSkill()
    {
        AgentMemory? stored = null;
        var memoryRepo = Substitute.For<IAgentMemoryRepository>();
        memoryRepo.AddAsync(Arg.Do<AgentMemory>(m => stored = m), Arg.Any<CancellationToken>());
        var agentRepo = Substitute.For<IAgentRepository>();
        agentRepo.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(new Agent { Id = Guid.NewGuid() });
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f });
        return (new AddPersonalMemorySkill(memoryRepo, agentRepo, embedding), () => stored);
    }

    [Test]
    public async Task AddPersonalMemory_SharedCategory_IsCoercedToUserInfo_AndScopedToCaller()
    {
        var (skill, captured) = BuildSkill();

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["key"] = "diet",
            ["content"] = "I am vegetarian",
            ["category"] = MemoryCategories.LearnedFact
        });

        result.Success.ShouldBeTrue();
        var memory = captured();
        memory.ShouldNotBeNull();
        memory!.UserId.ShouldBe(CallerId);
        memory.Category.ShouldBe(MemoryCategories.UserInfo);
    }

    [Test]
    public async Task AddPersonalMemory_PersonalCategory_IsKept_AndScopedToCaller()
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
        memory!.UserId.ShouldBe(CallerId);
        memory.Category.ShouldBe(MemoryCategories.Preference);
    }
}
