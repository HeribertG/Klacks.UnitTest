// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests that the agent memory queries only return what the calling user may see: shared memories
/// (UserId null) are visible to everyone, a personal memory only to its owner. Before this scope the
/// list and category queries filtered on the agent alone, and because Klacks runs a single default
/// agent every user read every other user's personal memories. SearchAsync is not covered here: it
/// uses EF.Functions.ILike, which the in-memory provider cannot translate.
/// </summary>

using Klacks.Api.Domain.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

[TestFixture]
public class AgentMemoryRepositoryUserScopeTests
{
    private DataBaseContext _context = null!;
    private AgentMemoryRepository _repository = null!;
    private Guid _agentId;
    private Guid _userA;
    private Guid _userB;

    private const string SharedKey = "company-wide";
    private const string PersonalOfAKey = "personal-of-a";
    private const string PersonalOfBKey = "personal-of-b";

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _context.Database.EnsureCreated();
        _repository = new AgentMemoryRepository(_context, NullLogger<AgentMemoryRepository>.Instance);

        _agentId = Guid.NewGuid();
        _userA = Guid.NewGuid();
        _userB = Guid.NewGuid();

        _context.Agents.Add(new Agent { Id = _agentId, Name = "test-agent" });
        _context.AgentMemories.AddRange(
            new AgentMemory
            {
                Id = Guid.NewGuid(), AgentId = _agentId, UserId = null,
                Key = SharedKey, Content = "everyone may read this", Category = MemoryCategories.Fact
            },
            new AgentMemory
            {
                Id = Guid.NewGuid(), AgentId = _agentId, UserId = _userA,
                Key = PersonalOfAKey, Content = "only user A may read this", Category = MemoryCategories.Preference
            },
            new AgentMemory
            {
                Id = Guid.NewGuid(), AgentId = _agentId, UserId = _userB,
                Key = PersonalOfBKey, Content = "only user B may read this", Category = MemoryCategories.Preference
            });
        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Test]
    public async Task GetAllAsync_UserB_DoesNotSeeThePersonalMemoryOfUserA()
    {
        var result = await _repository.GetAllAsync(_agentId, _userB);

        result.Select(m => m.Key).ShouldNotContain(PersonalOfAKey);
    }

    [Test]
    public async Task GetAllAsync_UserB_SeesTheSharedMemory()
    {
        var result = await _repository.GetAllAsync(_agentId, _userB);

        result.Select(m => m.Key).ShouldContain(SharedKey);
    }

    [Test]
    public async Task GetAllAsync_UserB_SeesTheirOwnPersonalMemory()
    {
        var result = await _repository.GetAllAsync(_agentId, _userB);

        result.Select(m => m.Key).ShouldContain(PersonalOfBKey);
    }

    [Test]
    public async Task GetAllAsync_WithoutAUser_ReturnsOnlySharedMemories()
    {
        var result = await _repository.GetAllAsync(_agentId);

        result.Select(m => m.Key).ShouldBe(new[] { SharedKey });
    }

    [Test]
    public async Task GetByCategoryAsync_UserB_DoesNotSeeThePersonalMemoryOfUserA()
    {
        var result = await _repository.GetByCategoryAsync(_agentId, MemoryCategories.Preference, _userB);

        result.Select(m => m.Key).ShouldBe(new[] { PersonalOfBKey });
    }

    [Test]
    public async Task GetByCategoryAsync_UserB_SeesTheSharedMemoryOfThatCategory()
    {
        var result = await _repository.GetByCategoryAsync(_agentId, MemoryCategories.Fact, _userB);

        result.Select(m => m.Key).ShouldBe(new[] { SharedKey });
    }
}
