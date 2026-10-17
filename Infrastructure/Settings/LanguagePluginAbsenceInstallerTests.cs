// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The file half of the default absence installer: which values the merge and the removal would send to the
/// database for one pack language. The SQL itself (fill only a missing language of a row that is still the seed
/// row, skip an abbreviation another live type already uses, remove only unchanged pack values) needs PostgreSQL
/// and is not exercised here. Covered: core languages and a missing master file never reach the database,
/// language codes match case-insensitively (zh-CN), every entry carries the seed German name the SQL guards on,
/// blank values are dropped, and the shipped master file yields all eleven types for every pack.
/// </summary>

using Klacks.Api.Infrastructure.Services.Settings;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class LanguagePluginAbsenceInstallerTests
{
    private const string MasterFileName = "default-absence-translations.json";
    private const string ManifestFileName = "manifest.json";
    private const string VacationId = "15ee57e6-31d1-492e-bf83-d3c386ef7472";
    private const string MilitaryId = "a04f8e87-8966-47c0-b293-931ea4f949ae";
    private const string ChineseCode = "zh-CN";
    private const string ChineseKey = "zh-cn";
    private const string PolishKey = "pl";
    private const int ExpectedSeedCount = 11;

    private static readonly string[] PluginsLanguagesRelativePath = ["Klacks.Api", "Plugins", "Languages"];

    private const string FixtureMaster = """
        {
          "absences": [
            {
              "id": "15ee57e6-31d1-492e-bf83-d3c386ef7472",
              "de": "Ferien",
              "name": { "zh-cn": "带薪年假", "pl": "Urlop wypoczynkowy" },
              "abbreviation": { "zh-cn": "年假", "pl": "UW" }
            },
            {
              "id": "a04f8e87-8966-47c0-b293-931ea4f949ae",
              "de": "Militär",
              "name": { "zh-cn": "兵役", "pl": " " },
              "abbreviation": { "zh-cn": "兵" }
            },
            {
              "id": "not-a-guid",
              "de": "Pikett",
              "name": { "pl": "Dyżur" },
              "abbreviation": { "pl": "Dy" }
            },
            {
              "id": "5cb57c1c-ea82-455c-92c6-7920a0d6b19f",
              "name": { "pl": "Dyżur" },
              "abbreviation": { "pl": "Dy" }
            }
          ]
        }
        """;

    private string _pluginDirectory = null!;
    private IServiceProvider _provider = null!;
    private IServiceScope _scope = null!;
    private LanguagePluginAbsenceInstaller _installer = null!;

    [SetUp]
    public void Setup()
    {
        _pluginDirectory = Path.Combine(Path.GetTempPath(), "klacks-absence-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_pluginDirectory);
        File.WriteAllText(Path.Combine(_pluginDirectory, MasterFileName), FixtureMaster);

        _provider = Substitute.For<IServiceProvider>();
        _scope = Substitute.For<IServiceScope>();
        _scope.ServiceProvider.Returns(_provider);

        _installer = new LanguagePluginAbsenceInstaller(_pluginDirectory, NullLogger.Instance);
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

    [TestCase("de")]
    [TestCase("EN")]
    [TestCase("fr")]
    [TestCase("it")]
    public async Task CoreLanguages_NeverReachTheDatabase(string code)
    {
        await _installer.MergeDefaultAbsenceTranslationsAsync(_scope, code);
        await _installer.RemoveDefaultAbsenceTranslationsAsync(_scope, code);

        _provider.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task MissingMasterFile_NeverReachesTheDatabase()
    {
        File.Delete(Path.Combine(_pluginDirectory, MasterFileName));

        await _installer.MergeDefaultAbsenceTranslationsAsync(_scope, PolishKey);
        await _installer.RemoveDefaultAbsenceTranslationsAsync(_scope, PolishKey);

        _provider.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task UnknownLanguage_NeverReachesTheDatabase()
    {
        await _installer.MergeDefaultAbsenceTranslationsAsync(_scope, "xx");

        _provider.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public void ReadTranslations_MatchesTheManifestSpellingCaseInsensitively()
    {
        var names = _installer.ReadTranslations(ChineseCode.ToLowerInvariant(), LanguagePluginAbsenceInstaller.NameProperty);
        var abbreviations = _installer.ReadTranslations(ChineseKey, LanguagePluginAbsenceInstaller.AbbreviationProperty);

        names.Keys.ShouldBe(new[] { VacationId, MilitaryId }, ignoreOrder: true);
        names[VacationId][LanguagePluginAbsenceInstaller.ValueProperty].ShouldBe("带薪年假");
        abbreviations[MilitaryId][LanguagePluginAbsenceInstaller.ValueProperty].ShouldBe("兵");
    }

    [Test]
    public void ReadTranslations_CarriesTheSeedGermanNameForTheRenameGuard()
    {
        var names = _installer.ReadTranslations(PolishKey, LanguagePluginAbsenceInstaller.NameProperty);

        names[VacationId][LanguagePluginAbsenceInstaller.SeedNameProperty].ShouldBe("Ferien");
    }

    [Test]
    public void ReadTranslations_DropsBlankValuesInvalidIdsAndEntriesWithoutSeedName()
    {
        var names = _installer.ReadTranslations(PolishKey, LanguagePluginAbsenceInstaller.NameProperty);
        var abbreviations = _installer.ReadTranslations(PolishKey, LanguagePluginAbsenceInstaller.AbbreviationProperty);

        names.Keys.ShouldBe(new[] { VacationId });
        abbreviations.Keys.ShouldBe(new[] { VacationId });
    }

    [Test]
    public void ShippedMasterFile_YieldsAllElevenTypesInBothFieldsForEveryPack()
    {
        var pluginDirectory = RepositoryRootLocator.RequireDirectory(PluginsLanguagesRelativePath);
        var installer = new LanguagePluginAbsenceInstaller(pluginDirectory, NullLogger.Instance);
        var packCodes = Directory.GetDirectories(pluginDirectory)
            .Where(pack => File.Exists(Path.Combine(pack, ManifestFileName)))
            .Select(pack => Path.GetFileName(pack).ToLowerInvariant())
            .ToList();
        packCodes.ShouldNotBeEmpty();

        var problems = new List<string>();
        foreach (var code in packCodes)
        {
            foreach (var field in new[] { LanguagePluginAbsenceInstaller.NameProperty, LanguagePluginAbsenceInstaller.AbbreviationProperty })
            {
                var count = installer.ReadTranslations(code, field).Count;
                if (count != ExpectedSeedCount)
                {
                    problems.Add($"{code}/{field}: {count}");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join("; ", problems));
    }
}
