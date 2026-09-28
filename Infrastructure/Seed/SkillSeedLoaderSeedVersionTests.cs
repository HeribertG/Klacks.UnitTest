// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the seed-version rule. The loader compares the seed file with SeedVersion, which only it writes, so a
/// Version the learning loop or an administrator raised can no longer hide a newer curated or exported
/// definition. A row the loader never wrote under this rule is adopted once: behind its seed it is applied in
/// full as before; otherwise only its description and version columns follow the seed, so an administrator's
/// switched-off skill stays switched off.
/// </summary>
namespace Klacks.UnitTest.Infrastructure.Seed;

using Klacks.Api.Application.DTOs.Plugins;
using Klacks.Api.Application.Interfaces.Plugins;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SkillSeedLoaderSeedVersionTests
{
    private const string SkillName = "add_employee_to_group";
    private const string SeedDescription = "new description";
    private const string LocalDescription = "learned description";
    private const string LocalKeywords = "{\"de\":[\"lokal\"]}";

    private string _contentRoot = null!;
    private string _seedFilePath = null!;
    private IAgentSkillRepository _skillRepository = null!;
    private ISkillPhraseRepository _phraseRepository = null!;
    private IAgentRepository _agentRepository = null!;
    private IFeaturePluginService _featurePluginService = null!;
    private Agent _agent = null!;

    [SetUp]
    public void Setup()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "klacks-seed-version-" + Guid.NewGuid().ToString("N"));
        var definitionsDir = Path.Combine(_contentRoot, "Application", "Skills", "Definitions");
        Directory.CreateDirectory(definitionsDir);
        _seedFilePath = Path.Combine(definitionsDir, "skill-seeds.json");

        _agent = new Agent { Id = Guid.NewGuid(), Name = "klacks-default", IsDefault = true };
        _skillRepository = Substitute.For<IAgentSkillRepository>();
        _phraseRepository = Substitute.For<ISkillPhraseRepository>();
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
    public async Task ASeedNewerThanTheSeedVersion_IsAppliedEvenWhenTheLearnerRaisedTheVersionAboveIt()
    {
        var existing = GivenStoredSkill(version: 7, seedVersion: 4);
        WriteSeedFile(version: 5);

        await CreateLoader().LoadAsync();

        existing.Description.ShouldBe(SeedDescription);
        existing.Version.ShouldBe(5);
        existing.SeedVersion.ShouldBe(5);
        await _skillRepository.Received(1).UpdateAsync(existing, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ASeedEqualToTheSeedVersion_IsSkipped()
    {
        var existing = GivenStoredSkill(version: 7, seedVersion: 5);
        WriteSeedFile(version: 5);

        await CreateLoader().LoadAsync();

        existing.Description.ShouldBe(LocalDescription);
        await _skillRepository.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ANeverSeededRowThatDivergedLocally_AdoptsOnlyTheDescriptionAndTheVersions()
    {
        var existing = GivenStoredSkill(version: 5, seedVersion: 0, isEnabled: false);
        WriteSeedFile(version: 4);

        await CreateLoader().LoadAsync();

        existing.Description.ShouldBe(SeedDescription);
        existing.Version.ShouldBe(4);
        existing.SeedVersion.ShouldBe(4);
        existing.IsEnabled.ShouldBeFalse();
        existing.TriggerKeywords.ShouldBe(LocalKeywords);
        await _skillRepository.Received(1).UpdateAsync(existing, Arg.Any<CancellationToken>());
        await _phraseRepository.DidNotReceiveWithAnyArgs()
            .ReplaceAllLanguagesAsync(default!, default!, default!, default!, default, default, default);
    }

    [Test]
    public async Task ANeverSeededRowBehindItsSeed_IsAppliedInFullAsBefore()
    {
        var existing = GivenStoredSkill(version: 1, seedVersion: 0, isEnabled: false);
        WriteSeedFile(version: 2);

        await CreateLoader().LoadAsync();

        existing.Description.ShouldBe(SeedDescription);
        existing.IsEnabled.ShouldBeTrue();
        existing.SeedVersion.ShouldBe(2);
    }

    [Test]
    public async Task ANewSkill_IsInsertedWithItsSeedVersion()
    {
        _skillRepository.GetAllByAgentIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>());
        WriteSeedFile(version: 3);

        await CreateLoader().LoadAsync();

        await _skillRepository.Received(1).AddAsync(
            Arg.Is<AgentSkill>(s => s.Name == SkillName && s.Version == 3 && s.SeedVersion == 3),
            Arg.Any<CancellationToken>());
    }

    private AgentSkill GivenStoredSkill(int version, int seedVersion, bool isEnabled = true)
    {
        var existing = new AgentSkill
        {
            AgentId = _agent.Id,
            Name = SkillName,
            Description = LocalDescription,
            Version = version,
            SeedVersion = seedVersion,
            IsEnabled = isEnabled,
            TriggerKeywords = LocalKeywords
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
            _phraseRepository,
            _featurePluginService,
            environment,
            NullLogger<SkillSeedLoader>.Instance);
    }

    private void WriteSeedFile(int version)
    {
        var json =
            "{\"version\":1,\"skills\":[{" +
            $"\"name\":\"{SkillName}\"," +
            $"\"description\":\"{SeedDescription}\"," +
            "\"category\":\"Crud\"," +
            "\"executionType\":\"Skill\"," +
            "\"isEnabled\":true," +
            $"\"version\":{version}" +
            "}]}";

        File.WriteAllText(_seedFilePath, json);
    }
}
