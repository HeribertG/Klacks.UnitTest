// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for MemoryRelationRepository: AddOrUpdateAsync stores an undirected edge canonically so
/// (A,B) and (B,A) never create two rows, and NeighboursOfAsync only returns active edges at or above
/// the confidence floor whose neighbour memory still exists (soft-deleted memories are excluded even
/// though the edge itself survives a soft delete). Uses a shared in-memory DataBaseContext.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

[TestFixture]
public class MemoryRelationRepositoryTests
{
    private DataBaseContext _context = null!;
    private MemoryRelationRepository _repository = null!;
    private Guid _agentId;
    private Guid _memoryA;
    private Guid _memoryB;
    private Guid _memoryC;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var httpAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, httpAccessor);
        _context.Database.EnsureCreated();
        _repository = new MemoryRelationRepository(_context);

        _agentId = Guid.NewGuid();
        _context.Agents.Add(new Agent { Id = _agentId, Name = "test-agent" });

        _memoryA = Guid.NewGuid();
        _memoryB = Guid.NewGuid();
        _memoryC = Guid.NewGuid();
        _context.AgentMemories.AddRange(
            new AgentMemory { Id = _memoryA, AgentId = _agentId, Key = "a", Content = "a" },
            new AgentMemory { Id = _memoryB, AgentId = _agentId, Key = "b", Content = "b" },
            new AgentMemory { Id = _memoryC, AgentId = _agentId, Key = "c", Content = "c" });
        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private MemoryRelation Relation(Guid memoryAId, Guid memoryBId, double confidence, MemoryRelationStatus status = MemoryRelationStatus.Active) => new()
    {
        Id = Guid.NewGuid(),
        AgentId = _agentId,
        MemoryAId = memoryAId,
        MemoryBId = memoryBId,
        Type = MemoryRelationType.Associative,
        Confidence = confidence,
        Provenance = "shared-tags",
        Status = status,
    };

    [Test]
    public async Task AddOrUpdateAsync_ReversedOrder_UpdatesSameCanonicalRow()
    {
        await _repository.AddOrUpdateAsync(Relation(_memoryA, _memoryB, 0.7));
        await _repository.AddOrUpdateAsync(Relation(_memoryB, _memoryA, 0.9));

        var edges = await _repository.GetByAgentAsync(_agentId);

        edges.Count.ShouldBe(1);
        edges[0].Confidence.ShouldBe(0.9);
    }

    [Test]
    public async Task NeighboursOfAsync_ReturnsOppositeSide_AboveConfidenceFloor()
    {
        await _repository.AddOrUpdateAsync(Relation(_memoryA, _memoryB, 0.8));

        var neighbours = await _repository.NeighboursOfAsync(_agentId, new[] { _memoryA }, minConfidence: 0.6, take: 5);

        neighbours.ShouldBe(new[] { _memoryB });
    }

    [Test]
    public async Task NeighboursOfAsync_ExcludesEdgeBelowConfidenceFloor()
    {
        await _repository.AddOrUpdateAsync(Relation(_memoryA, _memoryB, 0.5));

        var neighbours = await _repository.NeighboursOfAsync(_agentId, new[] { _memoryA }, minConfidence: 0.6, take: 5);

        neighbours.ShouldBeEmpty();
    }

    [Test]
    public async Task NeighboursOfAsync_ExcludesRetiredEdge()
    {
        await _repository.AddOrUpdateAsync(Relation(_memoryA, _memoryB, 0.9, MemoryRelationStatus.Retired));

        var neighbours = await _repository.NeighboursOfAsync(_agentId, new[] { _memoryA }, minConfidence: 0.6, take: 5);

        neighbours.ShouldBeEmpty();
    }

    [Test]
    public async Task NeighboursOfAsync_ExcludesSoftDeletedNeighbourMemory()
    {
        await _repository.AddOrUpdateAsync(Relation(_memoryA, _memoryB, 0.9));
        var memoryB = await _context.AgentMemories.FirstAsync(m => m.Id == _memoryB);
        memoryB.IsDeleted = true;
        await _context.SaveChangesAsync();

        var neighbours = await _repository.NeighboursOfAsync(_agentId, new[] { _memoryA }, minConfidence: 0.6, take: 5);

        neighbours.ShouldBeEmpty();
    }

    [Test]
    public async Task NeighboursOfAsync_RespectsTake()
    {
        await _repository.AddOrUpdateAsync(Relation(_memoryA, _memoryB, 0.9));
        await _repository.AddOrUpdateAsync(Relation(_memoryA, _memoryC, 0.8));

        var neighbours = await _repository.NeighboursOfAsync(_agentId, new[] { _memoryA }, minConfidence: 0.6, take: 1);

        neighbours.ShouldBe(new[] { _memoryB });
    }
}
