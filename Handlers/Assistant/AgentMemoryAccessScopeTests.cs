// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests the ownership rules of the agent memory handlers: reads are scoped to the calling user, a
/// foreign personal memory can neither be updated, deleted nor pinned, a shared memory may only be
/// changed by an administrator, and a newly created memory is stamped with its owner so that a
/// personal memory never becomes company-wide knowledge.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class AgentMemoryAccessScopeTests
{
    private IAgentMemoryRepository _memoryRepository = null!;
    private IEmbeddingService _embeddingService = null!;
    private IUserService _userService = null!;

    private Guid _agentId;
    private Guid _userA;
    private Guid _userB;

    [SetUp]
    public void SetUp()
    {
        _memoryRepository = Substitute.For<IAgentMemoryRepository>();
        _embeddingService = Substitute.For<IEmbeddingService>();
        _embeddingService.IsAvailable.Returns(false);
        _userService = Substitute.For<IUserService>();

        _agentId = Guid.NewGuid();
        _userA = Guid.NewGuid();
        _userB = Guid.NewGuid();
    }

    private void ActAs(Guid userId, bool isAdmin = false)
    {
        _userService.GetId().Returns(userId);
        _userService.IsAdmin().Returns(isAdmin);
    }

    private AgentMemory Existing(Guid? ownerId, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        AgentId = _agentId,
        UserId = ownerId,
        Key = "key",
        Content = "content",
        Category = ownerId == null ? MemoryCategories.Fact : MemoryCategories.Preference
    };

    private void RepositoryReturns(AgentMemory memory) =>
        _memoryRepository.GetByIdAsync(memory.Id, Arg.Any<CancellationToken>()).Returns(memory);

    [Test]
    public async Task Delete_UserBOnPersonalMemoryOfUserA_IsRejectedAndNothingIsDeleted()
    {
        var memory = Existing(_userA);
        RepositoryReturns(memory);
        ActAs(_userB);
        var handler = new DeleteAgentMemoryCommandHandler(_memoryRepository, _userService);

        await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new DeleteAgentMemoryCommand { AgentId = _agentId, MemoryId = memory.Id }, CancellationToken.None));

        await _memoryRepository.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_OwnerOnTheirOwnPersonalMemory_IsAllowed()
    {
        var memory = Existing(_userB);
        RepositoryReturns(memory);
        ActAs(_userB);
        var handler = new DeleteAgentMemoryCommandHandler(_memoryRepository, _userService);

        await handler.Handle(new DeleteAgentMemoryCommand { AgentId = _agentId, MemoryId = memory.Id }, CancellationToken.None);

        await _memoryRepository.Received(1).DeleteAsync(memory.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_NonAdminOnSharedMemory_IsRejected()
    {
        var memory = Existing(null);
        RepositoryReturns(memory);
        ActAs(_userB);
        var handler = new DeleteAgentMemoryCommandHandler(_memoryRepository, _userService);

        await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new DeleteAgentMemoryCommand { AgentId = _agentId, MemoryId = memory.Id }, CancellationToken.None));

        await _memoryRepository.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_AdminOnSharedMemory_IsAllowed()
    {
        var memory = Existing(null);
        RepositoryReturns(memory);
        ActAs(_userB, isAdmin: true);
        var handler = new DeleteAgentMemoryCommandHandler(_memoryRepository, _userService);

        await handler.Handle(new DeleteAgentMemoryCommand { AgentId = _agentId, MemoryId = memory.Id }, CancellationToken.None);

        await _memoryRepository.Received(1).DeleteAsync(memory.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Update_UserBOnPersonalMemoryOfUserA_IsRejectedAndNothingIsWritten()
    {
        var memory = Existing(_userA);
        RepositoryReturns(memory);
        ActAs(_userB);
        var handler = new UpdateAgentMemoryCommandHandler(_memoryRepository, _embeddingService, _userService);

        var result = await handler.Handle(
            new UpdateAgentMemoryCommand { AgentId = _agentId, MemoryId = memory.Id, Content = "hijacked" },
            CancellationToken.None);

        result.ShouldBeNull();
        memory.Content.ShouldBe("content");
        await _memoryRepository.DidNotReceive().UpdateAsync(Arg.Any<AgentMemory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Update_OwnerOnTheirOwnPersonalMemory_IsAllowed()
    {
        var memory = Existing(_userB);
        RepositoryReturns(memory);
        ActAs(_userB);
        var handler = new UpdateAgentMemoryCommandHandler(_memoryRepository, _embeddingService, _userService);

        var result = await handler.Handle(
            new UpdateAgentMemoryCommand { AgentId = _agentId, MemoryId = memory.Id, Content = "my own note" },
            CancellationToken.None);

        result.ShouldNotBeNull();
        await _memoryRepository.Received(1).UpdateAsync(memory, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TogglePin_UserBOnPersonalMemoryOfUserA_IsRejected()
    {
        var memory = Existing(_userA);
        RepositoryReturns(memory);
        ActAs(_userB);
        var handler = new ToggleMemoryPinCommandHandler(_memoryRepository, _userService);

        var result = await handler.Handle(
            new ToggleMemoryPinCommand { AgentId = _agentId, MemoryId = memory.Id }, CancellationToken.None);

        result.ShouldBeNull();
        memory.IsPinned.ShouldBeFalse();
        await _memoryRepository.DidNotReceive().UpdateAsync(Arg.Any<AgentMemory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TogglePin_NonAdminOnSharedMemory_IsRejected()
    {
        var memory = Existing(null);
        RepositoryReturns(memory);
        ActAs(_userB);
        var handler = new ToggleMemoryPinCommandHandler(_memoryRepository, _userService);

        var result = await handler.Handle(
            new ToggleMemoryPinCommand { AgentId = _agentId, MemoryId = memory.Id }, CancellationToken.None);

        result.ShouldBeNull();
        await _memoryRepository.DidNotReceive().UpdateAsync(Arg.Any<AgentMemory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetMemories_ScopesTheListQueryToTheCallingUser()
    {
        ActAs(_userB);
        _memoryRepository.GetAllAsync(_agentId, _userB, Arg.Any<CancellationToken>()).Returns(new List<AgentMemory>());
        var handler = new GetAgentMemoriesQueryHandler(_memoryRepository, _embeddingService, _userService);

        await handler.Handle(new GetAgentMemoriesQuery { AgentId = _agentId }, CancellationToken.None);

        await _memoryRepository.Received(1).GetAllAsync(_agentId, _userB, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetMemories_ScopesTheCategoryQueryToTheCallingUser()
    {
        ActAs(_userB);
        _memoryRepository.GetByCategoryAsync(_agentId, MemoryCategories.Preference, _userB, Arg.Any<CancellationToken>())
            .Returns(new List<AgentMemory>());
        var handler = new GetAgentMemoriesQueryHandler(_memoryRepository, _embeddingService, _userService);

        await handler.Handle(
            new GetAgentMemoriesQuery { AgentId = _agentId, Category = MemoryCategories.Preference }, CancellationToken.None);

        await _memoryRepository.Received(1).GetByCategoryAsync(
            _agentId, MemoryCategories.Preference, _userB, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Create_PersonalCategory_StampsTheCallingUserAsOwner()
    {
        ActAs(_userB);
        var handler = new CreateAgentMemoryCommandHandler(_memoryRepository, _embeddingService, _userService);

        await handler.Handle(
            new CreateAgentMemoryCommand
            {
                AgentId = _agentId, Key = "k", Content = "c", Category = MemoryCategories.Preference
            },
            CancellationToken.None);

        await _memoryRepository.Received(1).AddAsync(
            Arg.Is<AgentMemory>(m => m.UserId == _userB), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Create_NonPersonalCategory_StaysSharedCompanyKnowledge()
    {
        ActAs(_userB, isAdmin: true);
        var handler = new CreateAgentMemoryCommandHandler(_memoryRepository, _embeddingService, _userService);

        await handler.Handle(
            new CreateAgentMemoryCommand
            {
                AgentId = _agentId, Key = "k", Content = "c", Category = MemoryCategories.Fact
            },
            CancellationToken.None);

        await _memoryRepository.Received(1).AddAsync(
            Arg.Is<AgentMemory>(m => m.UserId == null), Arg.Any<CancellationToken>());
    }
}
