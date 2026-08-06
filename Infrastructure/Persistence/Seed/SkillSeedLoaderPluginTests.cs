// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the per-plugin entry points of SkillSeedLoader: seeding one plugin's skills at runtime
/// and flipping their enabled flag. Before these existed, a feature plugin installed while the
/// application was running only got its skills after the next start, and an uninstalled one kept
/// them in the catalogue for good.
/// </summary>
namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

using System.Text.Json;
using Klacks.Api.Application.Interfaces.Plugins;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SkillSeedLoaderPluginTests
{
    private const string PluginName = "messaging";
    private const string PluginSkillName = "send_message";

    private IAgentSkillRepository _skillRepository = null!;
    private IAgentRepository _agentRepository = null!;
    private ISkillPhraseRepository _phraseRepository = null!;
    private IFeaturePluginService _pluginService = null!;
    private IWebHostEnvironment _environment = null!;
    private SkillSeedLoader _loader = null!;
    private string _contentRoot = null!;
    private Agent _agent = null!;

    [SetUp]
    public void SetUp()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "klacks-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_contentRoot, "Plugins", "Features", PluginName));

        _agent = new Agent { Id = Guid.NewGuid(), Name = "klacks-default", IsDefault = true };

        _skillRepository = Substitute.For<IAgentSkillRepository>();
        _skillRepository.GetAllByAgentIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);

        _agentRepository = Substitute.For<IAgentRepository>();
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(_agent);

        _phraseRepository = Substitute.For<ISkillPhraseRepository>();
        _pluginService = Substitute.For<IFeaturePluginService>();

        _environment = Substitute.For<IWebHostEnvironment>();
        _environment.ContentRootPath.Returns(_contentRoot);

        _loader = new SkillSeedLoader(
            _skillRepository, _agentRepository, _phraseRepository, _pluginService, _environment,
            Substitute.For<ILogger<SkillSeedLoader>>());
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
    public async Task SeedPluginSkillsAsync_SkillNotInCatalogue_InsertsIt()
    {
        WritePluginSeed(version: 1);

        await _loader.SeedPluginSkillsAsync(PluginName);

        await _skillRepository.Received(1).AddAsync(
            Arg.Is<AgentSkill>(s => s.Name == PluginSkillName && s.AgentId == _agent.Id),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SeedPluginSkillsAsync_NoSeedFileForThatPlugin_DoesNothing()
    {
        await _loader.SeedPluginSkillsAsync("a-plugin-without-skills");

        await _skillRepository.DidNotReceive().AddAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
        await _skillRepository.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
    }

    // The version gate is the whole point of the separate enable step: an unchanged definition is
    // skipped by the seed, so a re-installed plugin would come back with its skills still disabled.
    [Test]
    public async Task SeedPluginSkillsAsync_SameVersionAlreadyStored_DoesNotReEnableTheSkill()
    {
        WritePluginSeed(version: 1);
        var stored = new AgentSkill { Name = PluginSkillName, Version = 1, IsEnabled = false };
        _skillRepository.GetAllByAgentIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([stored]);

        await _loader.SeedPluginSkillsAsync(PluginName);

        stored.IsEnabled.ShouldBeFalse();
        await _skillRepository.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetPluginSkillsEnabledAsync_False_DisablesTheSkillWithoutDeletingIt()
    {
        WritePluginSeed(version: 1);
        var stored = new AgentSkill { Name = PluginSkillName, Version = 1, IsEnabled = true };
        _skillRepository.GetAllByAgentIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([stored]);

        var changed = await _loader.SetPluginSkillsEnabledAsync(PluginName, isEnabled: false);

        changed.ShouldBe(1);
        stored.IsEnabled.ShouldBeFalse();
        await _skillRepository.Received(1).UpdateAsync(stored, Arg.Any<CancellationToken>());
        await _skillRepository.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetPluginSkillsEnabledAsync_True_BringsADisabledSkillBack()
    {
        WritePluginSeed(version: 1);
        var stored = new AgentSkill { Name = PluginSkillName, Version = 1, IsEnabled = false };
        _skillRepository.GetAllByAgentIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([stored]);

        var changed = await _loader.SetPluginSkillsEnabledAsync(PluginName, isEnabled: true);

        changed.ShouldBe(1);
        stored.IsEnabled.ShouldBeTrue();
    }

    [Test]
    public async Task SetPluginSkillsEnabledAsync_AlreadyInTargetState_WritesNothing()
    {
        WritePluginSeed(version: 1);
        _skillRepository.GetAllByAgentIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([new AgentSkill { Name = PluginSkillName, Version = 1, IsEnabled = false }]);

        var changed = await _loader.SetPluginSkillsEnabledAsync(PluginName, isEnabled: false);

        changed.ShouldBe(0);
        await _skillRepository.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
    }

    // Only the plugin's own skills: a plugin must never switch off a core skill that happens to sit
    // in the same catalogue.
    [Test]
    public async Task SetPluginSkillsEnabledAsync_LeavesSkillsOfOtherOriginsAlone()
    {
        WritePluginSeed(version: 1);
        var foreign = new AgentSkill { Name = "create_shift", Version = 3, IsEnabled = true };
        _skillRepository.GetAllByAgentIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([foreign]);

        var changed = await _loader.SetPluginSkillsEnabledAsync(PluginName, isEnabled: false);

        changed.ShouldBe(0);
        foreign.IsEnabled.ShouldBeTrue();
    }

    private void WritePluginSeed(int version)
    {
        var definitions = new[]
        {
            new
            {
                name = PluginSkillName,
                description = "Sends a message through a configured messenger provider.",
                category = "Action",
                executionType = "Skill",
                requiredPermissions = Array.Empty<string>(),
                isEnabled = true,
                version
            }
        };

        File.WriteAllText(
            Path.Combine(_contentRoot, "Plugins", "Features", PluginName, "skill-seeds.json"),
            JsonSerializer.Serialize(definitions));
    }
}
