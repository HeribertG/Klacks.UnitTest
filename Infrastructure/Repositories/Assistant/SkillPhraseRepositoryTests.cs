// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the write side of SkillPhraseRepository. The decisive one is the origin isolation:
/// a replacement carrying one source must not remove the rows another source contributed for the very
/// same skill. navigation_target_synonyms has the same source column but replaces on
/// (TargetId, Language) only, which is how a single training call could rewrite every row as "user"
/// and permanently disable the seed refresh. The tests below pin that this table does not repeat it.
/// The single-row writers are pinned the same way: the learning card addresses phrases by id alone, so
/// without the Learned filter it could rewrite or reject a seed row that the seed loader owns. Only
/// TryUpdatePhraseTextAsync is covered here: SetStatusAsync writes through ExecuteUpdateAsync, which the
/// in-memory provider refuses outright, and reshaping the production write into a tracked load-and-save
/// just to make it testable would trade a single statement for a read plus a write.
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

    // Every seed version bump and every language pack install rewrites its slice. Removing and re-inserting
    // unchanged phrases left one soft-deleted tombstone per phrase and run, which is how the table grew more
    // deleted than active rows.
    [Test]
    public async Task ReplaceForLanguage_WithIdenticalListTwice_CreatesNoNewAndNoDeletedRows()
    {
        await ReplaceSeedSynonymsAsync(["gruppe", "team"]);
        var idsAfterFirst = (await AllRowsAsync()).Select(p => p.Id).ToList();

        await ReplaceSeedSynonymsAsync(["gruppe", "team"]);

        var all = await AllRowsAsync();
        all.Count.ShouldBe(2);
        all.ShouldAllBe(p => !p.IsDeleted);
        all.Select(p => p.Id).ShouldBe(idsAfterFirst, ignoreOrder: true);
    }

    [Test]
    public async Task ReplaceAllLanguages_WithIdenticalDictionaryTwice_CreatesNoNewAndNoDeletedRows()
    {
        var phrases = new Dictionary<string, List<string>> { ["de"] = ["eins", "zwei"], ["en"] = ["one"] };
        await _repository.ReplaceAllLanguagesAsync(
            SkillPhraseOwnerKinds.Skill, SkillName, SkillPhraseKinds.Synonym, SkillPhraseSources.Seed, phrases);
        var idsAfterFirst = (await AllRowsAsync()).Select(p => p.Id).ToList();

        await _repository.ReplaceAllLanguagesAsync(
            SkillPhraseOwnerKinds.Skill, SkillName, SkillPhraseKinds.Synonym, SkillPhraseSources.Seed, phrases);

        var all = await AllRowsAsync();
        all.Count.ShouldBe(3);
        all.ShouldAllBe(p => !p.IsDeleted);
        all.Select(p => p.Id).ShouldBe(idsAfterFirst, ignoreOrder: true);
    }

    [Test]
    public async Task ReplaceForLanguage_WithAPhraseRemoved_SoftDeletesOnlyThatPhrase()
    {
        await ReplaceSeedSynonymsAsync(["gruppe", "team", "mannschaft"]);
        var before = await AllRowsAsync();

        await ReplaceSeedSynonymsAsync(["gruppe", "mannschaft"]);

        var all = await AllRowsAsync();
        all.Count.ShouldBe(3);
        all.Where(p => p.IsDeleted).Select(p => p.Phrase).ShouldBe(["team"]);
        all.Where(p => !p.IsDeleted).Select(p => p.Id)
            .ShouldBe(before.Where(p => p.Phrase != "team").Select(p => p.Id), ignoreOrder: true);
    }

    [Test]
    public async Task ReplaceForLanguage_WithAPhraseAdded_InsertsOnlyThatPhrase()
    {
        await ReplaceSeedSynonymsAsync(["gruppe"]);
        var gruppeId = (await AllRowsAsync()).Single().Id;

        await ReplaceSeedSynonymsAsync(["gruppe", "team"]);

        var all = await AllRowsAsync();
        all.Count.ShouldBe(2);
        all.ShouldAllBe(p => !p.IsDeleted);
        all.Single(p => p.Phrase == "gruppe").Id.ShouldBe(gruppeId);
        all.Single(p => p.Phrase == "team").SortOrder.ShouldBe(1);
    }

    [Test]
    public async Task ReplaceForLanguage_WithReorderedList_UpdatesSortOrderInPlace()
    {
        await ReplaceSeedSynonymsAsync(["erste", "zweite", "dritte"]);
        var idsAfterFirst = (await AllRowsAsync()).ToDictionary(p => p.Phrase, p => p.Id);

        await ReplaceSeedSynonymsAsync(["dritte", "erste", "zweite"]);

        var all = await AllRowsAsync();
        all.Count.ShouldBe(3);
        all.ShouldAllBe(p => !p.IsDeleted);
        all.OrderBy(p => p.SortOrder).Select(p => p.Phrase).ShouldBe(["dritte", "erste", "zweite"]);
        all.ShouldAllBe(p => idsAfterFirst[p.Phrase] == p.Id);
    }

    [Test]
    public async Task ReplaceForLanguage_WithInListDuplicatesAndBlanks_StaysStableOnRepeat()
    {
        await ReplaceSeedSynonymsAsync(["gruppe", " ", "gruppe", "team"]);
        await ReplaceSeedSynonymsAsync(["gruppe", " ", "gruppe", "team"]);

        var all = await AllRowsAsync();
        all.Count.ShouldBe(2);
        all.ShouldAllBe(p => !p.IsDeleted);
        all.OrderBy(p => p.SortOrder).Select(p => p.SortOrder).ShouldBe([0, 3]);
    }

    [Test]
    public async Task ReplaceForLanguage_WithBlankKeywordsTwice_KeepsTheBlankRowsInPlace()
    {
        await _repository.ReplaceForLanguageAsync(
            SkillPhraseOwnerKinds.Skill, SkillName, SkillPhraseKinds.Keyword, SkillPhraseSources.Seed,
            SkillPhraseLanguages.Multiple, ["a", " ", "b"]);
        await _repository.ReplaceForLanguageAsync(
            SkillPhraseOwnerKinds.Skill, SkillName, SkillPhraseKinds.Keyword, SkillPhraseSources.Seed,
            SkillPhraseLanguages.Multiple, ["a", " ", "b"]);

        var all = await AllRowsAsync();
        all.Count.ShouldBe(3);
        all.ShouldAllBe(p => !p.IsDeleted);
    }

    // A rejected row still occupies the unique key, so a replacement that lists the same wording again must
    // neither add a second row next to it nor quietly turn the rejection back into an active phrase.
    [Test]
    public async Task ReplaceForLanguage_WithARejectedPhraseListedAgain_KeepsTheRejectedRowAndAddsNoTwin()
    {
        var rejectedId = await GivenPhraseWithIdAsync(SkillPhraseSources.Seed, "gruppe", SkillPhraseStatuses.Rejected);

        await ReplaceSeedSynonymsAsync(["gruppe", "team"]);

        var all = await AllRowsAsync();
        all.Count.ShouldBe(2);
        all.ShouldAllBe(p => !p.IsDeleted);
        var gruppe = all.Single(p => p.Phrase == "gruppe");
        gruppe.Id.ShouldBe(rejectedId);
        gruppe.Status.ShouldBe(SkillPhraseStatuses.Rejected);
    }

    [Test]
    public async Task ReplaceForLanguage_WithARejectedPhraseNoLongerListed_SoftDeletesIt()
    {
        await GivenPhraseWithIdAsync(SkillPhraseSources.Seed, "gruppe", SkillPhraseStatuses.Rejected);

        await ReplaceSeedSynonymsAsync(["team"]);

        var all = await AllRowsAsync();
        all.Single(p => p.Phrase == "gruppe").IsDeleted.ShouldBeTrue();
        all.Single(p => p.Phrase == "team").IsDeleted.ShouldBeFalse();
    }

    // With AllSourcesOfOwner the written list becomes the whole slice. A matching phrase another source
    // contributed is replaced by a fresh row of the writer, exactly as the former delete-and-insert did.
    [Test]
    public async Task ReplaceForLanguage_WithAllSourcesScope_ReplacesAMatchingForeignRowWithAnActiveWriterRow()
    {
        await GivenPhraseAsync("de", SkillPhraseSources.Seed, "gruppe");
        var seedId = (await AllRowsAsync()).Single().Id;

        await _repository.ReplaceForLanguageAsync(
            SkillPhraseOwnerKinds.Skill, SkillName, SkillPhraseKinds.Synonym, SkillPhraseSources.Admin,
            "de", ["gruppe"], SkillPhraseReplaceScope.AllSourcesOfOwner);

        var all = await AllRowsAsync();
        all.Single(p => p.Id == seedId).IsDeleted.ShouldBeTrue();
        var active = all.Single(p => !p.IsDeleted);
        active.Source.ShouldBe(SkillPhraseSources.Admin);
        active.Status.ShouldBe(SkillPhraseStatuses.Active);
    }

    // Taking a rejected Learned row over in place would leave an Admin row that is inactive and that the
    // learning card, scoped to Learned, can no longer reach. The administrator's wording must be active.
    [Test]
    public async Task ReplaceForLanguage_WithAllSourcesScope_ReplacesARejectedLearnedRowWithAnActiveAdminRow()
    {
        var learnedId = await GivenPhraseWithIdAsync(SkillPhraseSources.Learned, "gruppe", SkillPhraseStatuses.Rejected);

        await _repository.ReplaceForLanguageAsync(
            SkillPhraseOwnerKinds.Skill, SkillName, SkillPhraseKinds.Synonym, SkillPhraseSources.Admin,
            "de", ["gruppe"], SkillPhraseReplaceScope.AllSourcesOfOwner);

        var all = await AllRowsAsync();
        all.Single(p => p.Id == learnedId).IsDeleted.ShouldBeTrue();
        var active = all.Single(p => !p.IsDeleted);
        active.Source.ShouldBe(SkillPhraseSources.Admin);
        active.Status.ShouldBe(SkillPhraseStatuses.Active);
    }

    [Test]
    public async Task ReplaceForLanguage_TwiceWithIdenticalList_LeavesNothingTracked()
    {
        await ReplaceSeedSynonymsAsync(["gruppe", "team"]);
        await ReplaceSeedSynonymsAsync(["team", "gruppe", "neu"]);

        _context.ChangeTracker.Entries<SkillPhrase>().Count().ShouldBe(0);
    }

    [Test]
    public async Task TryUpdatePhraseText_OnALearnedPhrase_RewritesIt()
    {
        var id = await GivenPhraseWithIdAsync(SkillPhraseSources.Learned, "umsatz pro kunde");

        var updated = await _repository.TryUpdatePhraseTextAsync(id, "umsatzstatistik pro kunde");

        updated.ShouldBeTrue();
        var rows = await ActiveRowsAsync();
        rows.Single(p => p.Id == id).Phrase.ShouldBe("umsatzstatistik pro kunde");
    }

    // The learning card knows only the id. Without the source filter this call would overwrite a row the
    // seed loader owns, and the next startup would fight the card over the same text.
    [Test]
    public async Task TryUpdatePhraseText_OnASeedPhrase_ReportsNotFoundAndChangesNothing()
    {
        var id = await GivenPhraseWithIdAsync(SkillPhraseSources.Seed, "gruppe zuweisen");

        var updated = await _repository.TryUpdatePhraseTextAsync(id, "etwas anderes");

        updated.ShouldBeFalse();
        var rows = await ActiveRowsAsync();
        rows.Single(p => p.Id == id).Phrase.ShouldBe("gruppe zuweisen");
    }

    [Test]
    public async Task TryUpdatePhraseText_OnAnAdminPhrase_ReportsNotFoundAndChangesNothing()
    {
        var id = await GivenPhraseWithIdAsync(SkillPhraseSources.Admin, "handverlesen");

        var updated = await _repository.TryUpdatePhraseTextAsync(id, "etwas anderes");

        updated.ShouldBeFalse();
        var rows = await ActiveRowsAsync();
        rows.Single(p => p.Id == id).Phrase.ShouldBe("handverlesen");
    }

    // The update handler probes GetByIdAsync to decide which of the card's two id spaces an id belongs to.
    // An unfiltered read would claim a seed id for the phrase branch, where the rejected write then reads
    // as a wording conflict instead of the 404 the card expects.
    [Test]
    public async Task GetById_OnALearnedPhrase_ReturnsIt()
    {
        var id = await GivenPhraseWithIdAsync(SkillPhraseSources.Learned, "umsatz pro kunde");

        var found = await _repository.GetByIdAsync(id);

        found.ShouldNotBeNull();
        found!.Phrase.ShouldBe("umsatz pro kunde");
    }

    [Test]
    [TestCase(SkillPhraseSources.Seed)]
    [TestCase(SkillPhraseSources.LanguagePack)]
    [TestCase(SkillPhraseSources.Admin)]
    public async Task GetById_OnAPhraseOfAnotherSource_ReturnsNull(string source)
    {
        var id = await GivenPhraseWithIdAsync(source, "gruppe zuweisen");

        (await _repository.GetByIdAsync(id)).ShouldBeNull();
    }

    private Task ReplaceSeedSynonymsAsync(IReadOnlyList<string> phrases) =>
        _repository.ReplaceForLanguageAsync(
            SkillPhraseOwnerKinds.Skill, SkillName, SkillPhraseKinds.Synonym, SkillPhraseSources.Seed, "de", phrases);

    private async Task<List<SkillPhrase>> AllRowsAsync()
    {
        return await _context.SkillPhrases.IgnoreQueryFilters().AsNoTracking().ToListAsync();
    }

    private async Task<Guid> GivenPhraseWithIdAsync(
        string source, string phrase, string status = SkillPhraseStatuses.Active)
    {
        var id = Guid.NewGuid();
        _context.SkillPhrases.Add(new SkillPhrase
        {
            Id = id,
            OwnerKind = SkillPhraseOwnerKinds.Skill,
            OwnerName = SkillName,
            Language = "de",
            Kind = SkillPhraseKinds.Synonym,
            Phrase = phrase,
            SortOrder = 0,
            Source = source,
            Status = status
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return id;
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
