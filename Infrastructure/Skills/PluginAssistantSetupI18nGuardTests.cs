// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the assistantSetup contract of feature plugins. The UI shows the offer text with the accept and
/// decline buttons and, on accept, sends the trigger phrase as the user's chat message; the chat path
/// quotes the same phrase. So every plugin i18n file must carry all four keys (plus plugin-specific extra
/// keys), every language Klacks ships must have such a file, and the trigger phrase of each language must be
/// a synonym of exactly one skill of that plugin in that language (seed synonyms for de/en/fr/it, the
/// language pack for all others) and of no other skill - otherwise the deterministic keyword match routes
/// the accepted offer to the wrong skill, or to none.
/// </summary>

using System.Text.Json;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class PluginAssistantSetupI18nGuardTests
{
    private const string ManifestFileName = "manifest.json";
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string SkillSynonymsFileName = "skill-synonyms.json";
    private const string I18nDirectoryName = "i18n";
    private const string I18nFileSearchPattern = "*.json";
    private const string AssistantSetupProperty = "assistantSetup";
    private const string ProvidedSkillsProperty = "providedSkills";
    private const string SynonymsProperty = "synonyms";
    private const string NameProperty = "name";
    private const string SkillsProperty = "skills";

    private static readonly string[] AssistantSetupKeyProperties =
        ["offerKey", "acceptKey", "declineKey", "triggerPhraseKey"];

    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private static readonly Dictionary<string, string[]> ExtraRequiredKeys = new(StringComparer.Ordinal)
    {
        ["messaging"] = ["messaging.setup-assistant.offer-after-save"]
    };

    private static readonly string[] ApiRelativePath = ["Klacks.Api"];
    private static readonly string[] FeaturesRelativePath = ["Plugins", "Features"];
    private static readonly string[] LanguagesRelativePath = ["Plugins", "Languages"];
    private static readonly string[] CoreSeedRelativePath = ["Application", "Skills", "Definitions", SkillSeedsFileName];

    public static IEnumerable<string> PluginsWithAssistantSetup()
    {
        var featuresDir = Path.Combine([ApiRoot(), .. FeaturesRelativePath]);
        foreach (var pluginDir in Directory.GetDirectories(featuresDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var manifestFile = Path.Combine(pluginDir, ManifestFileName);
            if (!File.Exists(manifestFile))
            {
                continue;
            }

            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestFile));
            if (manifest.RootElement.TryGetProperty(AssistantSetupProperty, out var setup)
                && setup.ValueKind == JsonValueKind.Object)
            {
                yield return Path.GetFileName(pluginDir);
            }
        }
    }

    [Test]
    public void AtLeastOnePluginDeclaresAnAssistantSetup()
    {
        PluginsWithAssistantSetup().ShouldNotBeEmpty(
            "no feature plugin declares assistantSetup, so the checks below would pass without proving anything");
    }

    [TestCaseSource(nameof(PluginsWithAssistantSetup))]
    public void EveryShippedLanguage_HasAnI18nFileForThePlugin(string plugin)
    {
        var present = I18nFiles(plugin).Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
        var missing = ShippedLanguages().Where(language => !present.Contains(language)).ToList();

        missing.ShouldBeEmpty(
            $"plugin '{plugin}' declares assistantSetup but has no i18n file for: {string.Join(", ", missing)}");
    }

    [TestCaseSource(nameof(PluginsWithAssistantSetup))]
    public void EveryI18nFile_CarriesEveryAssistantSetupKey(string plugin)
    {
        var requiredKeys = AssistantSetupKeys(plugin).Values
            .Concat(ExtraRequiredKeys.GetValueOrDefault(plugin) ?? [])
            .ToList();
        var violations = new List<string>();

        foreach (var file in I18nFiles(plugin))
        {
            var translations = LoadTranslations(file);
            violations.AddRange(requiredKeys
                .Where(key => !translations.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
                .Select(key => $"{Path.GetFileName(file)}: {key}"));
        }

        violations.ShouldBeEmpty(
            $"plugin '{plugin}' i18n files miss assistantSetup keys: {string.Join("; ", violations)}");
    }

    [TestCaseSource(nameof(PluginsWithAssistantSetup))]
    public void TriggerPhrase_IsASynonymOfExactlyOnePluginSkill_AndOfNoOtherSkill(string plugin)
    {
        var triggerKey = AssistantSetupKeys(plugin)["triggerPhraseKey"];
        var pluginSkills = ProvidedSkills(plugin);
        var owners = new HashSet<string>(StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (var file in I18nFiles(plugin))
        {
            var language = Path.GetFileNameWithoutExtension(file);
            if (!LoadTranslations(file).TryGetValue(triggerKey, out var trigger) || string.IsNullOrWhiteSpace(trigger))
            {
                continue;
            }

            var normalized = Normalize(trigger);
            var matchingSkills = SynonymsByLanguage(language)
                .Where(kv => kv.Value.Contains(normalized))
                .Select(kv => kv.Key)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            var matchingPluginSkills = matchingSkills.Where(pluginSkills.Contains).ToList();
            if (matchingPluginSkills.Count != 1)
            {
                violations.Add($"{language}: '{trigger}' is a synonym of {matchingPluginSkills.Count} skill(s) of the plugin");
                continue;
            }

            owners.Add(matchingPluginSkills[0]);
            var foreign = matchingSkills.Except(matchingPluginSkills).ToList();
            if (foreign.Count > 0)
            {
                violations.Add($"{language}: '{trigger}' is also a synonym of {string.Join(", ", foreign)}");
            }
        }

        violations.ShouldBeEmpty(
            $"plugin '{plugin}' trigger phrase violations: {string.Join("; ", violations)}");
        owners.Count.ShouldBe(1,
            $"plugin '{plugin}' trigger phrases point to different skills across languages: {string.Join(", ", owners)}");
    }

    private static Dictionary<string, string> AssistantSetupKeys(string plugin)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(PluginDir(plugin), ManifestFileName)));
        var setup = manifest.RootElement.GetProperty(AssistantSetupProperty);
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var property in AssistantSetupKeyProperties)
        {
            setup.TryGetProperty(property, out var value).ShouldBeTrue(
                $"plugin '{plugin}' assistantSetup has no '{property}'");
            keys[property] = value.GetString() ?? string.Empty;
            keys[property].ShouldNotBeNullOrWhiteSpace($"plugin '{plugin}' assistantSetup.{property} is blank");
        }

        return keys;
    }

    private static HashSet<string> ProvidedSkills(string plugin)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(PluginDir(plugin), ManifestFileName)));
        return manifest.RootElement.GetProperty(ProvidedSkillsProperty).EnumerateArray()
            .Select(skill => skill.GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static Dictionary<string, HashSet<string>> SynonymsByLanguage(string language)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        if (CoreLanguages.Contains(language))
        {
            using (var core = JsonDocument.Parse(File.ReadAllText(Path.Combine([ApiRoot(), .. CoreSeedRelativePath]))))
            {
                AddSeedSynonyms(core.RootElement.GetProperty(SkillsProperty), language, result);
            }

            foreach (var seedFile in PluginSeedFiles())
            {
                using var seeds = JsonDocument.Parse(File.ReadAllText(seedFile));
                AddSeedSynonyms(seeds.RootElement, language, result);
            }

            return result;
        }

        var packFile = Path.Combine([ApiRoot(), .. LanguagesRelativePath, language, SkillSynonymsFileName]);
        if (!File.Exists(packFile))
        {
            return result;
        }

        var pack = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(packFile)) ?? [];
        foreach (var (skill, phrases) in pack)
        {
            result[skill] = (phrases ?? []).Select(Normalize).ToHashSet(StringComparer.Ordinal);
        }

        return result;
    }

    private static void AddSeedSynonyms(JsonElement skills, string language, Dictionary<string, HashSet<string>> result)
    {
        foreach (var skill in skills.EnumerateArray())
        {
            if (!skill.TryGetProperty(SynonymsProperty, out var synonyms)
                || synonyms.ValueKind != JsonValueKind.Object
                || !synonyms.TryGetProperty(language, out var phrases)
                || phrases.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var name = skill.GetProperty(NameProperty).GetString() ?? string.Empty;
            if (!result.TryGetValue(name, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                result[name] = set;
            }

            set.UnionWith(phrases.EnumerateArray().Select(p => Normalize(p.GetString() ?? string.Empty)));
        }
    }

    private static IEnumerable<string> PluginSeedFiles() =>
        Directory.GetDirectories(Path.Combine([ApiRoot(), .. FeaturesRelativePath]))
            .Select(dir => Path.Combine(dir, SkillSeedsFileName))
            .Where(File.Exists);

    private static IEnumerable<string> ShippedLanguages() =>
        CoreLanguages.Concat(
            Directory.GetDirectories(Path.Combine([ApiRoot(), .. LanguagesRelativePath]))
                .Where(dir => File.Exists(Path.Combine(dir, SkillSynonymsFileName)))
                .Select(Path.GetFileName)
                .OfType<string>());

    private static IEnumerable<string> I18nFiles(string plugin) =>
        Directory.GetFiles(Path.Combine(PluginDir(plugin), I18nDirectoryName), I18nFileSearchPattern)
            .OrderBy(file => file, StringComparer.Ordinal);

    private static Dictionary<string, string> LoadTranslations(string file) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [];

    private static string Normalize(string phrase) => phrase.Trim().ToLowerInvariant();

    private static string PluginDir(string plugin) => Path.Combine([ApiRoot(), .. FeaturesRelativePath, plugin]);

    private static string ApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine([dir.FullName, .. ApiRelativePath]);
            if (Directory.Exists(Path.Combine([candidate, .. FeaturesRelativePath])))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate {string.Join('/', ApiRelativePath)} by walking up from the test base directory.");
    }
}
