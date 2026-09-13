// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Architecture guard that every language Klacks ships - the core languages plus every installed
/// language pack under Klacks.Api/Plugins/Languages - has its own answer-language directive and its
/// own relative day words. The list of codes is derived from the shipped packs rather than restated
/// here, so a newly added pack fails this test until both are supplied instead of silently answering
/// its users in English. Resolution, not key presence, is asserted: a directive keyed "zh-CN" is
/// worthless if the lookup strips the region before reading it. Relative day words are asserted the
/// way they are resolved at runtime - per language - and the flat inventory is asserted to hold
/// exactly the words the per-language table holds, so the two views of the same data cannot drift.
/// </summary>

using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class LanguageDirectiveCoverageGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string LanguagePluginsRelativePath = "Plugins/Languages";
    private const string PluginManifestFileName = "manifest.json";
    private const int ExpectedLanguageCount = 25;
    private const string EnglishLanguage = "en";

    private static readonly (string Word, string Language)[] HistoricalTodayWords =
    {
        ("today", "en"), ("heute", "de"), ("now", "en"), ("jetzt", "de"), ("sofort", "de"),
        ("ab sofort", "de"), ("ab heute", "de"), ("aujourd'hui", "fr"), ("oggi", "it")
    };

    private static readonly string EnglishDirective =
        LLMSystemPromptBuilder.ResolveLanguageDirective("en");

    [Test]
    public void EveryShippedLanguage_HasItsOwnDirective()
    {
        var missing = AllLanguageCodes()
            .Where(code => !LLMSystemPromptBuilder.HasLanguageDirective(code))
            .ToList();

        missing.ShouldBeEmpty(
            $"Languages without an own answer-language directive: {string.Join(", ", missing)}");
    }

    [Test]
    public void EveryShippedLanguage_ResolvesToANonEnglishDirective_ExceptEnglishItself()
    {
        var codes = AllLanguageCodes();

        foreach (var code in codes.Where(c => !string.Equals(c, "en", StringComparison.OrdinalIgnoreCase)))
        {
            LLMSystemPromptBuilder.ResolveLanguageDirective(code)
                .ShouldNotBe(EnglishDirective, $"Language {code} falls back to the English directive.");
        }
    }

    [Test]
    public void AllShippedLanguages_AreTheExpectedTwentyFive()
    {
        AllLanguageCodes().Count.ShouldBe(ExpectedLanguageCount);
    }

    [Test]
    public void ChineseVariants_KeepTheirOwnDirective_InsteadOfDegradingThroughTheBaseTag()
    {
        var simplified = LLMSystemPromptBuilder.ResolveLanguageDirective("zh-CN");
        var traditional = LLMSystemPromptBuilder.ResolveLanguageDirective("zh-TW");

        simplified.ShouldNotBe(EnglishDirective);
        traditional.ShouldNotBe(EnglishDirective);
        simplified.ShouldNotBe(traditional);
        simplified.ShouldContain("简体中文");
        traditional.ShouldContain("繁體中文");
    }

    [Test]
    public void RegionalTagOfAKnownLanguage_ResolvesThroughItsBaseLanguage()
    {
        LLMSystemPromptBuilder.ResolveLanguageDirective("pt-BR")
            .ShouldBe(LLMSystemPromptBuilder.ResolveLanguageDirective("pt"));
    }

    [Test]
    public void UnknownLanguage_FallsBackToEnglish()
    {
        LLMSystemPromptBuilder.ResolveLanguageDirective("ru").ShouldBe(EnglishDirective);
    }

    [Test]
    public void EveryShippedLanguage_HasARelativeDayWordSet()
    {
        var wordCount = SkillRelativeDayWords.TodayWords.Length
                        + SkillRelativeDayWords.TomorrowWords.Length
                        + SkillRelativeDayWords.YesterdayWords.Length;

        wordCount.ShouldBeGreaterThanOrEqualTo(ExpectedLanguageCount * 3);
    }

    [Test]
    public void RelativeDayWordSets_AreDisjoint()
    {
        var today = Normalized(SkillRelativeDayWords.TodayWords);
        var tomorrow = Normalized(SkillRelativeDayWords.TomorrowWords);
        var yesterday = Normalized(SkillRelativeDayWords.YesterdayWords);

        today.Intersect(tomorrow).ShouldBeEmpty();
        today.Intersect(yesterday).ShouldBeEmpty();
        tomorrow.Intersect(yesterday).ShouldBeEmpty();
    }

    [Test]
    public void HistoricalTodayWords_AreStillAccepted_ForAUserOfTheirOwnLanguage()
    {
        foreach (var (word, language) in HistoricalTodayWords)
        {
            SkillRelativeDayWords.TryResolveDayOffset(word, language, out var offset)
                .ShouldBeTrue($"{word} ({language})");
            offset.ShouldBe(SkillRelativeDayWords.TodayOffset, $"{word} ({language})");
        }
    }

    [Test]
    public void HistoricalTodayWords_AreStillAccepted_WithoutALanguage()
    {
        foreach (var (word, _) in HistoricalTodayWords)
        {
            SkillRelativeDayWords.TryResolveDayOffset(word, null, out var offset).ShouldBeTrue(word);
            offset.ShouldBe(SkillRelativeDayWords.TodayOffset, word);
        }
    }

    [Test]
    public void EveryShippedLanguage_HasItsOwnEntryInThePerLanguageWordTable()
    {
        var withOwnWords = SkillRelativeDayWords.LanguagesWithOwnWords
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = AllLanguageCodes().Where(code => !withOwnWords.Contains(code)).ToList();

        missing.ShouldBeEmpty(
            "A language without its own relative day words resolves only English words for its users, " +
            $"because the union fallback is reserved for callers with no user at all. Missing: {string.Join(", ", missing)}");
    }

    [Test]
    public void EveryShippedLanguage_CanSayAllThreeDays_InItsOwnWords()
    {
        var english = SkillRelativeDayWords.WordsForLanguage(EnglishLanguage)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var code in AllLanguageCodes())
        {
            var ownWords = SkillRelativeDayWords.WordsForLanguage(code)
                .Where(word => string.Equals(code, EnglishLanguage, StringComparison.OrdinalIgnoreCase)
                               || !english.Contains(word));

            var offsets = new HashSet<int>();
            foreach (var word in ownWords)
            {
                SkillRelativeDayWords.TryResolveDayOffset(word, code, out var offset).ShouldBeTrue(word);
                offsets.Add(offset);
            }

            offsets.ShouldBe(
                new[]
                {
                    SkillRelativeDayWords.YesterdayOffset,
                    SkillRelativeDayWords.TodayOffset,
                    SkillRelativeDayWords.TomorrowOffset
                },
                ignoreOrder: true,
                $"Language '{code}' has no own word for one of the three days, so its users can only " +
                "write that day in English while the other two work - the kind of half-coverage the " +
                "union fallback used to hide.");
        }
    }

    [Test]
    public void FlatWordInventory_AndThePerLanguageTable_HoldTheSameWords()
    {
        var flat = Normalized(SkillRelativeDayWords.TodayWords
            .Concat(SkillRelativeDayWords.TomorrowWords)
            .Concat(SkillRelativeDayWords.YesterdayWords));

        var perLanguage = SkillRelativeDayWords.LanguagesWithOwnWords
            .SelectMany(language => SkillRelativeDayWords.WordsForLanguage(language))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        flat.Except(perLanguage).ShouldBeEmpty(
            "Every word in the flat inventory must be assigned to at least one language, otherwise it " +
            "is only reachable on the union fallback and no real user can ever write it.");
        perLanguage.Except(flat).ShouldBeEmpty(
            "Every word of the per-language table must appear in the flat inventory, which is what the " +
            "coverage count and the disjointness guard read.");
    }

    [Test]
    public void ACrossLanguageHomograph_ResolvesOnlyForItsOwnLanguage()
    {
        SkillRelativeDayWords.TryResolveDayOffset("hier", "fr", out var french).ShouldBeTrue();
        french.ShouldBe(SkillRelativeDayWords.YesterdayOffset);

        SkillRelativeDayWords.TryResolveDayOffset("hier", "de", out _).ShouldBeFalse(
            "German 'hier' means 'here' - resolving the French word for a German user silently books " +
            "the wrong day");
    }

    private static HashSet<string> Normalized(IEnumerable<string> words) =>
        words.Select(SkillRelativeDayWords.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static List<string> AllLanguageCodes()
    {
        var codes = new List<string>(MultiLanguage.CoreLanguages);
        var pluginRoot = Path.Combine(
            LocateApiProject(), LanguagePluginsRelativePath.Replace('/', Path.DirectorySeparatorChar));

        foreach (var directory in Directory.EnumerateDirectories(pluginRoot))
        {
            if (!File.Exists(Path.Combine(directory, PluginManifestFileName)))
            {
                continue;
            }

            var code = new DirectoryInfo(directory).Name;
            if (!codes.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                codes.Add(code);
            }
        }

        return codes;
    }

    private static string LocateApiProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory);
            if (Directory.Exists(Path.Combine(candidate, "Domain", "Services")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the {ApiProjectDirectory} project by walking up from the test base directory.");
    }
}
