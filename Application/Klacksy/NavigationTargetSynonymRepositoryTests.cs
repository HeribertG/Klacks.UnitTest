// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for NavigationTargetSynonymRepository: verifies ReplaceForTargetLanguage soft-deletes existing
/// entries and inserts fresh ones, and that HasActiveEntries returns the correct result.
/// </summary>
namespace Klacks.UnitTest.Application.Klacksy;

using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class NavigationTargetSynonymRepositoryTests
{
    private DataBaseContext _context = null!;
    private NavigationTargetSynonymRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var httpAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, httpAccessor);
        _context.Database.EnsureCreated();
        _repository = new NavigationTargetSynonymRepository(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Test]
    public async Task GetAllAsync_returns_only_active_entries()
    {
        await _context.NavigationTargetSynonyms.AddRangeAsync(new[]
        {
            new NavigationTargetSynonym { Id = Guid.NewGuid(), TargetId = "t1", Language = "de", Keyword = "test", IsDeleted = false },
            new NavigationTargetSynonym { Id = Guid.NewGuid(), TargetId = "t1", Language = "de", Keyword = "deleted", IsDeleted = true }
        });
        await _context.SaveChangesAsync();

        var result = await _repository.GetAllAsync();

        result.Count.ShouldBe(1);
        result[0].Keyword.ShouldBe("test");
    }

    [Test]
    public async Task ReplaceForTargetLanguage_soft_deletes_existing_and_adds_new()
    {
        var existingId = Guid.NewGuid();
        _context.NavigationTargetSynonyms.Add(new NavigationTargetSynonym
        {
            Id = existingId,
            TargetId = "absence",
            Language = "de",
            Keyword = "abwesenheit",
            IsDeleted = false
        });
        await _context.SaveChangesAsync();

        await _repository.ReplaceForTargetLanguageAsync("absence", "de", new[] { "fehlzeit", "urlaub" });

        var allWithDeleted = await _context.NavigationTargetSynonyms
            .IgnoreQueryFilters()
            .Where(s => s.TargetId == "absence" && s.Language == "de")
            .ToListAsync();

        var active = allWithDeleted.Where(s => !s.IsDeleted).ToList();
        var deleted = allWithDeleted.Where(s => s.IsDeleted).ToList();

        active.Count.ShouldBe(2);
        active.Select(s => s.Keyword).ShouldContain("fehlzeit");
        active.Select(s => s.Keyword).ShouldContain("urlaub");
        deleted.Count.ShouldBe(1);
        deleted[0].Id.ShouldBe(existingId);
    }

    [Test]
    public async Task ReplaceForTargetLanguage_with_empty_list_removes_all()
    {
        _context.NavigationTargetSynonyms.Add(new NavigationTargetSynonym
        {
            Id = Guid.NewGuid(), TargetId = "t1", Language = "fr", Keyword = "test", IsDeleted = false
        });
        await _context.SaveChangesAsync();

        await _repository.ReplaceForTargetLanguageAsync("t1", "fr", Array.Empty<string>());

        var active = await _repository.GetAllAsync();
        active.ShouldBeEmpty();
    }

    [Test]
    public async Task HasActiveEntriesForTargetLanguage_returns_true_when_entries_exist()
    {
        _context.NavigationTargetSynonyms.Add(new NavigationTargetSynonym
        {
            Id = Guid.NewGuid(), TargetId = "schedule", Language = "en", Keyword = "schedule", IsDeleted = false
        });
        await _context.SaveChangesAsync();

        var result = await _repository.HasActiveEntriesForTargetLanguageAsync("schedule", "en");
        result.ShouldBeTrue();
    }

    [Test]
    public async Task HasActiveEntriesForTargetLanguage_returns_false_when_all_soft_deleted()
    {
        _context.NavigationTargetSynonyms.Add(new NavigationTargetSynonym
        {
            Id = Guid.NewGuid(), TargetId = "schedule", Language = "en", Keyword = "schedule", IsDeleted = true
        });
        await _context.SaveChangesAsync();

        var result = await _repository.HasActiveEntriesForTargetLanguageAsync("schedule", "en");
        result.ShouldBeFalse();
    }

    [Test]
    public async Task GetByLanguagesAsync_returns_only_requested_languages()
    {
        await _context.NavigationTargetSynonyms.AddRangeAsync(new[]
        {
            new NavigationTargetSynonym { Id = Guid.NewGuid(), TargetId = "t1", Language = "de", Keyword = "test-de" },
            new NavigationTargetSynonym { Id = Guid.NewGuid(), TargetId = "t1", Language = "en", Keyword = "test-en" },
            new NavigationTargetSynonym { Id = Guid.NewGuid(), TargetId = "t1", Language = "fr", Keyword = "test-fr" }
        });
        await _context.SaveChangesAsync();

        var result = await _repository.GetByLanguagesAsync(new[] { "de", "fr" });

        result.Count.ShouldBe(2);
        result.Select(s => s.Language).ShouldContain("de");
        result.Select(s => s.Language).ShouldContain("fr");
        result.Select(s => s.Language).ShouldNotContain("en");
    }
}
