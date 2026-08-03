// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Proves that a skill seed version bump maintains both phrase stores at once: the legacy jsonb
/// dictionary keeps merging the installed pack languages, and skill_phrase - the store the knowledge
/// index builds from - receives the same picture, with the pack rows surviving because the seed only
/// replaces rows of its own origin.
/// </summary>

using Klacks.Api.Application.DTOs.Plugins;
using Klacks.Api.Application.Interfaces.Plugins;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Persistence.Seed;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Seed;

[TestFixture]
public class SkillSeedLoaderPhraseTests
{
    private const string SkillName = "add_employee_to_group";
    private const string PackLanguage = "pl";
    private const string PackPhrase = "dodaj pracownika";

    private string _contentRoot = null!;
    private string _seedFilePath = null!;
    private DataBaseContext _context = null!;
    private SkillPhraseRepository _phraseRepository = null!;
    private IAgentSkillRepository _skillRepository = null!;
    private IAgentRepository _agentRepository = null!;
    private IFeaturePluginService _featurePluginService = null!;
    private Agent _agent = null!;

    [SetUp]
    public void Setup()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "klacks-skill-phrase-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_contentRoot, "Application", "Skills", "Definitions"));
        _seedFilePath = Path.Combine(_contentRoot, "Application", "Skills", "Definitions", "skill-seeds.json");

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _context.Database.EnsureCreated();
        _phraseRepository = new SkillPhraseRepository(_context);

        _agent = new Agent { Id = Guid.NewGuid(), Name = "klacks-default", IsDefault = true };
        _skillRepository = Substitute.For<IAgentSkillRepository>();
        _agentRepository = Substitute.For<IAgentRepository>();
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(_agent);
        _featurePluginService = Substitute.For<IFeaturePluginService>();
        _featurePluginService.GetAllPluginsAsync().Returns(new List<FeaturePluginInfo>());
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();

        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
    }

    [Test]
    public async Task VersionBump_KeepsPackLanguageInBothStores_AndRewritesTheSeededOnes()
    {
        var existing = GivenStoredSkill();
        await GivenPhraseAsync(PackLanguage, SkillPhraseSources.LanguagePack, PackPhrase);
        await GivenPhraseAsync("de", SkillPhraseSources.Seed, "alte saat");

        WriteSeedFile(version: 2, synonymsJson: "\"synonyms\":{\"de\":[\"neue saat\"],\"en\":[\"new seed\"]},");

        await CreateLoader().LoadAsync();

        existing.Synonyms.ShouldNotBeNull();
        existing.Synonyms!["de"].ShouldBe(["neue saat"]);
        existing.Synonyms["en"].ShouldBe(["new seed"]);
        existing.Synonyms[PackLanguage].ShouldBe([PackPhrase]);

        var rows = await _context.SkillPhrases.AsNoTracking().ToListAsync();
        var synonyms = rows.Where(p => p.Kind == SkillPhraseKinds.Synonym).ToList();

        synonyms.Where(p => p.Source == SkillPhraseSources.LanguagePack)
            .Select(p => (p.Language, p.Phrase))
            .ShouldBe([(PackLanguage, PackPhrase)]);
        synonyms.Where(p => p.Source == SkillPhraseSources.Seed)
            .Select(p => (p.Language, p.Phrase))
            .ShouldBe([("de", "neue saat"), ("en", "new seed")], ignoreOrder: true);
    }

    [Test]
    public async Task VersionBump_LegacyFlatKeywordArray_LandsInTheUndeterminedGroup()
    {
        GivenStoredSkill();
        WriteSeedFile(version: 2, synonymsJson: "\"triggerKeywords\":[\"gruppe\",\"team\",\"abteilung\"],");

        await CreateLoader().LoadAsync();

        var keywords = await _context.SkillPhrases.AsNoTracking()
            .Where(p => p.Kind == SkillPhraseKinds.Keyword)
            .OrderBy(p => p.SortOrder)
            .ToListAsync();

        keywords.Select(p => p.Phrase).ShouldBe(["gruppe", "team", "abteilung"]);
        keywords.Select(p => p.SortOrder).ShouldBe([0, 1, 2]);
        keywords.ShouldAllBe(p =>
            p.Source == SkillPhraseSources.Seed && p.Language == SkillPhraseLanguages.Undetermined);
    }

    [Test]
    public async Task VersionBump_GroupedKeywords_StoreOneLanguagePerGroupAndKeepReservedTagsVerbatim()
    {
        GivenStoredSkill();
        WriteSeedFile(
            version: 2,
            synonymsJson: "\"triggerKeywords\":{\"de\":[\"gruppe\",\"team\"],\"en\":[\"group\"],\"mul\":[\"smtp\"],\"und\":[\"cron\"]},");

        await CreateLoader().LoadAsync();

        var keywords = await _context.SkillPhrases.AsNoTracking()
            .Where(p => p.Kind == SkillPhraseKinds.Keyword)
            .ToListAsync();

        // "mul" is stored verbatim, not folded into null or an empty string. It is the only tag the
        // keyword matcher accepts as an anchor, and mapping it away made it unrepresentable in this
        // table - which is what would have silently dropped every anchor phrase had the matcher been
        // migrated onto it.
        keywords.Select(p => (p.Language, p.Phrase))
            .ShouldBe(
                [(SkillPhraseLanguages.Multiple, "smtp"), ("de", "gruppe"), ("de", "team"),
                 ("en", "group"), (SkillPhraseLanguages.Undetermined, "cron")],
                ignoreOrder: true);
        keywords.ShouldAllBe(p => p.Source == SkillPhraseSources.Seed);
    }

    [Test]
    public async Task SkippedSkill_WritesNothing()
    {
        GivenStoredSkill();
        await GivenPhraseAsync("de", SkillPhraseSources.Seed, "alte saat");

        WriteSeedFile(version: 1, synonymsJson: "\"synonyms\":{\"de\":[\"neue saat\"]},");

        await CreateLoader().LoadAsync();

        var rows = await _context.SkillPhrases.AsNoTracking().ToListAsync();
        rows.Select(p => p.Phrase).ShouldBe(["alte saat"]);
    }

    private AgentSkill GivenStoredSkill()
    {
        var existing = new AgentSkill
        {
            Id = Guid.NewGuid(),
            AgentId = _agent.Id,
            Name = SkillName,
            Description = "old description",
            Version = 1,
            Synonyms = new Dictionary<string, List<string>>
            {
                ["de"] = ["alte saat"],
                [PackLanguage] = [PackPhrase]
            }
        };

        _skillRepository.GetAllByAgentIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill> { existing });

        return existing;
    }

    private async Task GivenPhraseAsync(string? language, string source, string phrase)
    {
        _context.SkillPhrases.Add(new SkillPhrase
        {
            Id = Guid.NewGuid(),
            OwnerKind = SkillPhraseOwnerKinds.Skill,
            OwnerName = SkillName,
            Language = language,
            Kind = SkillPhraseKinds.Synonym,
            Phrase = phrase,
            Source = source,
            Status = SkillPhraseStatuses.Active
        });

        await _context.SaveChangesAsync();
    }

    private SkillSeedLoader CreateLoader()
    {
        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(_contentRoot);

        return new SkillSeedLoader(
            _skillRepository,
            _agentRepository,
            _phraseRepository,
            _featurePluginService,
            environment,
            NullLogger<SkillSeedLoader>.Instance);
    }

    private void WriteSeedFile(int version, string synonymsJson)
    {
        var json =
            "{\"version\":1,\"skills\":[{" +
            $"\"name\":\"{SkillName}\"," +
            "\"description\":\"new description\"," +
            "\"category\":\"Crud\"," +
            "\"executionType\":\"Skill\"," +
            "\"isEnabled\":true," +
            synonymsJson +
            $"\"version\":{version}" +
            "}]}";

        File.WriteAllText(_seedFilePath, json);
    }
}
