// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the recipe-anchors half of LanguagePluginRecipeVocabularyInstaller, run against a real
/// DbContext for the reason LanguagePluginRecipeVetoesInstallerTests gives: every installer shares ONE
/// scope, the recipe rows are already tracked by the synonym and veto installers, and a second instance of
/// a tracked key throws inside the installer's catch - the pack would install silently without anchors.
/// Installer errors are only logged, so every case reads the column back from a fresh context instead of
/// trusting a green run.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class LanguagePluginRecipeAnchorsInstallerTests
{
    private const string Spanish = "es";
    private const string Polish = "pl";
    private const string ChineseSimplified = "zh-CN";
    private const string Recipe = "add-employee-to-group";
    private const string SynonymsFileName = "recipe-synonyms.json";
    private const string VetoesFileName = "recipe-vetoes.json";
    private const string AnchorsFileName = "recipe-anchors.json";
    private const string SpanishSynonym = "incorporar un empleado al grupo";
    private const string SpanishVeto = "cómo ";
    private const string SpanishAnchor = "grupo";
    private const string SpanishAnchorReplacement = "equipo";
    private const string PolishAnchor = "grup";
    private const string ChineseAnchor = "小组";
    private const string MalformedJson = "{ \"add-employee-to-group\": [ \"grupo\" ";

    private string _pluginDirectory = null!;
    private string _databaseName = null!;
    private DataBaseContext _context = null!;
    private IServiceScope _scope = null!;
    private LanguagePluginContentInstaller _installer = null!;
    private LanguagePluginRecipeVocabularyInstaller _vocabularyInstaller = null!;

    [SetUp]
    public void Setup()
    {
        _pluginDirectory = Path.Combine(Path.GetTempPath(), "klacks-recipe-anchor-" + Guid.NewGuid().ToString("N"));
        WritePackFile(Spanish, SynonymsFileName, $"{{\"{Recipe}\": [\"{SpanishSynonym}\"]}}");
        WritePackFile(Spanish, VetoesFileName, $"{{\"{Recipe}\": [\"{SpanishVeto}\"]}}");
        WriteAnchors(Spanish, SpanishAnchor);
        WriteAnchors(Polish, PolishAnchor);
        WriteAnchors(ChineseSimplified, ChineseAnchor);

        _databaseName = Guid.NewGuid().ToString();
        using (var seeding = CreateContext())
        {
            seeding.Database.EnsureCreated();
            seeding.AgentRecipes.Add(new AgentRecipe { Name = Recipe });
            seeding.SaveChanges();
        }

        _context = CreateContext();

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IAgentRecipeRepository)).Returns(new AgentRecipeRepository(_context));
        provider.GetService(typeof(ISkillPhraseRepository)).Returns(Substitute.For<ISkillPhraseRepository>());
        _scope = Substitute.For<IServiceScope>();
        _scope.ServiceProvider.Returns(provider);

        _installer = new LanguagePluginContentInstaller(_pluginDirectory, NullLogger.Instance);
        _vocabularyInstaller = new LanguagePluginRecipeVocabularyInstaller(_pluginDirectory, NullLogger.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _scope.Dispose();
        _context.Dispose();
        if (Directory.Exists(_pluginDirectory))
        {
            Directory.Delete(_pluginDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Install_WritesTheAnchorsUnderTheManifestCode()
    {
        await _vocabularyInstaller.InstallRecipeAnchorsAsync(_scope, ChineseSimplified);

        var stored = ReadBack();
        stored.Anchors.ShouldNotBeNull();
        stored.Anchors!.Keys.ShouldBe(new[] { ChineseSimplified });
        stored.Anchors[ChineseSimplified].ShouldBe(new[] { ChineseAnchor });
    }

    [Test]
    public async Task Install_AfterTheSynonymAndVetoInstallersInTheSameScope_PersistsTheAnchors()
    {
        await _installer.InstallRecipeSynonymsAsync(_scope, Spanish);
        await _vocabularyInstaller.InstallRecipeVetoesAsync(_scope, Spanish);
        await _vocabularyInstaller.InstallRecipeAnchorsAsync(_scope, Spanish);

        var stored = ReadBack();
        stored.Synonyms![Spanish].ShouldContain(SpanishSynonym);
        stored.Vetoes![Spanish].ShouldContain(SpanishVeto);
        stored.Anchors.ShouldNotBeNull("the anchor installer runs after the synonym and veto installers in one scope");
        stored.Anchors![Spanish].ShouldBe(new[] { SpanishAnchor });
    }

    [Test]
    public async Task Install_TwoLanguagesInTheSameScope_PersistsBoth()
    {
        await _installer.InstallRecipeSynonymsAsync(_scope, Spanish);
        await _vocabularyInstaller.InstallRecipeAnchorsAsync(_scope, Spanish);
        await _vocabularyInstaller.InstallRecipeAnchorsAsync(_scope, Polish);

        var stored = ReadBack();
        stored.Anchors.ShouldNotBeNull();
        stored.Anchors!.Keys.ShouldBe(new[] { Spanish, Polish }, ignoreOrder: true);
    }

    [Test]
    public async Task Install_DoesNotMirrorAnchorsIntoSkillPhrases()
    {
        var phraseRepository = Substitute.For<ISkillPhraseRepository>();
        _scope.ServiceProvider.GetService(typeof(ISkillPhraseRepository)).Returns(phraseRepository);

        await _vocabularyInstaller.InstallRecipeAnchorsAsync(_scope, Spanish);

        phraseRepository.ReceivedCalls().ShouldBeEmpty();
        ReadBack().Anchors![Spanish].ShouldBe(new[] { SpanishAnchor });
    }

    [Test]
    public async Task Reinstall_ReplacesTheLanguageKey()
    {
        await _vocabularyInstaller.InstallRecipeAnchorsAsync(_scope, Spanish);
        WriteAnchors(Spanish, SpanishAnchorReplacement);

        await _vocabularyInstaller.InstallRecipeAnchorsAsync(_scope, Spanish);

        ReadBack().Anchors![Spanish].ShouldBe(new[] { SpanishAnchorReplacement });
    }

    [Test]
    public async Task Reinstall_OfAnUnchangedFile_DoesNotRewriteTheRecipe()
    {
        await _vocabularyInstaller.InstallRecipeAnchorsAsync(_scope, Spanish);
        var firstStamp = ReadBack().UpdateTime;

        await _vocabularyInstaller.InstallRecipeAnchorsAsync(_scope, Spanish);

        var stored = ReadBack();
        stored.Anchors![Spanish].ShouldBe(new[] { SpanishAnchor });
        stored.UpdateTime.ShouldBe(firstStamp);
    }

    [Test]
    public async Task Uninstall_RemovesOnlyThatLanguage()
    {
        await _installer.InstallRecipeSynonymsAsync(_scope, Spanish);
        await _vocabularyInstaller.InstallRecipeAnchorsAsync(_scope, Spanish);
        await _vocabularyInstaller.InstallRecipeAnchorsAsync(_scope, Polish);

        await _installer.UninstallRecipeSynonymsAsync(_scope, Spanish);
        await _vocabularyInstaller.UninstallRecipeAnchorsAsync(_scope, Spanish);

        var stored = ReadBack();
        stored.Anchors.ShouldNotBeNull();
        stored.Anchors!.Keys.ShouldBe(new[] { Polish });
    }

    [Test]
    public async Task Uninstall_LeavesTheVetoColumnUntouched()
    {
        await _vocabularyInstaller.InstallRecipeVetoesAsync(_scope, Spanish);
        await _vocabularyInstaller.InstallRecipeAnchorsAsync(_scope, Spanish);

        await _vocabularyInstaller.UninstallRecipeAnchorsAsync(_scope, Spanish);

        var stored = ReadBack();
        stored.Vetoes![Spanish].ShouldBe(new[] { SpanishVeto });
        stored.Anchors!.ShouldBeEmpty();
    }

    [Test]
    public async Task MalformedFile_LeavesTheInstalledAnchorsUnchanged()
    {
        await _vocabularyInstaller.InstallRecipeAnchorsAsync(_scope, Spanish);
        WritePackFile(Spanish, AnchorsFileName, MalformedJson);

        await _vocabularyInstaller.InstallRecipeAnchorsAsync(_scope, Spanish);

        ReadBack().Anchors![Spanish].ShouldBe(new[] { SpanishAnchor });
    }

    private AgentRecipe ReadBack()
    {
        using var reader = CreateContext();
        return reader.AgentRecipes.AsNoTracking().Single(r => r.Name == Recipe);
    }

    private DataBaseContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options;
        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private void WriteAnchors(string code, string term) =>
        WritePackFile(code, AnchorsFileName, $"{{\"{Recipe}\": [\"{term}\"]}}");

    private void WritePackFile(string code, string fileName, string json)
    {
        Directory.CreateDirectory(Path.Combine(_pluginDirectory, code));
        File.WriteAllText(Path.Combine(_pluginDirectory, code, fileName), json);
    }
}
