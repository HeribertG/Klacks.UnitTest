// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Every installed language pack must ship assistant-texts.json with every required key and every
/// required placeholder. These sentences reach the user without a model call, so a missing key is not a
/// degraded translation - it is either an English question in a non-English installation (which the
/// one-language rule forbids) or no question at all. Both are shipping defects, and this is the only
/// place they are visible before production.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Constants;
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

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [TearDown]
    public void ResetConfiguredTexts() => GracefulCorrectionTexts.Reset();

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

                foreach (var placeholder in GracefulCorrectionTexts.RequiredPlaceholders)
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
}
