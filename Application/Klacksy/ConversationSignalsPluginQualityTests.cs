// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Quality gate for the per-language conversation-signals.json plugin files. Guarantees that
/// every installed plugin language ships a parseable file with all five signal lists populated
/// and normalized (trimmed, lowercase, unique), so AffirmationDetector, ImplicitCorrectionDetector,
/// RecipeCancellationDetector and SkillGapDetector work in every supported language and a recipe
/// confirmation can never be silently cancelled because a language lacks affirmation entries.
/// </summary>

using System.Text.Json;

namespace Klacks.UnitTest.Application.Klacksy;

[TestFixture]
public class ConversationSignalsPluginQualityTests
{
    private const string ConversationSignalsFileName = "conversation-signals.json";

    private static readonly string[] PluginsLanguagesRelativePath =
    {
        "Klacks.Api", "Plugins", "Languages"
    };

    private static readonly string[] RequiredSignalKeys =
    {
        "affirmations", "negations", "corrections", "cancellations", "gapIndicators"
    };

    [Test]
    public void EveryPluginLanguage_HasConversationSignalsFile()
    {
        var languagesDir = LocatePluginsLanguagesDir();

        var missing = Directory.GetDirectories(languagesDir)
            .Where(dir => !File.Exists(Path.Combine(dir, ConversationSignalsFileName)))
            .Select(dir => new DirectoryInfo(dir).Name)
            .ToList();

        missing.ShouldBeEmpty(
            $"every plugin language needs a {ConversationSignalsFileName}, otherwise confirmations " +
            $"in that language are not understood and recipes get cancelled on a valid 'yes'");
    }

    [Test]
    public void EveryConversationSignalsFile_HasAllListsPopulatedAndNormalized()
    {
        var languagesDir = LocatePluginsLanguagesDir();
        var violations = new List<string>();

        foreach (var languageDir in Directory.GetDirectories(languagesDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var code = new DirectoryInfo(languageDir).Name;
            var file = Path.Combine(languageDir, ConversationSignalsFileName);
            if (!File.Exists(file))
            {
                continue;
            }

            Dictionary<string, List<string>>? map;
            try
            {
                map = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(file));
            }
            catch (JsonException ex)
            {
                violations.Add($"{code}: file is not valid JSON ({ex.Message})");
                continue;
            }

            if (map == null)
            {
                violations.Add($"{code}: file deserialized to null");
                continue;
            }

            foreach (var key in RequiredSignalKeys)
            {
                if (!map.TryGetValue(key, out var entries) || entries.Count == 0)
                {
                    violations.Add($"{code}: list '{key}' is missing or empty");
                    continue;
                }

                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry))
                    {
                        violations.Add($"{code}/{key}: contains an empty entry");
                    }
                    else if (entry != entry.Trim().ToLowerInvariant())
                    {
                        violations.Add($"{code}/{key}: entry '{entry}' is not trimmed lowercase");
                    }
                }

                if (entries.Distinct(StringComparer.Ordinal).Count() != entries.Count)
                {
                    violations.Add($"{code}/{key}: contains duplicate entries");
                }
            }
        }

        violations.ShouldBeEmpty(string.Join("\n", violations));
    }

    private static string LocatePluginsLanguagesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(PluginsLanguagesRelativePath);
            var candidate = Path.Combine(segments.ToArray());
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Plugins/Languages directory not found from test base directory");
    }
}
