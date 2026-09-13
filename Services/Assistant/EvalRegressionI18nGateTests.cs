// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards that both eval sentences exist wherever they can be rendered from: the regression alert's own
/// key and the three eval figures appended to the weekly learning digest, in the four core catalogues of
/// Klacks.Ui and in every language plugin that already carries the digest key. Follows
/// TargetHoursDriftSummaryI18nGateTests: the Klacks.Ui files live in a different repository and the
/// backend CI job does not check them out, so that half reports itself inconclusive when they are
/// unreachable instead of failing a job that has nothing to do with the frontend. The plugin half has no
/// such excuse - those files ship inside Klacks.Api. The last test closes the loop in the other
/// direction: a catalogue interpolating a placeholder no event supplies would render the raw braces.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class EvalRegressionI18nGateTests
{
    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";
    private const string PluginLanguagesRelativePath = "Klacks.Api/Plugins/Languages";
    private const string TranslationsFileName = "translations.json";

    /// <summary>
    /// How many language plugins carry the weekly digest key today. Pinned rather than compared against
    /// zero so that a catalogue silently dropping out of the scan is a failure: without it, a rename of
    /// the key or of the folder layout would leave the gate green while checking nothing. Raise it
    /// deliberately together with a new language pack.
    /// </summary>
    private const int ExpectedPluginCatalogues = 21;

    [Test]
    public void BothSentencesExistInAllFourCoreCatalogues()
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
            AssertSentences(path);
        }
    }

    [Test]
    public void EveryPluginCatalogueCarryingTheDigestKeyAlsoCarriesBothSentences()
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
            if (!document.RootElement.TryGetProperty(ProactiveMessageI18nKeys.KlacksyLearnedDigest, out _))
            {
                continue;
            }

            AssertSentences(path);
            checkedCatalogues++;
        }

        checkedCatalogues.ShouldBe(
            ExpectedPluginCatalogues,
            "Another number of language plugins carries the weekly digest key than this gate scans.");
    }

    [Test]
    public void TheEventsSupplyExactlyThePlaceholdersTheCataloguesInterpolate()
    {
        var alert = new EvalRegressionTriggerEvent(
            Guid.NewGuid(), TurnEvalDefaults.DefaultGoldset, "deepseek-v4-pro", 0.70, 0.60, 0.10, 0.00);
        var digest = new KlacksyLearnedDigestTriggerEvent(
            new DateOnly(2026, 9, 7), 1, 0, 0, 0, 0.82, 0.61, 335);

        alert.SummaryParams.Keys.ShouldBe(EvalRegressionSummaryPlaceholders.AlertNames, ignoreOrder: true);

        foreach (var name in EvalRegressionSummaryPlaceholders.DigestNames)
        {
            digest.SummaryParams.ShouldContainKey(name);
        }
    }

    private static void AssertSentences(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        document.RootElement
            .TryGetProperty(ProactiveMessageI18nKeys.EvalRegression, out var alert)
            .ShouldBeTrue($"'{ProactiveMessageI18nKeys.EvalRegression}' is missing from '{path}'.");
        AssertPlaceholders(alert.GetString(), EvalRegressionSummaryPlaceholders.AlertNames, path);

        document.RootElement
            .TryGetProperty(ProactiveMessageI18nKeys.KlacksyLearnedDigest, out var digest)
            .ShouldBeTrue($"'{ProactiveMessageI18nKeys.KlacksyLearnedDigest}' is missing from '{path}'.");
        AssertPlaceholders(digest.GetString(), EvalRegressionSummaryPlaceholders.DigestNames, path);
    }

    private static void AssertPlaceholders(string? sentence, IReadOnlyList<string> names, string path)
    {
        sentence.ShouldNotBeNullOrEmpty($"A sentence of this gate is empty in '{path}'.");

        foreach (var name in names)
        {
            var placeholder = EvalRegressionSummaryPlaceholders.Placeholder(name);
            sentence.ShouldContain(placeholder, customMessage: $"'{placeholder}' is missing from '{path}'.");
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
