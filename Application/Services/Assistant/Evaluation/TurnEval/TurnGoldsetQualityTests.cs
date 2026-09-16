// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class TurnGoldsetQualityTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string RecipesSeedFileName = "recipe-seeds.json";
    private const string ClientEntityType = "client";
    private const string HonestyGoldsetFileName = "turn-honesty-v1.json";
    private const string HonestyModeMustAbstain = "must-abstain";
    private const string NoExpectedToolSentinel = " no-tool";

    // W0.5: lower bounds for the goldset build-out. The recipe and messaging targets are exact
    // (every recipe, every messaging plugin skill), the mutating-skill target is a floor.
    private const int MinTotalItemsAcrossGoldsets = 200;
    private const double MinMutatingSkillCoverage = 0.5;
    private const string MutatingEffect = "Mutate";

    // W0.5 nacharbeiten (Prüfbericht 2026-09-02): a goldset item must not just repeat the skill's
    // German/English label back as "Bitte <Label> <Verb>." — that tests the goldset build, not
    // selection. A tool item counts as discoverable when it either names a genuine alternative
    // skill, or shares at least one real word with the skill's name/synonyms.
    private const int MinSharedTokenLength = 4;
    private const double MinDiscoverableToolItemCoverage = 0.5;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex TemplateMessageRegex = new(
        @"^(Bitte|Please)\s.+\s(abbrechen|aktivieren|akzeptieren|anlegen|anwenden|anzeigen|aktualisieren|ausführen|ausfüllen|bündeln|deaktivieren|deinstallieren|entfernen|geokodieren|gruppieren|hinzufügen|installieren|löschen|machen|markieren|optimieren|schneiden|setzen|starten|verschieben|versiegeln|verwerfen|widerrufen|zurücksetzen|zuweisen|ändern|execute|add|create|show|delete|update)\.$",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex WordRegex = new(@"\p{L}+", RegexOptions.Compiled, RegexTimeout);

    // W0.5 nacharbeiten: every turn-selection/turn-honesty file under Goldsets/ is picked up by
    // scanning the directory instead of a hardcoded file list, so a file can no longer go
    // unnoticed by the gate (as turn-selection-crud-v1.json did before it was removed).
    private static readonly Dictionary<string, int> ExpectedVersionByKind = new(StringComparer.Ordinal)
    {
        ["turn-selection"] = 2,
        ["turn-honesty"] = 1,
        ["turn-correction"] = 1
    };

    private static readonly string[] GoldsetsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Goldsets"
    ];

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] PluginsFeaturesRelativePath =
    [
        "Klacks.Api", "Plugins", "Features"
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Test]
    public void Goldsets_MustDeclareVersionAndKind()
    {
        foreach (var (fileName, document) in LoadGoldsets())
        {
            document.Kind.ShouldNotBeNullOrWhiteSpace(fileName);
            ExpectedVersionByKind.ContainsKey(document.Kind!).ShouldBeTrue($"{fileName}: unknown kind '{document.Kind}'");
            document.Version.ShouldBe(ExpectedVersionByKind[document.Kind!], fileName);
            document.Items.ShouldNotBeEmpty(fileName);
        }
    }

    // W0.5 nacharbeiten: a goldset must not carry two items with the identical message+locale
    // that resolve to different expectedTools — the model cannot possibly satisfy both, so the
    // item pair measures goldset authoring quality rather than skill selection.
    [Test]
    public void Goldsets_MessagesMustNotMapToDifferentExpectedTools()
    {
        var violations = new List<string>();

        var allItems = LoadGoldsets()
            .SelectMany(g => g.Document.Items.Select(item => (g.FileName, Item: item)))
            .ToList();

        var groups = allItems.GroupBy(x => (x.Item.Message, Locale: x.Item.Locale ?? string.Empty));

        foreach (var group in groups)
        {
            var distinctTools = group
                .Select(x => x.Item.ExpectedTool ?? NoExpectedToolSentinel)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (distinctTools.Count > 1)
            {
                var occurrences = string.Join(", ", group.Select(x => $"{x.FileName}/{x.Item.Id}={x.Item.ExpectedTool ?? "null"}"));
                violations.Add($"message '{group.Key.Message}' ({group.Key.Locale}) is ambiguous: {occurrences}");
            }
        }

        violations.ShouldBeEmpty();
    }

    // W0.5 nacharbeiten: a goldset item must not simply hand the skill's own label back as
    // "Bitte <Label> <Verb>." / "Please <Label> <Verb>." — that is a template of the goldset
    // build, not a message a real user would type, and it tests nothing about skill selection.
    [Test]
    public void Goldsets_MessagesMustNotFollowTheBitteLabelVerbTemplate()
    {
        var violations = new List<string>();

        foreach (var (fileName, document) in LoadGoldsets())
        {
            violations.AddRange(document.Items
                .Where(item => TemplateMessageRegex.IsMatch(item.Message))
                .Select(item => $"{fileName}/{item.Id}: message '{item.Message}' follows the Bitte/Please-label-verb template"));
        }

        violations.ShouldBeEmpty();
    }

    // W0.5 nacharbeiten: at least half of the tool items must be discoverable from their message
    // alone — either a genuinely equivalent alternativeTool is declared, or the message shares a
    // real word (>= MinSharedTokenLength characters) with the skill's name or one of its seeded
    // synonyms. Below that bar, the item measures the goldset author's word choice, not whether
    // the model can find the right skill from what a user would plausibly type.
    [Test]
    public void Goldsets_ToolItemsMustBeDiscoverableFromAlternativesOrSharedVocabulary()
    {
        var vocabulary = LoadSkillVocabulary();
        var toolItems = LoadGoldsets()
            .SelectMany(g => g.Document.Items)
            .Where(item => item.ExpectedTool != null)
            .ToList();

        toolItems.ShouldNotBeEmpty();

        var discoverable = toolItems.Count(item => IsDiscoverableFromAlternativesOrVocabulary(item, vocabulary));
        var coverage = (double)discoverable / toolItems.Count;

        coverage.ShouldBeGreaterThanOrEqualTo(
            MinDiscoverableToolItemCoverage,
            $"only {discoverable}/{toolItems.Count} ({coverage:P1}) tool items have alternativeTools or a shared token with " +
            $"their skill's name/synonyms, required >= {MinDiscoverableToolItemCoverage:P0}");
    }

    [Test]
    public void HonestyGoldset_ItemsMustBeNoToolMustAbstainWithLocale()
    {
        var violations = new List<string>();

        foreach (var (fileName, document) in LoadGoldsets())
        {
            var isHonestyFile = string.Equals(fileName, HonestyGoldsetFileName, StringComparison.Ordinal);

            foreach (var item in document.Items)
            {
                if (isHonestyFile && item.Honesty == null)
                {
                    violations.Add($"{fileName}/{item.Id}: honesty item without honesty block");
                }

                if (item.Honesty == null)
                {
                    continue;
                }

                if (item.ExpectedTool != null)
                {
                    violations.Add($"{fileName}/{item.Id}: honesty item must not expect a tool call");
                }

                if (!string.Equals(item.Honesty.Mode, HonestyModeMustAbstain, StringComparison.Ordinal))
                {
                    violations.Add($"{fileName}/{item.Id}: unknown honesty mode '{item.Honesty.Mode}'");
                }

                if (string.IsNullOrWhiteSpace(item.Locale))
                {
                    violations.Add($"{fileName}/{item.Id}: honesty item must declare a locale");
                }
            }
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void CorrectionGoldset_ItemsExpectingCorrectionMustDeclareAPreviousTurn()
    {
        var violations = new List<string>();

        foreach (var (fileName, document) in LoadGoldsets())
        {
            violations.AddRange(document.Items
                .Where(i => i.ExpectsCorrection && i.PreviousTurn == null)
                .Select(i => $"{fileName}/{i.Id}: expectsCorrection item must declare previousTurn"));
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void CorrectionGoldset_ClarificationItemsMustNotExpectATool()
    {
        var violations = new List<string>();

        foreach (var (fileName, document) in LoadGoldsets())
        {
            violations.AddRange(document.Items
                .Where(i => i.ExpectsClarification && i.ExpectedTool != null)
                .Select(i => $"{fileName}/{i.Id}: expectsClarification item must not declare expectedTool"));
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void Goldsets_ItemIdsMustBeUnique()
    {
        var violations = new List<string>();

        foreach (var (fileName, document) in LoadGoldsets())
        {
            var duplicates = document.Items
                .GroupBy(i => i.Id, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => $"{fileName}: duplicate item id '{g.Key}'");
            violations.AddRange(duplicates);

            violations.AddRange(document.Items
                .Where(i => string.IsNullOrWhiteSpace(i.Id))
                .Select(_ => $"{fileName}: item with blank id"));
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void Goldsets_ExpectedToolsAndAlternativesMustExistInSkillSeeds()
    {
        var skills = LoadSkillParameters();
        var violations = new List<string>();

        foreach (var (fileName, document) in LoadGoldsets())
        {
            foreach (var item in document.Items)
            {
                if (item.ExpectedTool != null && !skills.ContainsKey(item.ExpectedTool))
                {
                    violations.Add($"{fileName}/{item.Id}: expectedTool '{item.ExpectedTool}' not found in {SkillSeedsFileName}");
                }

                violations.AddRange(item.AlternativeTools
                    .Where(alt => !skills.ContainsKey(alt))
                    .Select(alt => $"{fileName}/{item.Id}: alternativeTool '{alt}' not found in {SkillSeedsFileName}"));

                if (item.PreviousTurn != null && !skills.ContainsKey(item.PreviousTurn.CalledSkill))
                {
                    violations.Add($"{fileName}/{item.Id}: previousTurn.calledSkill '{item.PreviousTurn.CalledSkill}' not found in {SkillSeedsFileName}");
                }

                if (item.ExpectedUndoSkill != null && !skills.ContainsKey(item.ExpectedUndoSkill))
                {
                    violations.Add($"{fileName}/{item.Id}: expectedUndoSkill '{item.ExpectedUndoSkill}' not found in {SkillSeedsFileName}");
                }
            }
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void CorrectionGoldset_ClarificationItemsMustAlsoExpectCorrection()
    {
        var violations = new List<string>();

        foreach (var (fileName, document) in LoadGoldsets())
        {
            violations.AddRange(document.Items
                .Where(i => i.ExpectsClarification && !i.ExpectsCorrection)
                .Select(i => $"{fileName}/{i.Id}: expectsClarification item must also declare expectsCorrection"));
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void Goldsets_ExpectedSlotsMustBeParametersOfTheSkill()
    {
        var skills = LoadSkillParameters();
        var violations = new List<string>();

        foreach (var (fileName, document) in LoadGoldsets())
        {
            foreach (var item in document.Items.Where(i => i.ExpectedTool != null && skills.ContainsKey(i.ExpectedTool!)))
            {
                var parameters = skills[item.ExpectedTool!];
                violations.AddRange(item.ExpectedSlots
                    .Where(slot => !parameters.Contains(slot.Name))
                    .Select(slot =>
                        $"{fileName}/{item.Id}: slot '{slot.Name}' is not a parameter of skill '{item.ExpectedTool}' " +
                        $"(parameters: {string.Join(", ", parameters.Order())})"));
            }
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void Goldsets_NoToolItemsMustNotHaveSlots()
    {
        var violations = new List<string>();

        foreach (var (fileName, document) in LoadGoldsets())
        {
            violations.AddRange(document.Items
                .Where(i => i.ExpectedTool == null && i.ExpectedSlots.Count > 0)
                .Select(i => $"{fileName}/{i.Id}: no-tool item must not define expectedSlots"));
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void Goldsets_ResolvedEntitySlotsMustReferenceEntityWithPositiveIdNumber()
    {
        var violations = new List<string>();

        foreach (var (fileName, document) in LoadGoldsets())
        {
            foreach (var item in document.Items)
            {
                foreach (var slot in item.ExpectedSlots.Where(s => s.Match == SlotMatchMode.ResolvedEntityId))
                {
                    if (slot.Entity == null)
                    {
                        violations.Add($"{fileName}/{item.Id}: resolved-entity-id slot '{slot.Name}' has no entity reference");
                    }
                    else if (slot.Entity.IdNumber <= 0)
                    {
                        violations.Add($"{fileName}/{item.Id}: resolved-entity-id slot '{slot.Name}' has non-positive idNumber {slot.Entity.IdNumber}");
                    }
                    else if (string.IsNullOrWhiteSpace(slot.Entity.Type))
                    {
                        violations.Add($"{fileName}/{item.Id}: resolved-entity-id slot '{slot.Name}' has blank entity type");
                    }
                    else if (!string.Equals(slot.Entity.Type, ClientEntityType, StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add($"{fileName}/{item.Id}: resolved-entity-id slot '{slot.Name}' uses unsupported entity type '{slot.Entity.Type}'");
                    }
                }
            }
        }

        violations.ShouldBeEmpty();
    }

    // W0.5: the build-out target is expressed as testable lower bounds, not just a plan number.
    [Test]
    public void Goldsets_MeetW05CoverageTargets()
    {
        var documents = LoadGoldsets();
        var items = documents.SelectMany(d => d.Document.Items).ToList();
        var expectedTools = items
            .Where(i => i.ExpectedTool != null)
            .Select(i => i.ExpectedTool!)
            .ToHashSet(StringComparer.Ordinal);
        var expectedRecipes = items
            .Where(i => i.ExpectedRecipe != null)
            .Select(i => i.ExpectedRecipe!)
            .ToHashSet(StringComparer.Ordinal);

        var violations = new List<string>();

        if (items.Count < MinTotalItemsAcrossGoldsets)
        {
            violations.Add($"goldset total {items.Count} below {MinTotalItemsAcrossGoldsets}");
        }

        // 25/25 recipes must appear as expectedRecipe items.
        foreach (var recipeName in LoadRecipeNames())
        {
            if (!expectedRecipes.Contains(recipeName))
            {
                violations.Add($"recipe '{recipeName}' has no expectedRecipe item");
            }
        }

        // 3/3 messaging plugin skills must appear as expectedTool items.
        foreach (var messagingSkill in LoadMessagingSkillNames())
        {
            if (!expectedTools.Contains(messagingSkill))
            {
                violations.Add($"messaging skill '{messagingSkill}' has no expectedTool item");
            }
        }

        // >= 50 % of mutating skills must be covered by at least one expectedTool item.
        var mutatingSkills = LoadSkillEffects()[MutatingEffect];
        var coveredMutating = mutatingSkills.Count(expectedTools.Contains);
        var coverage = (double)coveredMutating / mutatingSkills.Count;
        if (coverage < MinMutatingSkillCoverage)
        {
            violations.Add(
                $"mutating-skill coverage {coverage:P1} ({coveredMutating}/{mutatingSkills.Count}) below {MinMutatingSkillCoverage:P0}");
        }

        violations.ShouldBeEmpty();
    }

    // W0.5 nacharbeiten: scans every *.json file under Goldsets/ instead of a hardcoded list, so a
    // new or forgotten file can no longer sit outside the gate. Files that are not shaped like a
    // { version, kind, items } turn-selection/turn-honesty document (knowledge-index-v1.json is a
    // bare array, speech-wer-v1.json is a different kind/item shape) are skipped on purpose —
    // they have their own format and are not this test's concern.
    private static List<(string FileName, TurnGoldsetDocument Document)> LoadGoldsets()
    {
        var goldsetsDir = LocateRepoDirectory(GoldsetsRelativePath);
        var result = new List<(string, TurnGoldsetDocument)>();

        foreach (var path in Directory.GetFiles(goldsetsDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(path);
            TurnGoldsetDocument? document;

            try
            {
                document = JsonSerializer.Deserialize<TurnGoldsetDocument>(File.ReadAllText(path), SerializerOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (document?.Kind == null || !ExpectedVersionByKind.ContainsKey(document.Kind))
            {
                continue;
            }

            result.Add((fileName, document));
        }

        result.ShouldNotBeEmpty("no turn-selection/turn-honesty goldset file was found");
        return result;
    }

    private static Dictionary<string, HashSet<string>> LoadSkillParameters()
    {
        var skills = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        AddParametersFromSeedFile(skills, LocateRepoFile(DefinitionsRelativePath, SkillSeedsFileName));

        // W0.5: plugin seeds (messaging send_message/read_messages/list_messaging_providers) live in
        // Plugins/Features/*/skill-seeds.json and were invisible to the quality gates before.
        foreach (var pluginSeedFile in EnumeratePluginSeedFiles())
        {
            AddParametersFromSeedFile(skills, pluginSeedFile);
        }

        return skills;
    }

    private static void AddParametersFromSeedFile(Dictionary<string, HashSet<string>> skills, string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        foreach (var skill in EnumerateSkills(document.RootElement))
        {
            var name = skill.GetProperty("name").GetString() ?? string.Empty;
            var parameters = new HashSet<string>(StringComparer.Ordinal);

            if (skill.TryGetProperty("parameters", out var parameterArray)
                && parameterArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var parameter in parameterArray.EnumerateArray())
                {
                    if (parameter.TryGetProperty("name", out var parameterName)
                        && parameterName.ValueKind == JsonValueKind.String)
                    {
                        parameters.Add(parameterName.GetString()!);
                    }
                }
            }

            skills[name] = parameters;
        }
    }

    private static bool IsDiscoverableFromAlternativesOrVocabulary(
        TurnGoldsetItem item, Dictionary<string, HashSet<string>> vocabulary)
    {
        if (item.AlternativeTools.Count > 0)
        {
            return true;
        }

        if (item.ExpectedTool == null || !vocabulary.TryGetValue(item.ExpectedTool, out var skillTokens) || skillTokens.Count == 0)
        {
            return false;
        }

        return WordRegex.Matches(item.Message)
            .Select(match => match.Value)
            .Where(token => token.Length >= MinSharedTokenLength)
            .Any(skillTokens.Contains);
    }

    private static Dictionary<string, HashSet<string>> LoadSkillVocabulary()
    {
        var vocabulary = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        AddVocabularyFromSeedFile(vocabulary, LocateRepoFile(DefinitionsRelativePath, SkillSeedsFileName));
        foreach (var pluginSeedFile in EnumeratePluginSeedFiles())
        {
            AddVocabularyFromSeedFile(vocabulary, pluginSeedFile);
        }

        return vocabulary;
    }

    private static void AddVocabularyFromSeedFile(Dictionary<string, HashSet<string>> vocabulary, string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        foreach (var skill in EnumerateSkills(document.RootElement))
        {
            var name = skill.GetProperty("name").GetString() ?? string.Empty;
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddTokens(tokens, name.Replace('_', ' '));

            if (skill.TryGetProperty("synonyms", out var synonymsElement) && synonymsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var localeProperty in synonymsElement.EnumerateObject())
                {
                    if (localeProperty.Value.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var synonym in localeProperty.Value.EnumerateArray())
                    {
                        AddTokens(tokens, synonym.GetString() ?? string.Empty);
                    }
                }
            }

            vocabulary[name] = tokens;
        }
    }

    private static void AddTokens(HashSet<string> tokens, string text)
    {
        foreach (Match match in WordRegex.Matches(text))
        {
            if (match.Value.Length >= MinSharedTokenLength)
            {
                tokens.Add(match.Value);
            }
        }
    }

    private static IEnumerable<JsonElement> EnumerateSkills(JsonElement root)
    {
        // Core seeds are { "version": …, "skills": […] }, plugin seeds are a bare array.
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().ToList();
        }

        if (root.TryGetProperty("skills", out var skillsArray) && skillsArray.ValueKind == JsonValueKind.Array)
        {
            return skillsArray.EnumerateArray().ToList();
        }

        return [];
    }

    private static HashSet<string> LoadRecipeNames()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateRepoFile(DefinitionsRelativePath, RecipesSeedFileName)));
        return document.RootElement
            .GetProperty("recipes")
            .EnumerateArray()
            .Select(r => r.GetProperty("name").GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> LoadMessagingSkillNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pluginSeedFile in EnumeratePluginSeedFiles())
        {
            using var document = JsonDocument.Parse(File.ReadAllText(pluginSeedFile));
            foreach (var skill in EnumerateSkills(document.RootElement))
            {
                names.Add(skill.GetProperty("name").GetString() ?? string.Empty);
            }
        }

        return names;
    }

    private static Dictionary<string, HashSet<string>> LoadSkillEffects()
    {
        var effects = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        AddEffectsFromSeedFile(effects, LocateRepoFile(DefinitionsRelativePath, SkillSeedsFileName));
        foreach (var pluginSeedFile in EnumeratePluginSeedFiles())
        {
            AddEffectsFromSeedFile(effects, pluginSeedFile);
        }

        return effects;
    }

    private static void AddEffectsFromSeedFile(Dictionary<string, HashSet<string>> effects, string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        foreach (var skill in EnumerateSkills(document.RootElement))
        {
            var name = skill.GetProperty("name").GetString() ?? string.Empty;
            var effect = skill.TryGetProperty("effect", out var effectProperty) && effectProperty.ValueKind == JsonValueKind.String
                ? effectProperty.GetString() ?? string.Empty
                : string.Empty;

            if (!effects.TryGetValue(effect, out var names))
            {
                names = new HashSet<string>(StringComparer.Ordinal);
                effects[effect] = names;
            }

            names.Add(name);
        }
    }

    private static IEnumerable<string> EnumeratePluginSeedFiles()
    {
        var pluginsDir = LocateRepoDirectory(PluginsFeaturesRelativePath);
        return Directory.GetFiles(pluginsDir, SkillSeedsFileName, SearchOption.AllDirectories);
    }

    private static string LocateRepoDirectory(string[] relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine([dir.FullName, .. relativePath]);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate {string.Join('/', relativePath)} by walking up from the test base directory.");
    }

    private static string LocateRepoFile(string[] relativePath, string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine([dir.FullName, .. relativePath, fileName]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {string.Join('/', relativePath)}/{fileName} by walking up from the test base directory.");
    }
}
