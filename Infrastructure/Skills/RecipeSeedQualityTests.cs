// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Quality gate for the data-driven recipe engine: parses recipe-seeds.json and enforces the
/// machine-checkable rules from .claude/rules/recipe-authoring.md, so a malformed recipe turns the
/// build red instead of being discovered (and reworked) live. Checks naming (english kebab-case,
/// unique), unique sort order, that only the executed step kinds ask/search/mutate are used (guard and
/// verify are dead and must never appear), that every referenced skill exists in the skill seeds, that
/// every inject value is either a $slot reference resolving to an ask slot or a capture alias or a
/// non-blank literal string constant, that ask and search/mutate steps are structurally complete, that
/// every recipe carries goalTranslations and every ask step carries promptTranslations for the core
/// languages (de/en/fr/it) so deterministic fallbacks never leak English meta-prompts, and that no two
/// recipes' triggers can both match the same realistic sentence (using the production
/// RecipeTriggerMatcher itself, not a reimplementation).
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class RecipeSeedQualityTests
{
    private const string RecipeSeedsFileName = "recipe-seeds.json";
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string SettingsReaderSkillsFileName = "settings-reader-skills.json";

    private const string RecipeSynonymsFileName = "recipe-synonyms.json";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] PluginsLanguagesRelativePath =
    [
        "Klacks.Api", "Plugins", "Languages"
    ];

    private static readonly HashSet<string> ExecutedStepKinds =
        new(StringComparer.Ordinal) { "ask", "search", "mutate" };

    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private static readonly Regex KebabCase = new("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.Compiled);

    private sealed record InjectValue(string Reference, bool IsString);

    private sealed record RecipeStep(
        string Kind, string? Slot, string? Prompt, string? Skill,
        IReadOnlyList<InjectValue> InjectValues, string? CaptureAlias,
        IReadOnlyDictionary<string, string>? PromptTranslations);

    private sealed record Recipe(
        string Name, int SortOrder, bool HasTriggerAllOf, IReadOnlyList<RecipeStep> Steps,
        IReadOnlyDictionary<string, string>? GoalTranslations);

    private static List<Recipe> LoadRecipes()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile(RecipeSeedsFileName)));
        var recipes = new List<Recipe>();

        foreach (var element in document.RootElement.GetProperty("recipes").EnumerateArray())
        {
            var name = element.GetProperty("name").GetString() ?? string.Empty;
            var sortOrder = element.TryGetProperty("sortOrder", out var so) ? so.GetInt32() : -1;
            var hasAllOf = element.TryGetProperty("trigger", out var trigger) &&
                           trigger.TryGetProperty("allOf", out var allOf) &&
                           allOf.ValueKind == JsonValueKind.Array &&
                           allOf.GetArrayLength() > 0;

            var steps = new List<RecipeStep>();
            if (element.TryGetProperty("steps", out var stepArray) && stepArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var step in stepArray.EnumerateArray())
                {
                    var kind = step.TryGetProperty("kind", out var k) ? k.GetString() ?? string.Empty : string.Empty;
                    var slot = step.TryGetProperty("slot", out var s) ? s.GetString() : null;
                    var prompt = step.TryGetProperty("prompt", out var p) ? p.GetString() : null;
                    var skill = step.TryGetProperty("skill", out var sk) ? sk.GetString() : null;

                    var injectValues = new List<InjectValue>();
                    if (step.TryGetProperty("inject", out var inject) && inject.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in inject.EnumerateObject())
                        {
                            injectValues.Add(prop.Value.ValueKind == JsonValueKind.String
                                ? new InjectValue(prop.Value.GetString() ?? string.Empty, true)
                                : new InjectValue(prop.Value.GetRawText(), false));
                        }
                    }

                    string? captureAlias = null;
                    if (step.TryGetProperty("capture", out var capture) && capture.ValueKind == JsonValueKind.String)
                    {
                        var text = capture.GetString() ?? string.Empty;
                        var idx = text.LastIndexOf(" as ", StringComparison.Ordinal);
                        captureAlias = idx >= 0 ? text[(idx + 4)..].Trim() : null;
                    }

                    steps.Add(new RecipeStep(
                        kind, slot, prompt, skill, injectValues, captureAlias,
                        ReadTranslations(step, "promptTranslations")));
                }
            }

            recipes.Add(new Recipe(name, sortOrder, hasAllOf, steps, ReadTranslations(element, "goalTranslations")));
        }

        return recipes;
    }

    private static IReadOnlyDictionary<string, string>? ReadTranslations(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var translations)
            || translations.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in translations.EnumerateObject())
        {
            map[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString() ?? string.Empty
                : string.Empty;
        }

        return map;
    }

    [Test]
    public void RecipeNames_MustBeEnglishKebabCase_AndUnique()
    {
        var recipes = LoadRecipes();

        var badNames = recipes
            .Where(r => !KebabCase.IsMatch(r.Name))
            .Select(r => r.Name)
            .ToList();
        badNames.ShouldBeEmpty(
            "Recipe names must be english kebab-case (lowercase ASCII, hyphen-separated), e.g. " +
            "'add-employee-to-group'. Offending: " + string.Join(", ", badNames));

        var duplicateNames = recipes
            .GroupBy(r => r.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        duplicateNames.ShouldBeEmpty("Recipe names must be unique. Duplicates: " + string.Join(", ", duplicateNames));
    }

    [Test]
    public void RecipeSortOrders_MustBeUnique()
    {
        var duplicates = LoadRecipes()
            .GroupBy(r => r.SortOrder)
            .Where(g => g.Count() > 1)
            .Select(g => $"sortOrder {g.Key}: {string.Join(", ", g.Select(r => r.Name))}")
            .ToList();

        duplicates.ShouldBeEmpty("Recipe sortOrder values must be unique. Collisions: " + string.Join("; ", duplicates));
    }

    [Test]
    public void EveryRecipe_MustHaveTriggerAllOf_AndAtLeastOneStep()
    {
        var violations = LoadRecipes()
            .Where(r => !r.HasTriggerAllOf || r.Steps.Count == 0)
            .Select(r => $"{r.Name}: " + (!r.HasTriggerAllOf ? "missing trigger.allOf" : "no steps"))
            .ToList();

        violations.ShouldBeEmpty("Every recipe needs a trigger.allOf and at least one step. Violations: " +
            string.Join("; ", violations));
    }

    [Test]
    public void ContractAndGroupAssignmentSteps_MustAskTheSlotNameSuggestionGroundingKnows()
    {
        // SuggestionEntityNameReader (Infrastructure/Repositories/Assistant) hardcodes "contractName"
        // and "groupName" as the only two slots it grounds LLM suggestion chips against. If a recipe
        // ever asks for a contract/group name under a different slot, the grounding filter silently
        // no-ops for that recipe (fail-open) instead of failing loud — this gate catches that drift
        // at the JSON level before it ships.
        const string ContractSkill = "assign_contract_by_name";
        const string GroupSkill = "add_client_to_group_by_name";
        const string ContractSlot = "contractName";
        const string GroupSlot = "groupName";

        var violations = new List<string>();
        foreach (var recipe in LoadRecipes())
        {
            var askSlots = recipe.Steps.Where(s => s.Kind == "ask").Select(s => s.Slot).ToHashSet(StringComparer.Ordinal);

            if (recipe.Steps.Any(s => s.Skill == ContractSkill) && !askSlots.Contains(ContractSlot))
            {
                violations.Add($"{recipe.Name}: calls '{ContractSkill}' but has no ask step with slot '{ContractSlot}'");
            }

            if (recipe.Steps.Any(s => s.Skill == GroupSkill) && !askSlots.Contains(GroupSlot))
            {
                violations.Add($"{recipe.Name}: calls '{GroupSkill}' but has no ask step with slot '{GroupSlot}'");
            }
        }

        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Test]
    public void Steps_MustUseOnlyExecutedKinds_NoDeadGuardOrVerify()
    {
        var violations = new List<string>();
        foreach (var recipe in LoadRecipes())
        {
            foreach (var step in recipe.Steps.Where(s => !ExecutedStepKinds.Contains(s.Kind)))
            {
                violations.Add($"{recipe.Name}: step kind '{step.Kind}' is not executed by the engine " +
                    "(only ask/search/mutate run; guard/verify are dead — put verification in the skill)");
            }
        }

        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Test]
    public void SearchAndMutateSteps_MustReferenceExistingSkills()
    {
        var knownSkills = LoadKnownSkillNames();
        var violations = new List<string>();

        foreach (var recipe in LoadRecipes())
        {
            foreach (var step in recipe.Steps.Where(s => s.Kind is "search" or "mutate"))
            {
                if (string.IsNullOrWhiteSpace(step.Skill))
                {
                    violations.Add($"{recipe.Name}: a {step.Kind} step has no skill");
                }
                else if (!knownSkills.Contains(step.Skill))
                {
                    violations.Add($"{recipe.Name}: skill '{step.Skill}' does not exist in {SkillSeedsFileName}");
                }
            }
        }

        violations.ShouldBeEmpty("search/mutate steps must reference real seeded skills. Violations: " +
            string.Join("; ", violations));
    }

    [Test]
    public void InjectValues_MustBeResolvableSlotReferences_OrNonBlankStringLiterals()
    {
        // The engine resolves a $-prefixed inject value from the slot bag and passes any other string
        // through verbatim as a literal constant (RecipeExecutionPlan.ResolveReference). A literal must
        // be a non-blank JSON string; a $-reference must resolve to an ask slot or a capture alias.
        var violations = new List<string>();

        foreach (var recipe in LoadRecipes())
        {
            var definedSlots = new HashSet<string>(StringComparer.Ordinal);
            foreach (var step in recipe.Steps)
            {
                if (step.Kind == "ask" && !string.IsNullOrWhiteSpace(step.Slot))
                {
                    definedSlots.Add(step.Slot!);
                }

                if (!string.IsNullOrWhiteSpace(step.CaptureAlias))
                {
                    definedSlots.Add(step.CaptureAlias!);
                }
            }

            foreach (var step in recipe.Steps)
            {
                foreach (var value in step.InjectValues)
                {
                    if (!value.IsString)
                    {
                        violations.Add($"{recipe.Name}: inject value '{value.Reference}' is not a JSON string " +
                            "(the engine's inject bag is string-typed; literals must be strings)");
                        continue;
                    }

                    if (!value.Reference.StartsWith('$'))
                    {
                        if (string.IsNullOrWhiteSpace(value.Reference))
                        {
                            violations.Add($"{recipe.Name}: inject contains a blank literal value");
                        }

                        continue;
                    }

                    var slot = value.Reference[1..];
                    if (!definedSlots.Contains(slot))
                    {
                        violations.Add($"{recipe.Name}: inject references '${slot}' which is not defined by any " +
                            "ask slot or capture alias in the recipe");
                    }
                }
            }
        }

        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Test]
    public void EveryRecipe_MustHaveGoalTranslations_ForAllCoreLanguages()
    {
        var violations = new List<string>();

        foreach (var recipe in LoadRecipes())
        {
            var missing = MissingCoreLanguages(recipe.GoalTranslations);
            if (missing.Count > 0)
            {
                violations.Add($"{recipe.Name}: goalTranslations missing or blank for {string.Join(", ", missing)}");
            }
        }

        violations.ShouldBeEmpty(
            "Every recipe must carry non-blank goalTranslations for the core languages (de/en/fr/it): the " +
            "deterministic confirmation fallback embeds the localized goal instead of the raw English goal " +
            "text. Violations: " + string.Join("; ", violations));
    }

    [Test]
    public void EveryAskStep_MustHavePromptTranslations_ForAllCoreLanguages()
    {
        var violations = new List<string>();

        foreach (var recipe in LoadRecipes())
        {
            foreach (var step in recipe.Steps.Where(s => s.Kind == "ask"))
            {
                var missing = MissingCoreLanguages(step.PromptTranslations);
                if (missing.Count > 0)
                {
                    violations.Add($"{recipe.Name}/{step.Slot}: promptTranslations missing or blank for " +
                        string.Join(", ", missing));
                }
            }
        }

        violations.ShouldBeEmpty(
            "Every ask step must carry non-blank promptTranslations for the core languages (de/en/fr/it): " +
            "when the model fails to produce a question-shaped reply, the deterministic fallback shows the " +
            "localized question instead of the raw English ask meta-prompt. Violations: " +
            string.Join("; ", violations));
    }

    private static List<string> MissingCoreLanguages(IReadOnlyDictionary<string, string>? translations) =>
        CoreLanguages
            .Where(lang => translations == null
                           || !translations.TryGetValue(lang, out var text)
                           || string.IsNullOrWhiteSpace(text))
            .ToList();

    [Test]
    public void AskSteps_MustHaveSlotAndPrompt()
    {
        var violations = new List<string>();
        foreach (var recipe in LoadRecipes())
        {
            foreach (var step in recipe.Steps.Where(s => s.Kind == "ask"))
            {
                if (string.IsNullOrWhiteSpace(step.Slot) || string.IsNullOrWhiteSpace(step.Prompt))
                {
                    violations.Add($"{recipe.Name}: an ask step is missing slot or prompt");
                }
            }
        }

        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Test]
    public void RecipeTriggers_MustBeDisjoint_NoRealisticSentenceMatchesTwoRecipes()
    {
        var triggers = LoadRecipeTriggers();
        var violations = new List<string>();
        var reportedPairs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, trigger) in triggers)
        {
            var selfSatisfyingPhrases = GenerateCanonicalPhrases(trigger)
                .Where(phrase => RecipeTriggerMatcher.Matches(trigger, null, phrase))
                .ToList();

            foreach (var (otherName, otherTrigger) in triggers)
            {
                if (otherName == name)
                {
                    continue;
                }

                var pairKey = string.CompareOrdinal(name, otherName) < 0
                    ? $"{name}|{otherName}"
                    : $"{otherName}|{name}";
                if (reportedPairs.Contains(pairKey))
                {
                    continue;
                }

                var collidingPhrase = selfSatisfyingPhrases
                    .FirstOrDefault(phrase => RecipeTriggerMatcher.Matches(otherTrigger, null, phrase));
                if (collidingPhrase != null)
                {
                    reportedPairs.Add(pairKey);
                    violations.Add($"'{collidingPhrase}' matches both '{name}' and '{otherName}'");
                }
            }
        }

        violations.ShouldBeEmpty(
            "No realistic sentence built from a recipe's own trigger vocabulary (anyWordStart x anySubstring) " +
            "may also match another recipe's trigger — the engine returns the FIRST matching recipe by sort " +
            "order and silently ignores the rest, so an overlap means one recipe silently steals the other's " +
            "requests. Add a distinguishing noneOf guard on one side (see recipe-authoring.md §2). Violations: " +
            string.Join("; ", violations));
    }

    private static List<string> GenerateCanonicalPhrases(RecipeTrigger trigger)
    {
        IEnumerable<string> ConditionTerms(RecipeCondition condition) =>
            (condition.AnyWordStart ?? new List<string>())
                .Concat(condition.AnySubstring ?? new List<string>())
                .Concat(condition.StartsWith ?? new List<string>());

        IEnumerable<string> phrases = new[] { string.Empty };
        foreach (var condition in trigger.AllOf)
        {
            var terms = ConditionTerms(condition).ToList();
            if (terms.Count == 0)
            {
                continue;
            }

            phrases = phrases.SelectMany(prefix => terms.Select(term =>
                string.IsNullOrEmpty(prefix) ? term : $"{prefix} {term}"));
        }

        return phrases.Where(p => !string.IsNullOrEmpty(p)).ToList();
    }

    private static List<(string Name, RecipeTrigger Trigger)> LoadRecipeTriggers()
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        using var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile(RecipeSeedsFileName)));
        var result = new List<(string, RecipeTrigger)>();

        foreach (var element in document.RootElement.GetProperty("recipes").EnumerateArray())
        {
            var name = element.GetProperty("name").GetString() ?? string.Empty;
            if (!element.TryGetProperty("trigger", out var triggerElement))
            {
                continue;
            }

            var trigger = JsonSerializer.Deserialize<RecipeTrigger>(triggerElement.GetRawText(), jsonOptions);
            if (trigger != null)
            {
                result.Add((name, trigger));
            }
        }

        return result;
    }

    [Test]
    public void RecipeSynonymFiles_WhenPresent_MustCoverExactlyTheRecipeSlugs()
    {
        var slugs = LoadRecipes().Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (var (code, map) in LoadRecipeSynonymFiles())
        {
            var orphans = map.Keys.Where(k => !slugs.Contains(k)).ToList();
            if (orphans.Count > 0)
            {
                violations.Add($"{code}: orphan key(s) not matching any recipe slug: {string.Join(", ", orphans)}");
            }

            var missing = slugs.Where(s => !map.ContainsKey(s)).ToList();
            if (missing.Count > 0)
            {
                violations.Add($"{code}: missing synonym entry for recipe(s): {string.Join(", ", missing)}");
            }
        }

        violations.ShouldBeEmpty(
            "Every recipe-synonyms.json must cover exactly the current recipe slugs (no orphan keys, every " +
            "recipe present) so that adding a recipe forces multi-language coverage in each participating " +
            "language. Violations: " + string.Join("; ", violations));
    }

    [Test]
    public void RecipeSynonymLists_MustBeNonEmpty_WithNoBlankTerms()
    {
        var violations = new List<string>();

        foreach (var (code, map) in LoadRecipeSynonymFiles())
        {
            foreach (var (slug, terms) in map)
            {
                if (terms is null || terms.Count == 0)
                {
                    violations.Add($"{code}/{slug}: empty synonym list");
                }
                else if (terms.Any(string.IsNullOrWhiteSpace))
                {
                    violations.Add($"{code}/{slug}: contains a blank synonym term");
                }
            }
        }

        violations.ShouldBeEmpty(string.Join("; ", violations));
    }

    [Test]
    public void RecipeSynonyms_MustBeDisjointAcrossRecipes_PerLanguage()
    {
        var violations = new List<string>();

        foreach (var (code, map) in LoadRecipeSynonymFiles())
        {
            var entries = map
                .SelectMany(kv => (kv.Value ?? new List<string>())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => (Slug: kv.Key, Term: t.Trim())))
                .ToList();

            for (var i = 0; i < entries.Count; i++)
            {
                for (var j = i + 1; j < entries.Count; j++)
                {
                    if (entries[i].Slug == entries[j].Slug)
                    {
                        continue;
                    }

                    if (IsSubstringEither(entries[i].Term, entries[j].Term))
                    {
                        violations.Add(
                            $"{code}: synonym '{entries[i].Term}' ({entries[i].Slug}) collides with " +
                            $"'{entries[j].Term}' ({entries[j].Slug})");
                    }
                }
            }
        }

        violations.ShouldBeEmpty(
            "Synonyms must be disjoint across recipes within a language (no term may be a substring of " +
            "another recipe's term). The synonym path bypasses allOf and noneOf is core-language only, and a " +
            "single recipe sorts before its bulk sibling, so a shared term mis-routes a bulk request to the " +
            "single recipe. Violations: " + string.Join("; ", violations));
    }

    private static bool IsSubstringEither(string a, string b) =>
        a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
        b.Contains(a, StringComparison.OrdinalIgnoreCase);

    private static List<(string Code, Dictionary<string, List<string>> Map)> LoadRecipeSynonymFiles()
    {
        var result = new List<(string, Dictionary<string, List<string>>)>();
        var languagesDir = TryLocatePluginsLanguagesDir();
        if (languagesDir == null)
        {
            return result;
        }

        foreach (var languageDir in Directory.GetDirectories(languagesDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var file = Path.Combine(languageDir, RecipeSynonymsFileName);
            if (!File.Exists(file))
            {
                continue;
            }

            var map = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(file))
                      ?? new Dictionary<string, List<string>>();
            result.Add((new DirectoryInfo(languageDir).Name, map));
        }

        return result;
    }

    private static string? TryLocatePluginsLanguagesDir()
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

        return null;
    }

    private static HashSet<string> LoadKnownSkillNames()
    {
        var skillNames = new HashSet<string>(StringComparer.Ordinal);
        CollectSkillNames(LocateDefinitionsFile(SkillSeedsFileName), skillNames);

        var settingsReader = TryLocateDefinitionsFile(SettingsReaderSkillsFileName);
        if (settingsReader != null)
        {
            CollectSkillNames(settingsReader, skillNames);
        }

        return skillNames;
    }

    private static void CollectSkillNames(string filePath, HashSet<string> skillNames)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(filePath));
        foreach (var skill in document.RootElement.GetProperty("skills").EnumerateArray())
        {
            if (skill.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                skillNames.Add(name.GetString()!);
            }
        }
    }

    private static string LocateDefinitionsFile(string fileName) =>
        TryLocateDefinitionsFile(fileName)
        ?? throw new FileNotFoundException(
            $"Could not locate {string.Join('/', DefinitionsRelativePath)}/{fileName} by walking up from the test base directory.");

    private static string? TryLocateDefinitionsFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(DefinitionsRelativePath);
            segments.Add(fileName);
            var candidate = Path.Combine(segments.ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
