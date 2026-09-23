// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Proves that the startup backfill of language-pack skill synonyms writes under the manifest spelling of
/// the pack code. The installed codes come back lower-cased from the settings (zh-cn), while a fresh install
/// keys the jsonb mirror and the skill_phrase rows as zh-CN - a backfill under the lower-cased code would
/// add a second key and a second set of phrase rows for the same language.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class LanguagePluginServiceSkillSynonymBackfillTests
{
    private const string ManifestCode = "zh-CN";
    private const string InstalledSettingKey = "INSTALLED_LANGUAGE_ZH-CN";
    private const string SkillName = "add_employee_to_group";
    private const string Term = "添加员工到组";

    private string _pluginDirectory = null!;
    private DataBaseContext _context = null!;
    private IAgentSkillRepository _skillRepository = null!;
    private AgentSkill _skill = null!;
    private LanguagePluginService _service = null!;

    [SetUp]
    public void Setup()
    {
        _pluginDirectory = Path.Combine(Path.GetTempPath(), "klacks-skill-synonym-backfill-" + Guid.NewGuid().ToString("N"));
        var packDirectory = Path.Combine(_pluginDirectory, ManifestCode);
        Directory.CreateDirectory(packDirectory);
        File.WriteAllText(
            Path.Combine(packDirectory, "manifest.json"),
            $"{{\"code\": \"{ManifestCode}\", \"name\": \"Chinese (Simplified)\"}}");
        File.WriteAllText(
            Path.Combine(packDirectory, "skill-synonyms.json"),
            $"{{\"{SkillName}\": [\"{Term}\"]}}");

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _context.Database.EnsureCreated();

        _skill = new AgentSkill
        {
            Id = Guid.NewGuid(),
            Name = SkillName,
            Synonyms = new Dictionary<string, List<string>> { ["de"] = ["mitarbeiter zur gruppe"] }
        };
        _skillRepository = Substitute.For<IAgentSkillRepository>();
        _skillRepository.GetAllEnabledTrackedAsync().Returns(new List<AgentSkill> { _skill });

        var settingsRepository = Substitute.For<ISettingsRepository>();
        settingsRepository.GetSettingsList().Returns(Task.FromResult<IEnumerable<SettingsEntity>>(
            new List<SettingsEntity>
            {
                new() { Id = Guid.NewGuid(), Type = InstalledSettingKey, Value = "true" }
            }));

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IAgentSkillRepository)).Returns(_skillRepository);
        provider.GetService(typeof(ISkillPhraseRepository)).Returns(new SkillPhraseRepository(_context));
        provider.GetService(typeof(ISettingsRepository)).Returns(settingsRepository);
        provider.GetService(typeof(DataBaseContext)).Returns(_context);
        provider.GetService(typeof(IUnitOfWork)).Returns(Substitute.For<IUnitOfWork>());

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanguagePlugins:Directory"] = _pluginDirectory
            })
            .Build();

        _service = new LanguagePluginService(scopeFactory, configuration, NullLogger<LanguagePluginService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();

        if (Directory.Exists(_pluginDirectory))
        {
            Directory.Delete(_pluginDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Backfill_WritesUnderTheManifestSpellingOfTheCode()
    {
        await _service.ApplyInstalledSkillSynonymBackfillAsync();

        _skill.Synonyms!.ShouldContainKey(ManifestCode);
        _skill.Synonyms.ContainsKey(ManifestCode.ToLowerInvariant()).ShouldBeFalse();
        _skill.Synonyms[ManifestCode].ShouldBe([Term]);

        var rows = await _context.SkillPhrases.AsNoTracking().ToListAsync();

        rows.Count.ShouldBe(1);
        rows[0].Language.ShouldBe(ManifestCode);
        rows[0].Source.ShouldBe(SkillPhraseSources.LanguagePack);
    }

    [Test]
    public async Task Backfill_RunTwice_WritesTheSkillOnlyOnce()
    {
        await _service.ApplyInstalledSkillSynonymBackfillAsync();
        await _service.ApplyInstalledSkillSynonymBackfillAsync();

        await _skillRepository.Received(1).UpdateAsync(_skill, Arg.Any<CancellationToken>());
    }
}
