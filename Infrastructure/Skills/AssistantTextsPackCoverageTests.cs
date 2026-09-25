// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Every installed language pack must ship assistant-texts.json with every required key and every
/// required placeholder. These sentences reach the user without a model call, so a missing key is not a
/// degraded translation - it is either an English question in a non-English installation (which the
/// one-language rule forbids) or no question at all. Both are shipping defects, and this is the only
/// place they are visible before production.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class AssistantTextsPackCoverageTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string PluginsDirectory = "Plugins";
    private const string LanguagesDirectory = "Languages";
    private const int ExpectedPluginPacks = 21;
    private const string CompletionClaimsProperty = "completionClaims";
    private const string LetterRunPattern = @"\p{L}+";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [TearDown]
    public void ResetConfiguredTexts()
    {
        GracefulCorrectionTexts.Reset();
        ClarificationTexts.Reset();
        EscalationHandoffTexts.Reset();
        MessengerProactiveTexts.Reset();
    }

    /// <summary>
    /// The Klacks.Api project directory, i.e. the base directory the loader itself is given at startup.
    /// </summary>
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
            $"Could not locate {ApiProjectDirectory}/{PluginsDirectory}/{LanguagesDirectory} by walking up " +
            "from the test base directory.");
    }

    private static string PluginRoot() =>
        Path.Combine(ApiRoot(), PluginsDirectory, LanguagesDirectory);

    private static IEnumerable<string> PackDirectories() =>
        Directory.GetDirectories(PluginRoot())
            .Where(dir => File.Exists(Path.Combine(dir, LanguagePluginConstants.ManifestFileName)))
            .Where(dir => !LanguagePluginConstants.CoreLanguages.Contains(Path.GetFileName(dir)));

    [Test]
    public void EveryPack_ShipsTheAssistantTextsFile()
    {
        var missing = PackDirectories()
            .Where(dir => !File.Exists(Path.Combine(dir, LanguagePluginConstants.AssistantTextsFileName)))
            .Select(Path.GetFileName)
            .ToList();

        missing.ShouldBeEmpty(
            $"language packs without {LanguagePluginConstants.AssistantTextsFileName}: {string.Join(", ", missing)}");
    }

    [Test]
    public void EveryPack_CoversEveryRequiredKey_WithEveryPlaceholder()
    {
        var problems = new List<string>();

        foreach (var dir in PackDirectories())
        {
            var code = Path.GetFileName(dir);
            var file = Path.Combine(dir, LanguagePluginConstants.AssistantTextsFileName);
            if (!File.Exists(file))
            {
                continue;
            }

            var texts = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file), JsonOptions)
                        ?? new Dictionary<string, string>();

            foreach (var key in GracefulCorrectionTexts.RequiredKeys)
            {
                if (!texts.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
                {
                    problems.Add($"{code}: missing '{key}'");
                    continue;
                }

                foreach (var placeholder in GracefulCorrectionTexts.PlaceholdersFor(key))
                {
                    if (!text.Contains(placeholder, StringComparison.Ordinal))
                    {
                        problems.Add($"{code}: '{key}' has no {placeholder}");
                    }
                }
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void TheNumberOfPacks_IsStillTwentyOne()
    {
        PackDirectories().Count().ShouldBe(ExpectedPluginPacks);
    }

    // Closes the loop the two tests above only cover either side of: the files carry the key, and the
    // catalogue honours a configured pack - but only the loader joins them, and the one-language rule
    // stands or falls on that join. A pack that fails to load leaves its language resolving to the
    // English core text, which is exactly what rule 4 forbids.
    [Test]
    public void TheStartupLoader_ResolvesEveryPacksQuestionInItsOwnLanguage()
    {
        var failures = new List<string>();
        var english = GracefulCorrectionTexts.VariantsOf(
            GracefulCorrectionTexts.ClarificationQuestion)[LanguageConfig.DefaultLanguageFallback];

        AssistantTextsPluginLoader.Load(ApiRoot(), (file, ex) => failures.Add($"{file}: {ex.Message}"));

        foreach (var dir in PackDirectories())
        {
            var code = Path.GetFileName(dir);
            if (!GracefulCorrectionTexts.TryGetText(
                    GracefulCorrectionTexts.ClarificationQuestion, code, out var text))
            {
                failures.Add($"{code}: the loaded catalogue has no clarification question");
                continue;
            }

            if (string.Equals(text, english, StringComparison.Ordinal))
            {
                failures.Add($"{code}: resolved to the English core text");
            }
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    // Same closing of the load-then-resolve gap as the test above, for the empty-answer fallback notice
    // rather than the clarification question. Kept as its own test rather than folded into a loop over
    // RequiredKeys: the plain Yes/No button labels legitimately equal their English core text in some
    // packs (e.g. Spanish "No"), so that generic loop would misreport a correctly-shared word as an
    // unloaded pack.
    [Test]
    public void TheStartupLoader_ResolvesEveryPacksEmptyAnswerNoticeInItsOwnLanguage()
    {
        var failures = new List<string>();
        var english = GracefulCorrectionTexts.VariantsOf(
            GracefulCorrectionTexts.EmptyAnswerFallbackNotice)[LanguageConfig.DefaultLanguageFallback];

        AssistantTextsPluginLoader.Load(ApiRoot(), (file, ex) => failures.Add($"{file}: {ex.Message}"));

        foreach (var dir in PackDirectories())
        {
            var code = Path.GetFileName(dir);
            if (!GracefulCorrectionTexts.TryGetText(
                    GracefulCorrectionTexts.EmptyAnswerFallbackNotice, code, out var text))
            {
                failures.Add($"{code}: the loaded catalogue has no empty-answer fallback notice");
                continue;
            }

            if (string.Equals(text, english, StringComparison.Ordinal))
            {
                failures.Add($"{code}: resolved to the English core text");
            }
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Test]
    public void TheStartupLoader_ResolvesEveryPacksNoActionNoticeInItsOwnLanguage()
    {
        var failures = new List<string>();
        var english = GracefulCorrectionTexts.VariantsOf(
            GracefulCorrectionTexts.EmptyAnswerNoActionNotice)[LanguageConfig.DefaultLanguageFallback];

        AssistantTextsPluginLoader.Load(ApiRoot(), (file, ex) => failures.Add($"{file}: {ex.Message}"));

        foreach (var dir in PackDirectories())
        {
            var code = Path.GetFileName(dir);
            if (!GracefulCorrectionTexts.TryGetText(
                    GracefulCorrectionTexts.EmptyAnswerNoActionNotice, code, out var text))
            {
                failures.Add($"{code}: the loaded catalogue has no empty-answer no-action notice");
                continue;
            }

            if (string.Equals(text, english, StringComparison.Ordinal))
            {
                failures.Add($"{code}: resolved to the English core text");
            }
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    // The no-action notice ends a turn in which nothing ran. If it read as a completion claim - to the core
    // detector or to any pack's completion-claim.json, all of which are loaded together at startup - the
    // streaming path would append the separate no-action correction below it and the non-streaming path
    // would treat it as the false claim it is meant to replace.
    [Test]
    public void TheNoActionNotice_ReadsAsACompletionClaimInNoLanguage()
    {
        var packEntries = PluginPhraseMatcher.Merge([], PackDirectories()
            .Select(dir => Path.Combine(dir, LanguagePluginConstants.CompletionClaimFileName))
            .Where(File.Exists)
            .SelectMany(file => JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(file), JsonOptions)
                ?.GetValueOrDefault(CompletionClaimsProperty) ?? []));
        packEntries.ShouldNotBeEmpty();

        var notices = GracefulCorrectionTexts.VariantsOf(GracefulCorrectionTexts.EmptyAnswerNoActionNotice)
            .Select(pair => (Code: pair.Key, Text: pair.Value))
            .Concat(PackDirectories().Select(dir => (
                Code: Path.GetFileName(dir),
                Text: JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(Path.Combine(dir, LanguagePluginConstants.AssistantTextsFileName)), JsonOptions)!
                    [GracefulCorrectionTexts.EmptyAnswerNoActionNotice])))
            .ToList();
        notices.Count.ShouldBe(GracefulCorrectionTexts.CoreLanguages.Count + ExpectedPluginPacks);

        var claims = notices
            .Where(notice => CompletionClaimDetector.ClaimsCompletion(notice.Text)
                || PluginPhraseMatcher.MatchesAny(notice.Text.ToLowerInvariant(), Tokens(notice.Text), packEntries))
            .Select(notice => $"{notice.Code}: {notice.Text}")
            .ToList();

        claims.ShouldBeEmpty(string.Join(Environment.NewLine, claims));
    }

    private static IReadOnlyCollection<string> Tokens(string text) =>
        Regex.Matches(text, LetterRunPattern).Select(match => match.Value.ToLowerInvariant()).ToList();
}
