// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards that every kind an administrator can see in the proactive governance rule table carries a
/// label. The table lists ProactiveGovernanceDefaults.GovernedKinds (ProactiveGovernanceResolver builds
/// its rows from that list, not from the database), and the frontend renders
/// "setting.proactiveGovernance.kind.&lt;kind&gt;" for each of them - a missing entry shows the raw key,
/// which is how ungrouped_workforce, no_schedule_yet and eval_regression reached the settings card as
/// technical identifiers.
///
/// Reads the catalogues themselves rather than duplicating the kind list in TypeScript: the list only
/// exists in C#, and a jest spec would have to restate it. Follows MessengerProactiveTextsTests and
/// EvalRegressionI18nGateTests in walking up to Klacks.Ui and reporting itself inconclusive when that
/// repository is not checked out, because the backend CI job does not check it out and must not fail
/// over it. The language plugins ship inside Klacks.Api and have no such excuse.
///
/// Presence only, deliberately: what a label SAYS is a translation question, and asserting wording here
/// would turn every reworded label into a failing backend test.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class ProactiveGovernanceKindLabelI18nGateTests
{
    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";
    private const string PluginLanguagesRelativePath = "Klacks.Api/Plugins/Languages";
    private const string TranslationsFileName = "translations.json";
    private const string KindLabelKeyPrefix = "setting.proactiveGovernance.kind.";

    /// <summary>
    /// How many language plugins ship a governance kind label today. Pinned rather than compared against
    /// zero so that a catalogue silently dropping out of the scan is a failure rather than a vacuous
    /// pass. Raise it deliberately together with a new language pack.
    /// </summary>
    private const int ExpectedPluginCatalogues = 21;

    [Test]
    public void EveryGovernedKindHasALabelInAllFourCoreCatalogues()
    {
        var catalogueDirectory = FindDirectory(UiCatalogueRelativePath);
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive($"'{UiCatalogueRelativePath}' is not reachable from this working tree.");
            return;
        }

        foreach (var language in CoreLanguages)
        {
            var path = Path.Combine(catalogueDirectory, language + ".json");
            File.Exists(path).ShouldBeTrue($"Missing frontend catalogue '{path}'.");
            AssertEveryKindLabel(path);
        }
    }

    [Test]
    public void EveryPluginCatalogueCarryingKindLabelsCarriesOneForEveryGovernedKind()
    {
        var languagesDirectory = FindDirectory(PluginLanguagesRelativePath);
        if (languagesDirectory == null)
        {
            Assert.Inconclusive($"'{PluginLanguagesRelativePath}' is not reachable from this working tree.");
            return;
        }

        var checkedCatalogues = 0;

        foreach (var directory in Directory.GetDirectories(languagesDirectory))
        {
            var path = Path.Combine(directory, TranslationsFileName);
            if (!File.Exists(path))
            {
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty(
                KindLabelKeyPrefix + AgentTriggerKinds.UnstaffedShift, out _))
            {
                continue;
            }

            AssertEveryKindLabel(path);
            checkedCatalogues++;
        }

        checkedCatalogues.ShouldBe(
            ExpectedPluginCatalogues,
            "Another number of language plugins carries governance kind labels than this gate scans.");
    }

    private static void AssertEveryKindLabel(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        foreach (var kind in ProactiveGovernanceDefaults.GovernedKinds)
        {
            var key = KindLabelKeyPrefix + kind;

            document.RootElement
                .TryGetProperty(key, out var label)
                .ShouldBeTrue(
                    $"'{key}' is missing from '{path}', so the governance rule table renders the raw "
                    + "trigger identifier instead of a label.");

            label.GetString().ShouldNotBeNullOrWhiteSpace($"'{key}' is empty in '{path}'.");
        }
    }

    private static string? FindDirectory(string relativePath)
    {
        var segments = relativePath.Split('/');
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

        while (directory != null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
