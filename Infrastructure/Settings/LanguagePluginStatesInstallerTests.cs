// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Proves that installing a language plugin's states.json upserts the state table instead of only
/// merging translations into rows that already exist: a plugin's own subdivisions (e.g. Spanish
/// autonomous communities) must be inserted as new rows, while subdivisions that already exist in
/// the core seed only get their translations merged.
/// </summary>

using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class LanguagePluginStatesInstallerTests
{
    private const string Code = "es";
    private static readonly Guid StateId = Guid.Parse("51b2c3d4-e501-4000-8000-000000000001");

    private string _pluginDirectory = null!;
    private DataBaseContext _context = null!;
    private IServiceScope _scope = null!;
    private LanguagePluginContentInstaller _installer = null!;

    [SetUp]
    public void Setup()
    {
        _pluginDirectory = Path.Combine(Path.GetTempPath(), "klacks-states-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_pluginDirectory, Code));

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _context.Database.EnsureCreated();

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(DataBaseContext)).Returns(_context);
        _scope = Substitute.For<IServiceScope>();
        _scope.ServiceProvider.Returns(provider);

        _installer = new LanguagePluginContentInstaller(_pluginDirectory, NullLogger.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _scope.Dispose();
        _context.Database.EnsureDeleted();
        _context.Dispose();

        if (Directory.Exists(_pluginDirectory))
        {
            Directory.Delete(_pluginDirectory, recursive: true);
        }
    }

    [Test]
    public async Task InstallStatesAsync_NewState_IsInserted_WithAllLanguages()
    {
        GivenStatesFile(
            $$"""
            [
                {
                    "id": "{{StateId}}",
                    "abbreviation": "AN",
                    "countryPrefix": "ES",
                    "name": { "de": "Andalusien", "es": "Andalucía", "en": "Andalusia" }
                }
            ]
            """);

        await _installer.InstallStatesAsync(_scope, Code);

        var rows = await _context.State.AsNoTracking().ToListAsync();

        rows.Count.ShouldBe(1);
        rows[0].Id.ShouldBe(StateId);
        rows[0].Abbreviation.ShouldBe("AN");
        rows[0].CountryPrefix.ShouldBe("ES");
        rows[0].Name.GetValue("es").ShouldBe("Andalucía");
        rows[0].Name.GetValue("de").ShouldBe("Andalusien");
        rows[0].Name.GetValue("en").ShouldBe("Andalusia");
    }

    [Test]
    public async Task InstallStatesAsync_ExistingState_MergesTranslations_KeepsAbbreviation()
    {
        var name = new MultiLanguage();
        name.SetValue("de", "Andalusien");
        _context.State.Add(new State
        {
            Id = StateId,
            Abbreviation = "AN",
            CountryPrefix = "ES",
            Name = name
        });
        await _context.SaveChangesAsync();

        GivenStatesFile(
            $$"""
            [
                {
                    "id": "{{StateId}}",
                    "abbreviation": "AN",
                    "countryPrefix": "ES",
                    "name": { "de": "Andalusien", "es": "Andalucía", "en": "Andalusia" }
                }
            ]
            """);

        await _installer.InstallStatesAsync(_scope, Code);

        var rows = await _context.State.AsNoTracking().ToListAsync();

        rows.Count.ShouldBe(1);
        rows[0].Abbreviation.ShouldBe("AN");
        rows[0].Name.GetValue("de").ShouldBe("Andalusien");
        rows[0].Name.GetValue("es").ShouldBe("Andalucía");
        rows[0].Name.GetValue("en").ShouldBe("Andalusia");
    }

    [Test]
    public async Task InstallStatesAsync_MissingFile_DoesNothing()
    {
        await _installer.InstallStatesAsync(_scope, Code);

        var rows = await _context.State.AsNoTracking().ToListAsync();

        rows.ShouldBeEmpty();
    }

    [Test]
    public async Task InstallStatesAsync_InvalidId_IsSkipped()
    {
        GivenStatesFile(
            """
            [
                {
                    "id": "not-a-guid",
                    "abbreviation": "AN",
                    "countryPrefix": "ES",
                    "name": { "de": "Andalusien" }
                }
            ]
            """);

        await _installer.InstallStatesAsync(_scope, Code);

        var rows = await _context.State.AsNoTracking().ToListAsync();

        rows.ShouldBeEmpty();
    }

    private void GivenStatesFile(string json)
    {
        File.WriteAllText(Path.Combine(_pluginDirectory, Code, "states.json"), json);
    }
}
