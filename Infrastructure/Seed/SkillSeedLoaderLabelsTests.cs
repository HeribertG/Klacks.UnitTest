// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Proves that a skill seed version bump merges the seeded labels into the stored ones instead of
/// replacing the whole dictionary. The seed file owns the four core languages only; every other language
/// key is written once by a language pack installation and is never re-created on startup, so a full
/// replacement would delete 21 languages' worth of authored, user-facing text on every reseed - and it
/// would do so silently, because a missing label suppresses the correction question rather than failing.
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
public class SkillSeedLoaderLabelsTests
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
        _contentRoot = Path.Combine(Path.GetTempPath(), "klacks-skill-labels-" + Guid.NewGuid().ToString("N"));
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
    public async Task VersionBumpReseed_PreservesLanguagePackLabels()
    {
        var existing = GivenExistingSkill(new Dictionary<string, string>
        {
            ["de"] = "alt",
            ["pl"] = "stary"
        });
        WriteSeedFile(version: 2, labelsJson: "\"labels\":{\"de\":\"neu\"},");

        await CreateLoader().LoadAsync();

        await _skillRepository.Received(1).UpdateAsync(existing, Arg.Any<CancellationToken>());
        Assert.That(existing.Version, Is.EqualTo(2), "the version bump must be applied");
        Assert.That(existing.Labels, Is.Not.Null);
        Assert.That(existing.Labels!["pl"], Is.EqualTo("stary"),
            "language pack labels must survive the reseed");
        Assert.That(existing.Labels["de"], Is.EqualTo("neu"),
            "the seed is the truth for its own languages");
    }

    [Test]
    public async Task VersionBumpReseed_RemovesCoreLanguageDroppedFromDefinition()
    {
        var existing = GivenExistingSkill(new Dictionary<string, string>
        {
            ["de"] = "alt",
            ["en"] = "Add an employee to a group",
            ["pl"] = "stary"
        });
        WriteSeedFile(version: 2, labelsJson: "\"labels\":{\"de\":\"neu\"},");

        await CreateLoader().LoadAsync();

        Assert.That(existing.Labels, Is.Not.Null);
        Assert.That(existing.Labels!.ContainsKey("en"), Is.False,
            "a core language dropped from the definition must not linger in the database");
        Assert.That(existing.Labels["de"], Is.EqualTo("neu"));
        Assert.That(existing.Labels["pl"], Is.EqualTo("stary"));
    }

    [Test]
    public async Task VersionBumpReseed_DefinitionWithoutLabels_KeepsNonCoreLanguages()
    {
        var existing = GivenExistingSkill(new Dictionary<string, string>
        {
            ["de"] = "alt",
            ["pl"] = "stary"
        });
        WriteSeedFile(version: 2, labelsJson: string.Empty);

        await CreateLoader().LoadAsync();

        Assert.That(existing.Labels, Is.Not.Null, "a definition without labels must not null the dictionary");
        Assert.That(existing.Labels!.Count, Is.EqualTo(1));
        Assert.That(existing.Labels.ContainsKey("de"), Is.False);
        Assert.That(existing.Labels["pl"], Is.EqualTo("stary"));
    }

    [Test]
    public async Task WithoutVersionBump_LabelsRemainUntouched()
    {
        var existing = GivenExistingSkill(new Dictionary<string, string>
        {
            ["de"] = "alt",
            ["pl"] = "stary"
        });
        WriteSeedFile(version: 1, labelsJson: "\"labels\":{\"de\":\"neu\"},");

        await CreateLoader().LoadAsync();

        await _skillRepository.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
        Assert.That(existing.Labels, Is.Not.Null);
        Assert.That(existing.Labels!["de"], Is.EqualTo("alt"));
        Assert.That(existing.Labels["pl"], Is.EqualTo("stary"));
    }

    private AgentSkill GivenExistingSkill(Dictionary<string, string> labels)
    {
        var existing = new AgentSkill
        {
            AgentId = _agent.Id,
            Name = SkillName,
            Description = "old description",
            Version = 1,
            Labels = labels
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
            Substitute.For<ISkillPhraseRepository>(),
            _featurePluginService,
            environment,
            NullLogger<SkillSeedLoader>.Instance);
    }

    private void WriteSeedFile(int version, string labelsJson)
    {
        var json =
            "{\"version\":1,\"skills\":[{" +
            $"\"name\":\"{SkillName}\"," +
            "\"description\":\"new description\"," +
            "\"category\":\"Crud\"," +
            "\"executionType\":\"Skill\"," +
            "\"isEnabled\":true," +
            labelsJson +
            $"\"version\":{version}" +
            "}]}";

        File.WriteAllText(_seedFilePath, json);
    }
}
