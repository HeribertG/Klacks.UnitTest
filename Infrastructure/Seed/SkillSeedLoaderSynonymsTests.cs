// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Proves that a skill seed version bump merges the seeded synonyms into the stored ones instead of
/// replacing the whole dictionary: the seed file only ships the core languages, every other language key
/// is written once by a language plugin installation and is never re-created on startup, so a full
/// replacement would delete the installed multi-language coverage on every reseed.
/// </summary>

using Klacks.Api.Application.DTOs.Plugins;
using Klacks.Api.Application.Interfaces.Plugins;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Seed;

[TestFixture]
public class SkillSeedLoaderSynonymsTests
{
    private const string SkillName = "add_employee_to_group";

    private string _contentRoot = null!;
    private string _seedFilePath = null!;
    private IAgentSkillRepository _skillRepository = null!;
    private IAgentRepository _agentRepository = null!;
    private IFeaturePluginService _featurePluginService = null!;
    private Agent _agent = null!;

    [SetUp]
    public void Setup()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "klacks-skill-seed-" + Guid.NewGuid().ToString("N"));
        var definitionsDir = Path.Combine(_contentRoot, "Application", "Skills", "Definitions");
        Directory.CreateDirectory(definitionsDir);
        _seedFilePath = Path.Combine(definitionsDir, "skill-seeds.json");

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
        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
    }

    [Test]
    public async Task VersionBumpReseed_PreservesLanguagePluginSynonyms()
    {
        var existing = GivenExistingSkill(new Dictionary<string, List<string>>
        {
            ["de"] = ["mitarbeiter der gruppe hinzufuegen"],
            ["pl"] = ["dodaj pracownika do grupy"]
        });
        WriteSeedFile(version: 2, synonymsJson: "\"synonyms\":{\"de\":[\"mitarbeiter zuweisen\"]},");

        await CreateLoader().LoadAsync();

        await _skillRepository.Received(1).UpdateAsync(existing, Arg.Any<CancellationToken>());
        Assert.That(existing.Version, Is.EqualTo(2), "the version bump must be applied");
        Assert.That(existing.Synonyms, Is.Not.Null);
        Assert.That(existing.Synonyms!["pl"], Is.EqualTo(new List<string> { "dodaj pracownika do grupy" }),
            "language plugin synonyms must survive the reseed");
        Assert.That(existing.Synonyms["de"], Is.EqualTo(new List<string> { "mitarbeiter zuweisen" }),
            "the seed is the truth for its own languages");
    }

    [Test]
    public async Task VersionBumpReseed_RemovesCoreLanguageDroppedFromDefinition()
    {
        var existing = GivenExistingSkill(new Dictionary<string, List<string>>
        {
            ["de"] = ["alt"],
            ["en"] = ["add employee to group"],
            ["pl"] = ["dodaj pracownika do grupy"]
        });
        WriteSeedFile(version: 2, synonymsJson: "\"synonyms\":{\"de\":[\"neu\"]},");

        await CreateLoader().LoadAsync();

        Assert.That(existing.Synonyms, Is.Not.Null);
        Assert.That(existing.Synonyms!.ContainsKey("en"), Is.False,
            "a core language dropped from the definition must not linger in the database");
        Assert.That(existing.Synonyms["de"], Is.EqualTo(new List<string> { "neu" }));
        Assert.That(existing.Synonyms["pl"], Is.EqualTo(new List<string> { "dodaj pracownika do grupy" }));
    }

    [Test]
    public async Task VersionBumpReseed_DefinitionWithoutSynonyms_KeepsNonCoreLanguages()
    {
        var existing = GivenExistingSkill(new Dictionary<string, List<string>>
        {
            ["de"] = ["alt"],
            ["pl"] = ["dodaj pracownika do grupy"]
        });
        WriteSeedFile(version: 2, synonymsJson: string.Empty);

        await CreateLoader().LoadAsync();

        Assert.That(existing.Synonyms, Is.Not.Null, "a definition without synonyms must not null the dictionary");
        Assert.That(existing.Synonyms!.Count, Is.EqualTo(1));
        Assert.That(existing.Synonyms.ContainsKey("de"), Is.False);
        Assert.That(existing.Synonyms["pl"], Is.EqualTo(new List<string> { "dodaj pracownika do grupy" }));
    }

    [Test]
    public async Task WithoutVersionBump_SynonymsRemainUntouched()
    {
        var existing = GivenExistingSkill(new Dictionary<string, List<string>>
        {
            ["de"] = ["alt"],
            ["pl"] = ["dodaj pracownika do grupy"]
        });
        WriteSeedFile(version: 1, synonymsJson: "\"synonyms\":{\"de\":[\"neu\"]},");

        await CreateLoader().LoadAsync();

        await _skillRepository.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
        Assert.That(existing.Synonyms, Is.Not.Null);
        Assert.That(existing.Synonyms!["de"], Is.EqualTo(new List<string> { "alt" }));
        Assert.That(existing.Synonyms["pl"], Is.EqualTo(new List<string> { "dodaj pracownika do grupy" }));
    }

    private AgentSkill GivenExistingSkill(Dictionary<string, List<string>> synonyms)
    {
        var existing = new AgentSkill
        {
            AgentId = _agent.Id,
            Name = SkillName,
            Description = "old description",
            Version = 1,
            Synonyms = synonyms
        };

        _skillRepository.GetAllByAgentIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill> { existing });

        return existing;
    }

    private SkillSeedLoader CreateLoader()
    {
        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(_contentRoot);

        return new SkillSeedLoader(
            _skillRepository,
            _agentRepository,
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
