// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the embedded header/gender vocabulary of the employee import: every installed language pack
/// (Plugins/Languages directories) plus de/en/fr/it has a template header for every template column,
/// male and female words, and no normalized synonym points at two different targets or genders across
/// languages (the detector works without a language hint, so a collision would silently misroute).
/// </summary>

using Klacks.Api.Application.Services.ClientImport;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.ClientImport;

[TestFixture]
public class ClientImportSynonymCatalogTests
{
    private const string PluginsLanguagesRelativePath = "Plugins/Languages";

    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private ClientImportSynonymCatalog _catalog = null!;

    [OneTimeSetUp]
    public void LoadCatalog()
    {
        _catalog = ClientImportSynonymCatalog.LoadEmbedded();
    }

    public static IEnumerable<string> SupportedLanguages()
    {
        var languagesDirectory = RepositoryRootLocator.RequireDirectory(RepositoryRootLocator.ApiProjectDirectoryName, PluginsLanguagesRelativePath);

        var packs = Directory.GetDirectories(languagesDirectory)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!);

        return CoreLanguages.Concat(packs).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(l => l, StringComparer.Ordinal);
    }

    [Test]
    public void LanguageSource_FindsAllTwentyFivePacks()
    {
        SupportedLanguages().Count().ShouldBeGreaterThanOrEqualTo(25);
    }

    [TestCaseSource(nameof(SupportedLanguages))]
    public void EveryLanguage_HasATemplateHeaderForEveryTemplateColumn(string language)
    {
        _catalog.IsSupportedLanguage(language).ShouldBeTrue($"'{language}' is missing in client-import-header-synonyms.json");

        foreach (var target in ClientImportTemplateLayout.Targets)
        {
            _catalog.TemplateHeader(language, target).ShouldNotBeNullOrWhiteSpace($"{language}: no header for {target}");
        }
    }

    [TestCaseSource(nameof(SupportedLanguages))]
    public void EveryLanguage_HasFemaleAndMaleWords(string language)
    {
        var genders = _catalog.GenderValues.First(g => string.Equals(g.Key, language, StringComparison.OrdinalIgnoreCase)).Value;

        genders.ShouldContainKey(GenderEnum.Female);
        genders.ShouldContainKey(GenderEnum.Male);
        genders.ShouldContainKey(GenderEnum.Intersexuality);
    }

    [Test]
    public void NoNormalizedHeaderSynonym_MapsToTwoTargets()
    {
        var collisions = _catalog.HeaderSynonyms
            .SelectMany(language => language.Value.SelectMany(target => target.Value.Select(synonym =>
                (Language: language.Key, Target: target.Key, Key: ClientImportTextNormalizer.Normalize(synonym)))))
            .GroupBy(entry => entry.Key)
            .Where(group => group.Select(e => e.Target).Distinct().Count() > 1)
            .Select(group => $"'{group.Key}': " + string.Join(", ", group.Select(e => $"{e.Language}/{e.Target}")))
            .ToList();

        collisions.ShouldBeEmpty();
    }

    [Test]
    public void NoNormalizedGenderWord_MapsToTwoGenders()
    {
        var collisions = _catalog.GenderValues
            .SelectMany(language => language.Value.SelectMany(gender => gender.Value.Select(word =>
                (Language: language.Key, Gender: gender.Key, Key: ClientImportTextNormalizer.Normalize(word)))))
            .GroupBy(entry => entry.Key)
            .Where(group => group.Select(e => e.Gender).Distinct().Count() > 1)
            .Select(group => $"'{group.Key}': " + string.Join(", ", group.Select(e => $"{e.Language}/{e.Gender}")))
            .ToList();

        collisions.ShouldBeEmpty();
    }

    [Test]
    public void NoSynonym_NormalizesToEmpty()
    {
        var empty = _catalog.HeaderSynonyms
            .SelectMany(language => language.Value.SelectMany(target => target.Value.Select(synonym => (language.Key, synonym))))
            .Where(entry => ClientImportTextNormalizer.Normalize(entry.synonym).Length == 0)
            .ToList();

        empty.ShouldBeEmpty();
    }

    [TestCase("E-Mail", "email")]
    [TestCase("ＥＭＡＩＬ", "email")]
    [TestCase("Straße ", "straße")]
    [TestCase("Prénom", "prenom")]
    [TestCase("Geb.-Datum", "gebdatum")]
    [TestCase("郵便番号", "郵便番号")]
    public void Normalizer_FoldsSpellingVariants(string input, string expected)
    {
        ClientImportTextNormalizer.Normalize(input).ShouldBe(expected);
    }

    [TestCase("Herr", GenderEnum.Male)]
    [TestCase("Frau Dr.", GenderEnum.Female)]
    [TestCase("Mme", GenderEnum.Female)]
    [TestCase("M.", GenderEnum.Male)]
    [TestCase("Sig.ra", GenderEnum.Female)]
    [TestCase("女性", GenderEnum.Female)]
    [TestCase("divers", GenderEnum.Intersexuality)]
    public void ResolveGender_ReadsSalutationsAndGenderWords(string value, GenderEnum expected)
    {
        _catalog.ResolveGender(value).ShouldBe(expected);
    }

    [Test]
    public void ResolveGender_UnknownWord_IsNull()
    {
        _catalog.ResolveGender("Dr.").ShouldBeNull();
    }
}
