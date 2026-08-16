// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the server-side messenger text catalogue. Its sentences are verbatim duplicates of the
/// frontend translation files, so the real risk is drift: somebody rewords a message in
/// Klacks.Ui/src/assets/i18n and the messenger keeps sending the old wording forever.
/// The drift test reads those JSON files directly. They live in a different repository, and the
/// backend-tests CI job checks out Klacks.Api and Klacks.UnitTest but NOT Klacks.Ui, so the test
/// marks itself inconclusive when the files are absent instead of failing a job that has nothing
/// to do with the frontend. It therefore fires exactly where the edit happens: on a developer
/// machine with the full working tree.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class MessengerProactiveTextsTests
{
    private static readonly string[] Languages = ["de", "en", "fr", "it"];
    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";

    /// <summary>
    /// Deliberately maintained here and not in production code: there is no programmatic link
    /// between a trigger kind and the i18n key its event emits. Adding a kind to
    /// MessengerWakeUpPolicy without listing it here fails the test below, which is the point.
    /// </summary>
    private static readonly Dictionary<string, string> KeyPerWakeUpKind = new(StringComparer.OrdinalIgnoreCase)
    {
        [AgentTriggerKinds.UnstaffedShift] = ProactiveMessageI18nKeys.UnstaffedShift,
        [AgentTriggerKinds.WorkDroppedByErpImport] = ProactiveMessageI18nKeys.WorkDroppedByErpImport,
        [AgentTriggerKinds.OrderImportFailed] = ProactiveMessageI18nKeys.OrderImportFailed
    };

    [Test]
    public void CatalogueCoversExactlyTheKindsThatMayWakeSomebody()
    {
        Assert.That(KeyPerWakeUpKind.Keys, Is.EquivalentTo(MessengerWakeUpPolicy.WakeUpTriggerKinds),
            "A kind that may wake somebody but has no known i18n key would go out as a raw key.");
        Assert.That(MessengerProactiveTexts.CoveredKeys, Is.EquivalentTo(KeyPerWakeUpKind.Values),
            "Every key a wake-worthy trigger can emit needs a sentence here, and nothing else belongs in it.");
    }

    [Test]
    public void EveryCoveredKeyCarriesAllFourBaseLanguages()
    {
        foreach (var key in MessengerProactiveTexts.CoveredKeys)
        {
            foreach (var language in Languages)
            {
                Assert.That(MessengerProactiveTexts.TryGetText(key, language, out var text), Is.True,
                    $"'{key}' has no text for '{language}'.");
                Assert.That(text, Is.Not.Empty);
            }
        }
    }

    [Test]
    public void UnknownKeyIsReportedAsMissingRatherThanGuessed()
    {
        Assert.That(MessengerProactiveTexts.TryGetText(ProactiveMessageI18nKeys.MuteSuggestion, "de", out _), Is.False);
    }

    [Test]
    public void CatalogueMatchesTheFrontendTranslationFiles()
    {
        var catalogueDirectory = FindUiCatalogueDirectory();
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive(
                $"'{UiCatalogueRelativePath}' is not reachable from this working tree, so the duplicate cannot be compared here.");
            return;
        }

        foreach (var language in Languages)
        {
            var path = Path.Combine(catalogueDirectory, language + ".json");
            Assert.That(File.Exists(path), Is.True, $"Missing frontend catalogue '{path}'.");

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var key in MessengerProactiveTexts.CoveredKeys)
            {
                Assert.That(document.RootElement.TryGetProperty(key, out var uiValue), Is.True,
                    $"'{key}' is missing from '{path}'.");
                Assert.That(MessengerProactiveTexts.TryGetText(key, language, out var serverText), Is.True);
                Assert.That(serverText, Is.EqualTo(uiValue.GetString()),
                    $"'{key}' ({language}) drifted apart. The frontend says \"{uiValue.GetString()}\", " +
                    "the server-side messenger catalogue in MessengerProactiveTexts.cs says something else. " +
                    "Copy the frontend wording over, otherwise inbox and messenger contradict each other.");
            }
        }
    }

    private static string? FindUiCatalogueDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, UiCatalogueRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
