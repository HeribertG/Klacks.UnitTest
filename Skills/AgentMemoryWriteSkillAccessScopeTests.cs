// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the owner check on the memory-writing skills. delete_ai_memory and
/// update_ai_memory are gated to administrators, but the role alone must not open a foreign personal
/// memory: AgentMemoryAccessPolicy lets an administrator change shared company knowledge while a
/// personal memory stays with its owner. A denied write must answer with the same "not found" text as
/// a genuinely missing memory, so the skill never confirms that a foreign memory exists, and it must
/// leave the repository untouched.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AgentMemoryWriteSkillAccessScopeTests
{
    private static readonly Guid AdminId = Guid.NewGuid();
    private static readonly Guid ForeignUserId = Guid.NewGuid();
    private static readonly Guid MemoryId = Guid.NewGuid();

    private const string MemoryIdParameter = "memoryId";
    private const string ExpectedDenialMessagePart = "not found";

    private static SkillExecutionContext AdminContext() => new()
    {
        UserId = AdminId,
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Roles.Admin }
    };

    private static SkillExecutionContext NonAdminContext() => new()
    {
        UserId = AdminId,
        TenantId = Guid.NewGuid(),
        UserName = "planner",
        UserPermissions = new List<string> { Roles.Authorised }
    };

    private static AgentMemory Memory(Guid? ownerId) => new()
    {
        Id = MemoryId,
        AgentId = Guid.NewGuid(),
        UserId = ownerId,
        Key = "some-key",
        Content = "some content",
        Category = ownerId == null ? MemoryCategories.LearnedFact : MemoryCategories.UserInfo
    };

    private static IAgentMemoryRepository RepositoryReturning(AgentMemory memory)
    {
        var repository = Substitute.For<IAgentMemoryRepository>();
        repository.GetByIdAsync(MemoryId, Arg.Any<CancellationToken>()).Returns(memory);
        return repository;
    }

    private static IEmbeddingService EmbeddingService()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f });
        return embedding;
    }

    [Test]
    public async Task DeleteAiMemory_AdminOnForeignPersonalMemory_IsDeniedAndNothingIsDeleted()
    {
        var repository = RepositoryReturning(Memory(ForeignUserId));
        var skill = new DeleteAiMemorySkill(repository);

        var result = await skill.ExecuteAsync(
            AdminContext(),
            new Dictionary<string, object> { [MemoryIdParameter] = MemoryId.ToString() });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(ExpectedDenialMessagePart);
        await repository.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteAiMemory_AdminOnSharedMemory_IsAllowed()
    {
        var repository = RepositoryReturning(Memory(null));
        var skill = new DeleteAiMemorySkill(repository);

        var result = await skill.ExecuteAsync(
            AdminContext(),
            new Dictionary<string, object> { [MemoryIdParameter] = MemoryId.ToString() });

        result.Success.ShouldBeTrue();
        await repository.Received(1).DeleteAsync(MemoryId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteAiMemory_OwnerOnOwnPersonalMemory_IsAllowed()
    {
        var repository = RepositoryReturning(Memory(AdminId));
        var skill = new DeleteAiMemorySkill(repository);

        var result = await skill.ExecuteAsync(
            AdminContext(),
            new Dictionary<string, object> { [MemoryIdParameter] = MemoryId.ToString() });

        result.Success.ShouldBeTrue();
        await repository.Received(1).DeleteAsync(MemoryId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteAiMemory_NonAdminOnSharedMemory_IsDeniedAndNothingIsDeleted()
    {
        var repository = RepositoryReturning(Memory(null));
        var skill = new DeleteAiMemorySkill(repository);

        var result = await skill.ExecuteAsync(
            NonAdminContext(),
            new Dictionary<string, object> { [MemoryIdParameter] = MemoryId.ToString() });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(ExpectedDenialMessagePart);
        await repository.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateAiMemory_AdminOnForeignPersonalMemory_IsDeniedAndNothingIsWritten()
    {
        var repository = RepositoryReturning(Memory(ForeignUserId));
        var skill = new UpdateAiMemorySkill(repository, EmbeddingService());

        var result = await skill.ExecuteAsync(
            AdminContext(),
            new Dictionary<string, object>
            {
                [MemoryIdParameter] = MemoryId.ToString(),
                ["content"] = "hijacked"
            });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(ExpectedDenialMessagePart);
        await repository.DidNotReceive().UpdateAsync(Arg.Any<AgentMemory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateAiMemory_AdminOnSharedMemory_IsAllowed()
    {
        var repository = RepositoryReturning(Memory(null));
        var skill = new UpdateAiMemorySkill(repository, EmbeddingService());

        var result = await skill.ExecuteAsync(
            AdminContext(),
            new Dictionary<string, object>
            {
                [MemoryIdParameter] = MemoryId.ToString(),
                ["content"] = "corrected company fact"
            });

        result.Success.ShouldBeTrue();
        await repository.Received(1).UpdateAsync(Arg.Any<AgentMemory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateAiMemory_OwnerOnOwnPersonalMemory_IsAllowed()
    {
        var repository = RepositoryReturning(Memory(AdminId));
        var skill = new UpdateAiMemorySkill(repository, EmbeddingService());

        var result = await skill.ExecuteAsync(
            AdminContext(),
            new Dictionary<string, object>
            {
                [MemoryIdParameter] = MemoryId.ToString(),
                ["content"] = "my own note"
            });

        result.Success.ShouldBeTrue();
        await repository.Received(1).UpdateAsync(Arg.Any<AgentMemory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateAiMemory_NonAdminOnSharedMemory_IsDeniedAndNothingIsWritten()
    {
        var repository = RepositoryReturning(Memory(null));
        var skill = new UpdateAiMemorySkill(repository, EmbeddingService());

        var result = await skill.ExecuteAsync(
            NonAdminContext(),
            new Dictionary<string, object>
            {
                [MemoryIdParameter] = MemoryId.ToString(),
                ["content"] = "hijacked"
            });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(ExpectedDenialMessagePart);
        await repository.DidNotReceive().UpdateAsync(Arg.Any<AgentMemory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteAiMemory_DenialMessage_IsIndistinguishableFromMissingMemory()
    {
        var foreignRepository = RepositoryReturning(Memory(ForeignUserId));
        var missingRepository = Substitute.For<IAgentMemoryRepository>();
        missingRepository.GetByIdAsync(MemoryId, Arg.Any<CancellationToken>()).Returns((AgentMemory?)null);

        var parameters = new Dictionary<string, object> { [MemoryIdParameter] = MemoryId.ToString() };

        var denied = await new DeleteAiMemorySkill(foreignRepository).ExecuteAsync(AdminContext(), parameters);
        var missing = await new DeleteAiMemorySkill(missingRepository).ExecuteAsync(AdminContext(), parameters);

        denied.Message.ShouldBe(
            missing.Message,
            "A denied delete must be indistinguishable from a missing memory, otherwise the skill " +
            "confirms that a foreign personal memory exists.");
    }
}
