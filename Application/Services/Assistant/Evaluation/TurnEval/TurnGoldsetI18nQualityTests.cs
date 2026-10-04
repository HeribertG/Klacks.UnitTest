// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Quality gate of the translated goldset turn-selection-v1-i18n (spec 2026-09-27 section 3.5). Every id is
/// i18n-&lt;locale&gt;--&lt;sourceId&gt; naming a single-turn tool item of the default goldset, inherits that item's
/// expectation and partition (a translated holdout item stays holdout), uses an installed language-pack code or
/// a core language other than German, repeats no message of any other goldset, and every non-German language
/// reaches TurnGoldsetI18nQualityChecks.MinItemsPerLocale items together with the default goldset. A failure is
/// fixed by regenerating or dropping items, never by hand-editing a translation. A missing file fails the gate:
/// the file is a learning goldset the optimizer, the gate and the weekly eval rely on. The checks live in
/// TurnGoldsetI18nQualityChecks, exercised against fixtures by TurnGoldsetI18nQualityChecksTests.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Constants;
using NUnit.Framework;
using Shouldly;
using Klacks.UnitTest.TestHelpers;

[TestFixture]
public class TurnGoldsetI18nQualityTests
{
    private const string GoldsetFileExtension = ".json";
    private const string GeneratorHint = "scripts/generate-goldset-translations.ps1";
    private const string GoldsetFilePattern = "*.json";
    private const string ItemsProperty = "items";
    private const string MessageProperty = "message";

    private static readonly string[] ApiRelativePath = ["Klacks.Api"];
    private static readonly string[] GoldsetsRelativePath = ["Application", "Skills", "Goldsets"];
    private static readonly string[] LanguagePacksRelativePath = ["Plugins", "Languages"];

    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };

    private List<TurnGoldsetItem> _translations = null!;
    private List<TurnGoldsetItem> _baseItems = null!;
    private Dictionary<string, TurnGoldsetItem> _baseById = null!;
    private List<string> _languagePacks = null!;
    private List<string> _otherGoldsetMessages = null!;

    [OneTimeSetUp]
    public void LoadFiles()
    {
        var apiRoot = LocateRepoDirectory(ApiRelativePath);
        var goldsetDirectory = Path.Combine([apiRoot, .. GoldsetsRelativePath]);
        var i18nFileName = TurnEvalDefaults.I18nGoldset + GoldsetFileExtension;
        var i18nPath = Path.Combine(goldsetDirectory, i18nFileName);

        if (!File.Exists(i18nPath))
        {
            Assert.Fail($"{i18nFileName} is missing although it is a learning goldset (TurnEvalDefaults.LearningGoldsets); regenerate it with {GeneratorHint}.");
        }

        _translations = Load(i18nPath).Items;
        _baseItems = Load(Path.Combine(goldsetDirectory, TurnEvalDefaults.DefaultGoldset + GoldsetFileExtension)).Items;
        _baseById = _baseItems.ToDictionary(item => item.Id, StringComparer.Ordinal);
        _languagePacks = [.. Directory.GetDirectories(Path.Combine([apiRoot, .. LanguagePacksRelativePath]))
            .Select(Path.GetFileName)
            .OfType<string>()];
        _otherGoldsetMessages = [.. Directory.GetFiles(goldsetDirectory, GoldsetFilePattern)
            .Where(path => !string.Equals(Path.GetFileName(path), i18nFileName, StringComparison.Ordinal))
            .SelectMany(ReadMessages)];
    }

    [Test]
    public void TheFile_IsNotEmpty()
    {
        _translations.ShouldNotBeEmpty();
    }

    [Test]
    public void EveryId_IsUniqueWellFormedAndInheritsThePartitionOfItsSource()
    {
        TurnGoldsetI18nQualityChecks.ValidateIdsSourcesAndPartitions(_translations, _baseById).ShouldBeEmpty();
    }

    [Test]
    public void EveryTranslation_InheritsTheExpectationOfItsSource()
    {
        TurnGoldsetI18nQualityChecks.ValidateInheritance(_translations, _baseById).ShouldBeEmpty();
    }

    [Test]
    public void EveryLocale_IsAnInstalledLanguagePackOrANonGermanCoreLanguage()
    {
        TurnGoldsetI18nQualityChecks.ValidateLocales(_translations, _languagePacks).ShouldBeEmpty();
    }

    [Test]
    public void EveryNonGermanLanguage_ReachesTheMinimumItemCount()
    {
        TurnGoldsetI18nQualityChecks.FindLocalesBelowMinimum(_translations, _baseItems, _languagePacks).ShouldBeEmpty();
    }

    [Test]
    public void NoTranslation_RepeatsAnotherGoldsetMessageOrAnotherTranslation()
    {
        TurnGoldsetI18nQualityChecks.FindMessagesRepeatingAnotherMessage(_translations, _otherGoldsetMessages).ShouldBeEmpty();
    }

    private static TurnGoldsetDocument Load(string path) =>
        JsonSerializer.Deserialize<TurnGoldsetDocument>(File.ReadAllText(path), SerializerOptions)
            .ShouldNotBeNull(path);

    private static IEnumerable<string> ReadMessages(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(ItemsProperty, out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. items.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty(MessageProperty, out var message)
                && message.ValueKind == JsonValueKind.String)
            .Select(item => item.GetProperty(MessageProperty).GetString()!)];
    }

    private static string LocateRepoDirectory(string[] relativePath)
    {
        RepositoryRootLocator.RequireDirectory([.. relativePath, .. GoldsetsRelativePath]);
        return RepositoryRootLocator.RequireDirectory(relativePath);
    }
}
