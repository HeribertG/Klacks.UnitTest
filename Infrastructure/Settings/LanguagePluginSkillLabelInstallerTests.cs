// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The pack half of the authored skill labels: a language pack's skill-labels.json lands in
/// AgentSkill.Labels under that pack's own code, touches no other language, and is removed again on
/// uninstall. Uninstall is column-driven rather than file-driven on purpose - the newer precedent of
/// UninstallRecipeVetoesAsync - so a renamed or deleted pack file cannot strand a label that nobody can
/// reach any more. The fixture builds its own throwaway pack directory, because the 21 real files are
/// not part of this task and this test must pass before any of them exists.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class LanguagePluginSkillLabelInstallerTests
{
    private const string Code = "pl";
    private const string MatchingSkill = "add_employee_to_group";
    private const string UnlistedSkill = "create_group";
    private const string PolishLabel = "Dodaj pracownika do grupy";
    private const string GermanLabel = "Mitarbeitende zur Gruppe hinzufügen";

    private string _pluginDirectory = null!;
    private AgentSkill _matching = null!;
    private AgentSkill _unlisted = null!;
    private IAgentSkillRepository _skillRepository = null!;
    private IServiceScope _scope = null!;
    private LanguagePluginContentInstaller _installer = null!;

    [SetUp]
    public void Setup()
    {
        _pluginDirectory = Path.Combine(Path.GetTempPath(), "klacks-skill-label-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_pluginDirectory, Code));
        File.WriteAllText(
            Path.Combine(_pluginDirectory, Code, "skill-labels.json"),
            $"{{\"{MatchingSkill}\": \"{PolishLabel}\"}}");

        _matching = new AgentSkill
        {
            Id = Guid.NewGuid(),
            Name = MatchingSkill,
            Labels = new Dictionary<string, string> { ["de"] = GermanLabel }
        };
        _unlisted = new AgentSkill { Id = Guid.NewGuid(), Name = UnlistedSkill };

        _skillRepository = Substitute.For<IAgentSkillRepository>();
        _skillRepository.GetAllEnabledAsync().Returns(new List<AgentSkill> { _matching, _unlisted });

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IAgentSkillRepository)).Returns(_skillRepository);
        _scope = Substitute.For<IServiceScope>();
        _scope.ServiceProvider.Returns(provider);

        _installer = new LanguagePluginContentInstaller(_pluginDirectory, NullLogger.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _scope.Dispose();

        if (Directory.Exists(_pluginDirectory))
        {
            Directory.Delete(_pluginDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Install_WritesThePackLabelUnderThePackCode()
    {
        await _installer.InstallSkillLabelsAsync(_scope, Code);

        _matching.Labels.ShouldContainKeyAndValue(Code, PolishLabel);
    }

    // A pack owns its own language and nothing else. The core-language label the seed authored has to
    // survive, or installing a pack would blank the four languages Klacks ships with.
    [Test]
    public async Task Install_LeavesEveryOtherLanguageAlone()
    {
        await _installer.InstallSkillLabelsAsync(_scope, Code);

        _matching.Labels.ShouldContainKeyAndValue("de", GermanLabel);
    }

    [Test]
    public async Task Install_SkipsSkillsThePackDoesNotName()
    {
        await _installer.InstallSkillLabelsAsync(_scope, Code);

        _unlisted.Labels.ShouldBeNull();
    }

    [Test]
    public async Task Install_WithoutAPackFile_DoesNothing()
    {
        File.Delete(Path.Combine(_pluginDirectory, Code, "skill-labels.json"));

        await _installer.InstallSkillLabelsAsync(_scope, Code);

        _matching.Labels.ShouldNotContainKey(Code);
        await _skillRepository.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<AgentSkill>());
    }

    [Test]
    public async Task Install_WithABlankLabel_WritesNothingForThatSkill()
    {
        File.WriteAllText(
            Path.Combine(_pluginDirectory, Code, "skill-labels.json"),
            $"{{\"{MatchingSkill}\": \"   \"}}");

        await _installer.InstallSkillLabelsAsync(_scope, Code);

        _matching.Labels.ShouldNotContainKey(Code);
    }

    [Test]
    public async Task Install_IsIdempotent()
    {
        await _installer.InstallSkillLabelsAsync(_scope, Code);
        await _installer.InstallSkillLabelsAsync(_scope, Code);

        _matching.Labels.ShouldContainKeyAndValue(Code, PolishLabel);
        _matching.Labels!.Count.ShouldBe(2);
    }

    // IAgentSkillRepository.UpdateAsync commits on its own, and the startup backfill re-runs this for
    // every installed pack. An unchanged label must therefore cost no write at all, or a steady-state
    // boot would issue one round trip per skill per pack for values that are already correct.
    [Test]
    public async Task Install_WritesNothingWhenTheLabelIsAlreadyStored()
    {
        await _installer.InstallSkillLabelsAsync(_scope, Code);
        await _installer.InstallSkillLabelsAsync(_scope, Code);

        await _skillRepository.Received(1).UpdateAsync(_matching);
    }

    [Test]
    public async Task Uninstall_RemovesOnlyThePacksOwnLanguage()
    {
        await _installer.InstallSkillLabelsAsync(_scope, Code);

        await _installer.UninstallSkillLabelsAsync(_scope, Code);

        _matching.Labels.ShouldNotContainKey(Code);
        _matching.Labels.ShouldContainKeyAndValue("de", GermanLabel);
    }

    // Column-driven, not file-driven: the row is cleaned even when the pack file has vanished since.
    [Test]
    public async Task Uninstall_WorksEvenWhenThePackFileIsGone()
    {
        await _installer.InstallSkillLabelsAsync(_scope, Code);
        File.Delete(Path.Combine(_pluginDirectory, Code, "skill-labels.json"));

        await _installer.UninstallSkillLabelsAsync(_scope, Code);

        _matching.Labels.ShouldNotContainKey(Code);
    }
}
