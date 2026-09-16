// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the recipe-vetoes half of the language plugin content installer, run against a real
/// DbContext rather than a substitute repository. The substitute fixtures beside this one cannot see
/// the failure these cases pin: every installer in InstallPluginAsync shares ONE scope, and the recipe
/// repository reads with AsNoTracking but writes with Update. A second installer touching the same
/// recipe rows in that scope hands EF a second instance of an already-tracked key, which throws and is
/// swallowed by the installer's catch - the pack installs its synonyms but silently no vetoes, and the
/// startup backfill stops after the first language.
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
public class LanguagePluginRecipeVetoesInstallerTests
{
    private const string Spanish = "es";
    private const string Polish = "pl";
    private const string Recipe = "add-employee-to-group";
    private const string SpanishSynonym = "incorporar un empleado al grupo";
    private const string SpanishVeto = "cómo ";
    private const string PolishVeto = "jak ";

    private string _pluginDirectory = null!;
    private string _databaseName = null!;
    private DataBaseContext _context = null!;
    private IServiceScope _scope = null!;
    private LanguagePluginContentInstaller _installer = null!;

    [SetUp]
    public void Setup()
    {
        _pluginDirectory = Path.Combine(Path.GetTempPath(), "klacks-recipe-veto-" + Guid.NewGuid().ToString("N"));
        WritePackFile(Spanish, "recipe-synonyms.json", $"{{\"{Recipe}\": [\"{SpanishSynonym}\"]}}");
        WritePackFile(Spanish, "recipe-vetoes.json", $"{{\"{Recipe}\": [\"{SpanishVeto}\"]}}");
        WritePackFile(Polish, "recipe-vetoes.json", $"{{\"{Recipe}\": [\"{PolishVeto}\"]}}");

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
    public async Task Install_AfterTheSynonymInstallerInTheSameScope_PersistsTheVetoes()
    {
        await _installer.InstallRecipeSynonymsAsync(_scope, Spanish);
        await _installer.InstallRecipeVetoesAsync(_scope, Spanish);

        var stored = ReadBack();
        Assert.That(stored.Synonyms?[Spanish], Does.Contain(SpanishSynonym));
        Assert.That(stored.Vetoes, Is.Not.Null,
            "the vetoes installer runs after the synonyms installer in the same scope and must still write");
        Assert.That(stored.Vetoes![Spanish], Does.Contain(SpanishVeto));
    }

    [Test]
    public async Task Install_TwoLanguagesInTheSameScope_PersistsBoth()
    {
        await _installer.InstallRecipeVetoesAsync(_scope, Spanish);
        await _installer.InstallRecipeVetoesAsync(_scope, Polish);

        var stored = ReadBack();
        Assert.That(stored.Vetoes, Is.Not.Null);
        Assert.That(stored.Vetoes!.Keys, Is.EquivalentTo(new[] { Spanish, Polish }),
            "the startup backfill installs every language through one scope; the second must not be lost");
    }

    [Test]
    public async Task Install_IsIdempotent_AndDoesNotRewriteAnUnchangedRecipe()
    {
        await _installer.InstallRecipeVetoesAsync(_scope, Spanish);
        var firstStamp = ReadBack().UpdateTime;

        await _installer.InstallRecipeVetoesAsync(_scope, Spanish);

        var stored = ReadBack();
        Assert.That(stored.Vetoes![Spanish], Is.EqualTo(new[] { SpanishVeto }));
        Assert.That(stored.UpdateTime, Is.EqualTo(firstStamp),
            "a startup backfill that finds the vocabulary already installed must not touch the row");
    }

    [Test]
    public async Task Uninstall_AfterTheSynonymUninstallerInTheSameScope_RemovesOnlyThatLanguage()
    {
        await _installer.InstallRecipeSynonymsAsync(_scope, Spanish);
        await _installer.InstallRecipeVetoesAsync(_scope, Spanish);
        await _installer.InstallRecipeVetoesAsync(_scope, Polish);

        await _installer.UninstallRecipeSynonymsAsync(_scope, Spanish);
        await _installer.UninstallRecipeVetoesAsync(_scope, Spanish);

        var stored = ReadBack();
        Assert.That(stored.Vetoes, Is.Not.Null);
        Assert.That(stored.Vetoes!.Keys, Is.EquivalentTo(new[] { Polish }));
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

    private void WritePackFile(string code, string fileName, string json)
    {
        Directory.CreateDirectory(Path.Combine(_pluginDirectory, code));
        File.WriteAllText(Path.Combine(_pluginDirectory, code, fileName), json);
    }
}
