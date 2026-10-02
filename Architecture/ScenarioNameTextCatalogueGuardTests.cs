// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the prefixes of server-created scenario names (ScenarioNameTexts) in all 25 languages. The packs are
/// loaded through the real startup loader, so a pack whose file carries the keys but is never fed into the
/// catalogue fails here too. Every ScenarioNameKind maps to exactly one required key; every language resolves
/// every key in its own right (never through the English fallback); the distinctive words (proposal, absence
/// cover) differ from English in every other language, which catches a pack that was copied instead of
/// translated; and no prefix carries braces, line breaks or surrounding blanks, because it is glued verbatim in
/// front of the dates.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ScenarioNameTextCatalogueGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string PluginsDirectory = "Plugins";
    private const string LanguagesDirectory = "Languages";
    private const int ExpectedPluginPacks = 21;
    private const int ExpectedLanguages = 25;
    private const int MaxPrefixLength = 60;
    private const string GermanRegional = "de-CH";
    private const string German = "de";
    private const string PortugueseRegional = "pt-BR";
    private const string Portuguese = "pt";
    private const string ChineseSimplified = "zh-CN";
    private const string ChineseTraditional = "zh-TW";
    private const string UnknownLanguage = "xx-XX";
    private static readonly char[] ForbiddenCharacters = ['{', '}', '\n', '\r', '\t'];

    private static readonly IReadOnlyList<string> KeysThatMustDifferFromEnglish =
    [
        ScenarioNameTexts.Proposal, ScenarioNameTexts.AbsenceCover
    ];

    [SetUp]
    public void LoadThePacks()
    {
        AssistantTextCatalogues.ResetAll();
        var failures = new List<string>();
        AssistantTextsPluginLoader.Load(ApiRoot(), (file, ex) => failures.Add($"{file}: {ex.Message}"));
        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [TearDown]
    public void ResetConfiguredTexts() => AssistantTextCatalogues.ResetAll();

    private static string ApiRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory);
            if (Directory.Exists(Path.Combine(candidate, PluginsDirectory, LanguagesDirectory)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate {ApiProjectDirectory}/{PluginsDirectory}/{LanguagesDirectory} by walking up from the test base directory.");
    }

    private static IReadOnlyList<string> PackLanguages() =>
        Directory.GetDirectories(Path.Combine(ApiRoot(), PluginsDirectory, LanguagesDirectory))
            .Where(dir => File.Exists(Path.Combine(dir, LanguagePluginConstants.ManifestFileName)))
            .Select(dir => Path.GetFileName(dir))
            .Where(code => !LanguagePluginConstants.CoreLanguages.Contains(code))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<string> AllLanguages() => ScenarioNameTexts.CoreLanguages.Concat(PackLanguages()).ToList();

    private static string OwnText(string key, string language)
    {
        ScenarioNameTexts.TryGetOwnText(key, language, out var text).ShouldBeTrue($"{language}: no own text for '{key}'");
        return text;
    }

    [Test]
    public void TheCatalogue_CoversAllTwentyFiveLanguages()
    {
        PackLanguages().Count.ShouldBe(ExpectedPluginPacks);
        AllLanguages().Count.ShouldBe(ExpectedLanguages);
    }

    [Test]
    public void EveryKind_MapsToExactlyOneRequiredKey_AndTheCoreTableCarriesExactlyThoseKeys()
    {
        var keys = Enum.GetValues<ScenarioNameKind>().Select(ScenarioNameTexts.KeyOf).ToList();

        keys.Distinct(StringComparer.Ordinal).Count().ShouldBe(keys.Count);
        keys.ShouldBe(ScenarioNameTexts.RequiredKeys, ignoreOrder: true);
        ScenarioNameTexts.Keys.ShouldBe(ScenarioNameTexts.RequiredKeys, ignoreOrder: true);
    }

    [Test]
    public void EveryLanguage_ResolvesEveryKeyInItsOwnRight_AsAUsablePrefix()
    {
        var problems = new List<string>();

        foreach (var language in AllLanguages())
        {
            foreach (var key in ScenarioNameTexts.RequiredKeys)
            {
                if (!ScenarioNameTexts.TryGetOwnText(key, language, out var text) || string.IsNullOrWhiteSpace(text))
                {
                    problems.Add($"{language}: no text for '{key}'");
                    continue;
                }

                if (text != text.Trim() || text.IndexOfAny(ForbiddenCharacters) >= 0 || text.Length > MaxPrefixLength)
                {
                    problems.Add($"{language}: '{key}' is not a usable one-line prefix: '{text}'");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void TheDistinctiveWords_DifferFromEnglish_InEveryOtherLanguage()
    {
        var problems = new List<string>();

        foreach (var language in AllLanguages().Where(code => code != LanguageConfig.DefaultLanguageFallback))
        {
            foreach (var key in KeysThatMustDifferFromEnglish)
            {
                if (string.Equals(OwnText(key, language), ScenarioNameTexts.EnglishOf(key), StringComparison.Ordinal))
                {
                    problems.Add($"{language}: '{key}' is still the English text");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void TheKinds_StayDistinguishable_WithinEveryLanguage()
    {
        var problems = new List<string>();

        foreach (var language in AllLanguages())
        {
            var duplicates = ScenarioNameTexts.RequiredKeys
                .GroupBy(key => OwnText(key, language), StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => $"{language}: '{group.Key}' is used by {string.Join(", ", group)}");
            problems.AddRange(duplicates);
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void RegionalTags_ReachTheirLanguage_AndTheTwoChinesePacksStaySeparate()
    {
        OwnText(ScenarioNameTexts.Proposal, GermanRegional).ShouldBe(OwnText(ScenarioNameTexts.Proposal, German));
        OwnText(ScenarioNameTexts.Proposal, PortugueseRegional).ShouldBe(OwnText(ScenarioNameTexts.Proposal, Portuguese));
        OwnText(ScenarioNameTexts.Plan, ChineseSimplified).ShouldNotBe(OwnText(ScenarioNameTexts.Plan, ChineseTraditional));
    }

    [Test]
    public void AnUnknownLanguage_ClaimsNothing()
    {
        ScenarioNameTexts.TryGetOwnText(ScenarioNameTexts.Proposal, UnknownLanguage, out var text).ShouldBeFalse();
        text.ShouldBeEmpty();
    }
}
