// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the write side of SkillPhraseRepository. The decisive one is the origin isolation:
/// a replacement carrying one source must not remove the rows another source contributed for the very
/// same skill. navigation_target_synonyms has the same source column but replaces on
/// (TargetId, Language) only, which is how a single training call could rewrite every row as "user"
/// and permanently disable the seed refresh. The tests below pin that this table does not repeat it.
/// </summary>
namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SkillPhraseRepositoryTests
{
    private const string SkillName = "add_employee_to_group";

    private DataBaseContext _context = null!;
    private SkillPhraseRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _context.Database.EnsureCreated();
        _repository = new SkillPhraseRepository(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Test]
    public async Task ReplaceAllLanguages_WithSeedSource_LeavesLanguagePackAndAdminRowsUntouched()
    {
        await GivenPhraseAsync("de", SkillPhraseSources.Seed, "alte saat");
        await GivenPhraseAsync("pl", SkillPhraseSources.LanguagePack, "dodaj pracownika");
        await GivenPhraseAsync("de", SkillPhraseSources.Admin, "handverlesen");

        await _repository.ReplaceAllLanguagesAsync(
            SkillPhraseOwnerKinds.Skill,
            SkillName,
            SkillPhraseKinds.Synonym,
            SkillPhraseSources.Seed,
            new Dictionary<string, List<string>> { ["de"] = ["neue saat"] });

        var rows = await ActiveRowsAsync();

        rows.Where(p => p.Source == SkillPhraseSources.Seed).Select(p => p.Phrase).ShouldBe(["neue saat"]);
        rows.Where(p => p.Source == SkillPhraseSources.LanguagePack).Select(p => p.Phrase).ShouldBe(["dodaj pracownika"]);
        rows.Where(p => p.Source == SkillPhraseSources.Admin).Select(p => p.Phrase).ShouldBe(["handverlesen"]);
    }

    [Test]
    public async Task ReplaceForLanguage_WithLanguagePackSource_LeavesSeedAndAdminRowsUntouched()
    {
        await GivenPhraseAsync("de", SkillPhraseSources.Seed, "gruppe zuweisen");
        await GivenPhraseAsync("pl", SkillPhraseSources.LanguagePack, "dodaj pracownika");
        await GivenPhraseAsync("pl", SkillPhraseSources.Admin, "recznie dodane");

        await _repository.ReplaceForLanguageAsync(
            SkillPhraseOwnerKinds.Skill,
            SkillName,
            SkillPhraseKinds.Synonym,
            SkillPhraseSources.LanguagePack,
            "pl",
            ["przypisz do grupy"]);

        var rows = await ActiveRowsAsync();

        rows.Where(p => p.Source == SkillPhraseSources.Seed).Select(p => p.Phrase).ShouldBe(["gruppe zuweisen"]);
        rows.Where(p => p.Source == SkillPhraseSources.LanguagePack).Select(p => p.Phrase).ShouldBe(["przypisz do grupy"]);
        rows.Where(p => p.Source == SkillPhraseSources.Admin).Select(p => p.Phrase).ShouldBe(["recznie dodane"]);
    }

    [Test]
    public async Task ReplaceForLanguage_WithEmptyList_RemovesOnlyThatLanguageAndSource()
    {
        await GivenPhraseAsync("pl", SkillPhraseSources.LanguagePack, "dodaj pracownika");
        await GivenPhraseAsync("es", SkillPhraseSources.LanguagePack, "anadir empleado");
        await GivenPhraseAsync("pl", SkillPhraseSources.Seed, "saat polnisch");

        await _repository.ReplaceForLanguageAsync(
            SkillPhraseOwnerKinds.Skill,
            SkillName,
            SkillPhraseKinds.Synonym,
            SkillPhraseSources.LanguagePack,
            "pl",
            []);

        var rows = await ActiveRowsAsync();

        rows.Select(p => p.Phrase).ShouldBe(["anadir empleado", "saat polnisch"], ignoreOrder: true);
    }

    [Test]
    public async Task ReplaceForLanguage_DoesNotTouchOtherOwners()
    {
        await GivenPhraseAsync("de", SkillPhraseSources.Seed, "fremd", ownerName: "other_skill");
        await GivenPhraseAsync("de", SkillPhraseSources.Seed, "eigen");

        await _repository.ReplaceForLanguageAsync(
            SkillPhraseOwnerKinds.Skill,
            SkillName,
            SkillPhraseKinds.Synonym,
            SkillPhraseSources.Seed,
            "de",
            []);

        var rows = await ActiveRowsAsync();

        rows.Select(p => p.Phrase).ShouldBe(["fremd"]);
    }

    [Test]
    public async Task ReplaceForLanguage_KeywordRowsAreAddressedByTheirReservedTag()
    {
        await GivenPhraseAsync(SkillPhraseLanguages.Multiple, SkillPhraseSources.Seed, "gruppe", kind: SkillPhraseKinds.Keyword);
        await GivenPhraseAsync("de", SkillPhraseSources.Seed, "gruppe");

        await _repository.ReplaceForLanguageAsync(
            SkillPhraseOwnerKinds.Skill,
            SkillName,
            SkillPhraseKinds.Keyword,
            SkillPhraseSources.Seed,
            SkillPhraseLanguages.Multiple,
            ["team"]);

        var rows = await ActiveRowsAsync();

        rows.Where(p => p.Kind == SkillPhraseKinds.Keyword).Select(p => p.Phrase).ShouldBe(["team"]);
        rows.Where(p => p.Kind == SkillPhraseKinds.Synonym).Select(p => p.Phrase).ShouldBe(["gruppe"]);
    }

    [Test]
    public async Task ReplaceForLanguage_WithAllSourcesScope_RemovesForeignSourcesToo()
    {
        await GivenPhraseAsync(SkillPhraseLanguages.Multiple, SkillPhraseSources.Seed, "gruppe", kind: SkillPhraseKinds.Keyword);
        await GivenPhraseAsync(SkillPhraseLanguages.Multiple, SkillPhraseSources.LanguagePack, "grupa", kind: SkillPhraseKinds.Keyword);

        await _repository.ReplaceForLanguageAsync(
            SkillPhraseOwnerKinds.Skill,
            SkillName,
            SkillPhraseKinds.Keyword,
            SkillPhraseSources.Admin,
            SkillPhraseLanguages.Multiple,
            ["team"],
            SkillPhraseReplaceScope.AllSourcesOfOwner);

        var rows = await ActiveRowsAsync();

        rows.Select(p => p.Phrase).ShouldBe(["team"]);
        rows.Single().Source.ShouldBe(SkillPhraseSources.Admin);
    }

    [Test]
    public async Task ReplaceForLanguage_SortOrderFollowsListPosition()
    {
        await _repository.ReplaceForLanguageAsync(
            SkillPhraseOwnerKinds.Skill,
            SkillName,
            SkillPhraseKinds.Keyword,
            SkillPhraseSources.Seed,
            null,
            ["erste", "zweite", "dritte"]);

        var rows = await ActiveRowsAsync();

        rows.OrderBy(p => p.SortOrder).Select(p => p.Phrase).ShouldBe(["erste", "zweite", "dritte"]);
        rows.OrderBy(p => p.SortOrder).Select(p => p.SortOrder).ShouldBe([0, 1, 2]);
    }

    [Test]
    public async Task ReplaceAllLanguages_SortOrderRestartsPerLanguage()
    {
        await _repository.ReplaceAllLanguagesAsync(
            SkillPhraseOwnerKinds.Skill,
            SkillName,
            SkillPhraseKinds.Synonym,
            SkillPhraseSources.Seed,
            new Dictionary<string, List<string>>
            {
                ["de"] = ["eins", "zwei"],
                ["en"] = ["one", "two"]
            });

        var rows = await ActiveRowsAsync();

        rows.Where(p => p.Language == "de").OrderBy(p => p.SortOrder).Select(p => p.Phrase).ShouldBe(["eins", "zwei"]);
        rows.Where(p => p.Language == "en").OrderBy(p => p.SortOrder).Select(p => p.Phrase).ShouldBe(["one", "two"]);
        rows.Where(p => p.Language == "en").Select(p => p.SortOrder).ShouldBe([0, 1], ignoreOrder: true);
    }

    [Test]
    public async Task ReplaceForLanguage_KeepsBlankKeywordsButDropsBlankSynonyms()
    {
        await _repository.ReplaceForLanguageAsync(
            SkillPhraseOwnerKinds.Skill,
            SkillName,
            SkillPhraseKinds.Keyword,
            SkillPhraseSources.Seed,
            null,
            ["a", " ", "b"]);

        await _repository.ReplaceForLanguageAsync(
            SkillPhraseOwnerKinds.Skill,
            SkillName,
            SkillPhraseKinds.Synonym,
            SkillPhraseSources.Seed,
            "de",
            ["a", " ", "b"]);

        var rows = await ActiveRowsAsync();

        rows.Count(p => p.Kind == SkillPhraseKinds.Keyword).ShouldBe(3);
        rows.Where(p => p.Kind == SkillPhraseKinds.Synonym).OrderBy(p => p.SortOrder).Select(p => p.Phrase).ShouldBe(["a", "b"]);
        rows.Where(p => p.Kind == SkillPhraseKinds.Synonym).OrderBy(p => p.SortOrder).Select(p => p.SortOrder).ShouldBe([0, 2]);
    }

    [Test]
    public async Task ReplaceForLanguage_RewritingIdenticalPhrasesLeavesOneActiveRowEach()
    {
        await _repository.ReplaceForLanguageAsync(
            SkillPhraseOwnerKinds.Skill,
            SkillName,
            SkillPhraseKinds.Synonym,
            SkillPhraseSources.Seed,
            "de",
            ["gruppe", "team"]);

        await _repository.ReplaceForLanguageAsync(
            SkillPhraseOwnerKinds.Skill,
            SkillName,
            SkillPhraseKinds.Synonym,
            SkillPhraseSources.Seed,
            "de",
            ["gruppe", "team"]);

        var rows = await ActiveRowsAsync();

        rows.Select(p => p.Phrase).ShouldBe(["gruppe", "team"], ignoreOrder: true);
    }

    private async Task GivenPhraseAsync(
        string? language,
        string source,
        string phrase,
        string kind = SkillPhraseKinds.Synonym,
        string ownerName = SkillName)
    {
        _context.SkillPhrases.Add(new SkillPhrase
        {
            Id = Guid.NewGuid(),
            OwnerKind = SkillPhraseOwnerKinds.Skill,
            OwnerName = ownerName,
            Language = language,
            Kind = kind,
            Phrase = phrase,
            SortOrder = 0,
            Source = source,
            Status = SkillPhraseStatuses.Active
        });

        await _context.SaveChangesAsync();
    }

    private async Task<List<SkillPhrase>> ActiveRowsAsync()
    {
        return await _context.SkillPhrases.AsNoTracking().ToListAsync();
    }
}
