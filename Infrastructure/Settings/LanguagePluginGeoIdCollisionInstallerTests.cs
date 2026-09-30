// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Proves the language pack geo installers no longer trust an id alone: a row whose id another pack claimed
/// first (different natural key) is left untouched and the pack's own row is inserted under a fresh id, and
/// a pack entry whose id changed still finds its row by natural key instead of inserting a duplicate.
/// </summary>

using System.Collections.Concurrent;
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
public class LanguagePluginGeoIdCollisionInstallerTests
{
    private const string Code = "id";
    private const string OwnerName = "ישראל";
    private static readonly Guid SharedId = Guid.Parse("a1b2c3d4-bb01-4000-8000-000000000001");
    private static readonly Guid NewPackId = Guid.Parse("0f5e8a52-7c1d-4b8e-9a0e-2c7d1f3b6a91");

    private string _pluginDirectory = null!;
    private DataBaseContext _context = null!;
    private IServiceScope _scope = null!;
    private LanguagePluginContentInstaller _contentInstaller = null!;
    private LanguagePluginGeoDataInstaller _geoDataInstaller = null!;

    [SetUp]
    public void Setup()
    {
        _pluginDirectory = Path.Combine(Path.GetTempPath(), "klacks-geo-collision-" + Guid.NewGuid().ToString("N"));
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

        _contentInstaller = new LanguagePluginContentInstaller(_pluginDirectory, NullLogger.Instance);
        _geoDataInstaller = new LanguagePluginGeoDataInstaller(
            _pluginDirectory, new ConcurrentDictionary<string, LanguagePluginManifest>(), NullLogger.Instance);
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
    public async Task InstallCountryAsync_IdOwnedByOtherCountry_LeavesItUntouched_AndInsertsUnderFreshId()
    {
        await GivenCountryAsync(SharedId, "IL", OwnerName);
        GivenPackFile("countries.json", CountryJson(SharedId, "ID", "Indonesia"));

        await _contentInstaller.InstallCountryAsync(_scope, Code);

        var rows = await _context.Countries.AsNoTracking().ToListAsync();
        rows.Count.ShouldBe(2);
        var owner = rows.Single(c => c.Id == SharedId);
        owner.Abbreviation.ShouldBe("IL");
        owner.Name.GetValue("he").ShouldBe(OwnerName);
        owner.Name.GetValue("en").ShouldBeNull();
        rows.Single(c => c.Abbreviation == "ID").Id.ShouldNotBe(SharedId);
    }

    [Test]
    public async Task InstallCountryAsync_RenumberedEntry_MergesIntoRowWithSameAbbreviation()
    {
        await GivenCountryAsync(SharedId, "ID", "Indonesia");
        GivenPackFile("countries.json", CountryJson(NewPackId, "ID", "Indonesia"));

        await _contentInstaller.InstallCountryAsync(_scope, Code);

        var rows = await _context.Countries.AsNoTracking().ToListAsync();
        rows.Count.ShouldBe(1);
        rows[0].Id.ShouldBe(SharedId);
        rows[0].Name.GetValue("en").ShouldBe("Indonesia");
    }

    [Test]
    public async Task InstallCountryAsync_SoftDeletedRow_IsNeitherWrittenNorRevivedNorDuplicated()
    {
        await GivenCountryAsync(SharedId, "ID", "Indonesia");
        var row = await _context.Countries.SingleAsync();
        row.IsDeleted = true;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        GivenPackFile("countries.json", CountryJson(SharedId, "ID", "Indonesia"));

        await _contentInstaller.InstallCountryAsync(_scope, Code);

        var rows = await _context.Countries.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        rows.Count.ShouldBe(1);
        rows[0].IsDeleted.ShouldBeTrue();
        rows[0].Name.GetValue("en").ShouldBeNull();
    }

    [Test]
    public async Task InstallGeoDataAsync_SoftDeletedCountry_IsRevived()
    {
        await GivenCountryAsync(SharedId, "ID", "Indonesia");
        var row = await _context.Countries.SingleAsync();
        row.IsDeleted = true;
        await _context.SaveChangesAsync();
        GivenPackFile("countries.json", CountryJson(SharedId, "ID", "Indonesia"));

        await _geoDataInstaller.InstallGeoDataAsync(_scope, Code);
        await _context.SaveChangesAsync();

        var rows = await _context.Countries.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        rows.Count.ShouldBe(1);
        rows[0].IsDeleted.ShouldBeFalse();
    }

    [Test]
    public async Task InstallStatesAsync_IdOwnedByOtherState_LeavesItUntouched_AndInsertsUnderFreshId()
    {
        var name = new MultiLanguage();
        name.SetValue("he", "ירושלים");
        _context.State.Add(new State { Id = SharedId, Abbreviation = "JM", CountryPrefix = "IL", Name = name });
        await _context.SaveChangesAsync();
        GivenPackFile("states.json",
            $$"""[ { "id": "{{SharedId}}", "abbreviation": "AC", "countryPrefix": "ID", "name": { "en": "Aceh" } } ]""");

        await _contentInstaller.InstallStatesAsync(_scope, Code);

        var rows = await _context.State.AsNoTracking().ToListAsync();
        rows.Count.ShouldBe(2);
        var owner = rows.Single(s => s.Id == SharedId);
        owner.Abbreviation.ShouldBe("JM");
        owner.Name.GetValue("en").ShouldBeNull();
        rows.Single(s => s.CountryPrefix == "ID").Id.ShouldNotBe(SharedId);
    }

    [Test]
    public async Task InstallGeoDataAsync_CalendarRuleIdOwnedByOtherRule_KeepsOwner_AndInsertsPackRule()
    {
        var name = new MultiLanguage();
        name.SetValue("en", "Purim");
        _context.CalendarRule.Add(new CalendarRule { Id = SharedId, Country = "IL", State = "IL", Rule = "03/03", Name = name });
        await _context.SaveChangesAsync();
        GivenPackFile("calendar-rules.json", CalendarRuleJson(SharedId));

        await _geoDataInstaller.InstallGeoDataAsync(_scope, Code);
        await _context.SaveChangesAsync();

        var rules = await _context.CalendarRule.AsNoTracking().ToListAsync();
        rules.Count.ShouldBe(2);
        var owner = rules.Single(r => r.Id == SharedId);
        owner.Country.ShouldBe("IL");
        owner.Name.GetValue("en").ShouldBe("Purim");
        rules.Single(r => r.Country == "ID").Id.ShouldNotBe(SharedId);
    }

    [Test]
    public async Task InstallGeoDataAsync_RenumberedCalendarRule_DoesNotInsertDuplicate()
    {
        var name = new MultiLanguage();
        name.SetValue("en", "New Year's Day");
        _context.CalendarRule.Add(new CalendarRule { Id = SharedId, Country = "ID", State = "ID", Rule = "01/01", Name = name });
        await _context.SaveChangesAsync();
        GivenPackFile("calendar-rules.json", CalendarRuleJson(NewPackId));

        await _geoDataInstaller.InstallGeoDataAsync(_scope, Code);
        await _context.SaveChangesAsync();

        var rules = await _context.CalendarRule.AsNoTracking().ToListAsync();
        rules.Count.ShouldBe(1);
        rules[0].Id.ShouldBe(SharedId);
    }

    private async Task GivenCountryAsync(Guid id, string abbreviation, string heName)
    {
        var name = new MultiLanguage();
        name.SetValue("he", heName);
        _context.Countries.Add(new Countries { Id = id, Abbreviation = abbreviation, Name = name, Prefix = "+972" });
        await _context.SaveChangesAsync();
    }

    private static string CountryJson(Guid id, string abbreviation, string englishName) =>
        $$"""[ { "id": "{{id}}", "abbreviation": "{{abbreviation}}", "name": { "en": "{{englishName}}" }, "prefix": "+62" } ]""";

    private static string CalendarRuleJson(Guid id) =>
        $$"""
        [ { "id": "{{id}}", "rule": "01/01", "subRule": "", "isMandatory": true, "isPaid": true,
            "state": "ID", "country": "ID", "name": { "en": "New Year's Day" }, "description": { "en": "" } } ]
        """;

    private void GivenPackFile(string fileName, string json)
    {
        File.WriteAllText(Path.Combine(_pluginDirectory, Code, fileName), json);
    }
}
