// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the refused-booking toast of the schedule: every text key of a 400 refusal code (sealed day, outside the
/// membership or group period, work in a closed period, sealed work) must exist non-empty in en.json and, with the
/// same placeholders, in all four core catalogues of Klacks.Ui and in every language plugin. A second test keeps the
/// error codes of Klacks.Ui/src/app/domain/constants/work-conflict.constants.ts in step with WorkBookingRefusalCodes
/// and WorkWriteConflictCodes, so a code the backend sends never silently loses its translated sentence. Inconclusive
/// when Klacks.Ui is not checked out next to Klacks.Api, like PermissionConstantsUiMirrorGuardTests; the language plugins
/// belong to Klacks.Api and must exist. The CI job (Klacks.Api tests.yml, backend-tests) checks Klacks.Ui out at its main
/// HEAD with exactly the two folders read here (src/app/domain/constants, src/assets/i18n), so there the gate is binding
/// against the Ui main branch, not against a local Ui branch.
/// </summary>

using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class WorkRefusalToastI18nGateTests
{
    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private static readonly Dictionary<string, string[]> KeyPlaceholders = new()
    {
        ["schedule.workConflict.daySealed"] = ["date"],
        ["schedule.workConflict.outsideMembershipPeriod"] = ["client", "date", "period"],
        ["schedule.workConflict.outsideGroupPeriod"] = ["client", "date", "period"],
        ["schedule.workConflict.workInClosedPeriod"] = [],
        ["schedule.workConflict.workSealedRoleRequired"] = [],
        ["schedule.workConflict.periodFrom"] = ["from"],
        ["schedule.workConflict.periodUntil"] = ["until"],
    };

    private const string SourceLanguage = "en";
    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";
    private const string UiConstantsRelativePath = "Klacks.Ui/src/app/domain/constants/work-conflict.constants.ts";
    private const string PluginLanguagesRelativePath = "Klacks.Api/Plugins/Languages";
    private const string TranslationsFileName = "translations.json";
    private const string JsonExtension = ".json";
    private const string PlaceholderPattern = @"\{\{\s*([^{}\s]+)\s*\}\}";
    private const string FieldSuffix = "Field";
    private const int ExpectedPluginCatalogues = 21;

    private static readonly Regex Placeholder = new(PlaceholderPattern, RegexOptions.CultureInvariant);

    [Test]
    public void EnglishSource_CarriesEveryKeyWithItsPlaceholders()
    {
        var catalogueDirectory = RepositoryRootLocator.FindDirectory(UiCatalogueRelativePath);
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
            return;
        }

        var english = ReadFlat(Path.Combine(catalogueDirectory, SourceLanguage + JsonExtension));
        foreach (var (key, placeholders) in KeyPlaceholders)
        {
            english.ShouldContainKey(key);
            english[key].ShouldNotBeNullOrWhiteSpace();
            Placeholders(english[key]).ShouldBe(new HashSet<string>(placeholders), ignoreOrder: true, customMessage: key);
        }
    }

    [Test]
    public void EveryKey_ExistsWithTheSamePlaceholdersInAllCatalogues()
    {
        var catalogueDirectory = RepositoryRootLocator.FindDirectory(UiCatalogueRelativePath);
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
            return;
        }

        var pluginDirectory = RepositoryRootLocator.RequireDirectory(PluginLanguagesRelativePath);

        var english = ReadFlat(Path.Combine(catalogueDirectory, SourceLanguage + JsonExtension));
        var catalogues = CoreLanguages
            .Select(language => (Name: language, Path: Path.Combine(catalogueDirectory, language + JsonExtension)))
            .Concat(Directory.GetDirectories(pluginDirectory)
                .Select(directory => (Name: Path.GetFileName(directory), Path: Path.Combine(directory, TranslationsFileName)))
                .Where(entry => File.Exists(entry.Path)))
            .ToList();

        catalogues.Count.ShouldBe(CoreLanguages.Length + ExpectedPluginCatalogues);

        var problems = new List<string>();
        foreach (var catalogue in catalogues)
        {
            var texts = ReadFlat(catalogue.Path);
            foreach (var key in KeyPlaceholders.Keys)
            {
                if (!texts.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
                {
                    problems.Add($"{catalogue.Name}: missing {key}");
                    continue;
                }

                if (!Placeholders(text).SetEquals(Placeholders(english[key])))
                {
                    problems.Add($"{catalogue.Name}: placeholders differ in {key}");
                }
            }
        }

        problems.ShouldBeEmpty();
    }

    [Test]
    public void UiConstants_MustCarryEveryBackendRefusalAndConflictCode()
    {
        var path = RepositoryRootLocator.FindFile(UiConstantsRelativePath);
        if (path == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiConstantsRelativePath));
            return;
        }

        var source = File.ReadAllText(path);
        var codes = CodesOf(typeof(WorkBookingRefusalCodes)).Concat(CodesOf(typeof(WorkWriteConflictCodes))).ToList();
        codes.Count.ShouldBeGreaterThan(0);

        var missing = codes
            .Where(code => !source.Contains($"'{code}'", StringComparison.Ordinal))
            .ToList();

        missing.ShouldBeEmpty("work-conflict.constants.ts lacks a literal for these backend codes, so the toast cannot explain them.");
    }

    private static IEnumerable<string> CodesOf(Type constantsType) =>
        constantsType
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string) && !field.Name.EndsWith(FieldSuffix, StringComparison.Ordinal))
            .Select(field => (string)field.GetRawConstantValue()!);

    private static Dictionary<string, string> ReadFlat(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty);
    }

    private static HashSet<string> Placeholders(string text) =>
        Placeholder.Matches(text).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
}
