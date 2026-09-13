// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests that FindDuplicateAsync narrows its SQL candidate set before loading a row for
/// in-memory normalization: a system_import knowledge document or a long stored memory can never equal a
/// freshly extracted fact (capped at 512 output tokens by AutoMemoryExtractionService) and must be
/// excluded before materialization, or a dedupe check in the company-wide scope would load every seeded
/// knowledge document on every chat turn. Also pins that the match is scoped: a personal memory is only
/// ever compared against the same user's rows, and that an already expired row is still found - it is the
/// row whose expiry the caller has to push forward instead of writing a second copy.
/// </summary>

using Klacks.Api.Domain.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

[TestFixture]
public class AgentMemoryRepositoryDedupeCandidatesTests
{
    private DataBaseContext _context = null!;
    private AgentMemoryRepository _repository = null!;
    private Guid _agentId;

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
        _context.Agents.Add(new Agent { Id = _agentId, Name = "test-agent" });
        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Test]
    public async Task ASystemImportRowWithIdenticalContent_DoesNotCountAsDuplicate()
    {
        const string content = "This exact sentence should never be treated as a duplicate fact.";
        _context.AgentMemories.Add(new AgentMemory
        {
            Id = Guid.NewGuid(),
            AgentId = _agentId,
            UserId = null,
            Key = "knowledge-doc-key",
            Content = content,
            Category = MemoryCategories.Fact,
            Source = MemorySources.SystemImport
        });
        _context.SaveChanges();

        var result = await _repository.FindDuplicateAsync(
            _agentId, null, "unrelated-key", MessageNormalizer.Normalize(content));

        result.ShouldBeNull();
    }

    [Test]
    public async Task ALongStoredRow_DoesNotCountAsDuplicate()
    {
        var longContent = new string('a', AgentMemoryDedupeLimits.MaxCandidateContentLength + 1);
        _context.AgentMemories.Add(new AgentMemory
        {
            Id = Guid.NewGuid(),
            AgentId = _agentId,
            UserId = null,
            Key = "long-memory-key",
            Content = longContent,
            Category = MemoryCategories.Fact,
            Source = MemorySources.Conversation
        });
        _context.SaveChanges();

        var result = await _repository.FindDuplicateAsync(
            _agentId, null, "unrelated-key", MessageNormalizer.Normalize(longContent));

        result.ShouldBeNull();
    }

    [Test]
    public async Task AShortConversationRowWithEqualNormalizedContent_CountsAsDuplicate()
    {
        const string content = "Die Firma hat ihren Hauptsitz in Bern.";
        _context.AgentMemories.Add(new AgentMemory
        {
            Id = Guid.NewGuid(),
            AgentId = _agentId,
            UserId = null,
            Key = "Firmen_Standort",
            Content = content,
            Category = MemoryCategories.Fact,
            Source = MemorySources.Conversation
        });
        _context.SaveChanges();

        var result = await _repository.FindDuplicateAsync(
            _agentId, null, "unrelated-key", MessageNormalizer.Normalize(content));

        result.ShouldNotBeNull();
        result!.Key.ShouldBe("Firmen_Standort");
    }

    [Test]
    public async Task AShortConversationRowWithEqualNormalizedKey_CountsAsDuplicate()
    {
        const string storedKey = "  Firmen_Standort ";
        _context.AgentMemories.Add(new AgentMemory
        {
            Id = Guid.NewGuid(),
            AgentId = _agentId,
            UserId = null,
            Key = storedKey,
            Content = "Der Hauptsitz liegt in Bern.",
            Category = MemoryCategories.Fact,
            Source = MemorySources.Conversation
        });
        _context.SaveChanges();

        var result = await _repository.FindDuplicateAsync(
            _agentId,
            null,
            MessageNormalizer.Normalize("FIRMEN_STANDORT"),
            MessageNormalizer.Normalize("Etwas ganz anderes steht hier."));

        result.ShouldNotBeNull();
        result!.Key.ShouldBe(storedKey);
    }

    [Test]
    public async Task ARowOfAnotherUser_DoesNotCountAsDuplicate()
    {
        const string key = "Lieblingssport";
        const string content = "Der Benutzer mag Fussball.";
        _context.AgentMemories.Add(new AgentMemory
        {
            Id = Guid.NewGuid(),
            AgentId = _agentId,
            UserId = Guid.NewGuid(),
            Key = key,
            Content = content,
            Category = MemoryCategories.Preference,
            Source = MemorySources.Conversation
        });
        _context.SaveChanges();

        var result = await _repository.FindDuplicateAsync(
            _agentId,
            Guid.NewGuid(),
            MessageNormalizer.Normalize(key),
            MessageNormalizer.Normalize(content));

        result.ShouldBeNull();
    }

    [Test]
    public async Task AnExpiredRow_IsStillReturnedAsTheDuplicate()
    {
        const string content = "Der Benutzer plant gerade den Mai.";
        _context.AgentMemories.Add(new AgentMemory
        {
            Id = Guid.NewGuid(),
            AgentId = _agentId,
            UserId = null,
            Key = "Aktueller_Fokus",
            Content = content,
            Category = MemoryCategories.Context,
            Source = MemorySources.Conversation,
            ExpiresAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        _context.SaveChanges();

        var result = await _repository.FindDuplicateAsync(
            _agentId, null, "unrelated-key", MessageNormalizer.Normalize(content));

        result.ShouldNotBeNull();
        result!.ExpiresAt.ShouldBe(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }
}
