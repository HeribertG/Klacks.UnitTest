// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Cross-quality gate between the two independent trigger systems: skill triggerKeywords
/// (skill-seeds.json / settings-reader-skills.json) and recipe triggers (recipe-seeds.json).
/// A recipe keyword match hijacks the whole chat turn deterministically BEFORE the LLM sees any
/// tools, so a skill phrase that also satisfies a foreign recipe trigger silently misroutes the
/// user (live regression 2026-07-16: "neue Firmenregel: maximal 3 Nachtschichten ..." started the
/// create-shift-order recipe instead of the start_company_rule skill). The recipe-vs-recipe
/// disjointness gate in RecipeSeedQualityTests never generated phrases from skill keywords — this
/// fixture closes that gap: no phrase built from a skill's own trigger keywords may fully match a
/// recipe trigger unless the recipe executes that skill or the pair is a documented intended
/// overlap, and no plugin-language skill synonym may embed a foreign recipe synonym. Regression
/// tests prove the original bug message is fixed and that the runtime safety net
/// (CompetingSkillIntentDetector) catches the pre-fix trigger.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SkillRecipeTriggerCrossQualityTests
{
    private const string RecipeSeedsFileName = "recipe-seeds.json";
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string SettingsReaderSkillsFileName = "settings-reader-skills.json";
    private const string RecipeSynonymsFileName = "recipe-synonyms.json";
    private const string SkillSynonymsFileName = "skill-synonyms.json";

    private const string CreateShiftOrderRecipeName = "create-shift-order";
    private const string CompanyRuleSkillName = "start_company_rule";
    private const string CompanyRuleNoneOfMarker = "firmenregel";
    private const string GermanLanguageCode = "de";
    private const string OriginalBugMessage =
        "Klacksy, wir haben eine neue Firmenregel: maximal 3 Nachtschichten pro Woche, hart blockieren.";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] PluginsLanguagesRelativePath =
    [
        "Klacks.Api", "Plugins", "Languages"
    ];

    // Documented intended overlaps: the recipe is the guided flow for exactly the action the skill
    // phrase names, so the deterministic hijack is the desired UX. Explain-page skills carry
    // action phrases as retrieval keywords ("absenz eintragen") — a user literally typing the
    // action phrase wants the action, not the page explanation. Every new entry needs the same
    // justification; a foreign-intent collision (like the company-rule regression) must be fixed
    // with a noneOf guard in recipe-seeds.json instead of being listed here.
    private static readonly HashSet<(string Skill, string Recipe)> IntendedOverlaps =
    [
        ("delete_break", "remove-absence-for-employee"),
        ("delete_client", "offboard-employee"),
        ("delete_absence", "remove-absence-for-employee"),
        ("add_client_to_group_by_name", "add-employee-to-group"),
        ("fill_group_by_criteria", "add-employee-to-group"),
        ("update_address", "record-employee-address-change"),
        ("analyze_group_semantics", "add-employee-to-group"),
        ("list_sealed_orders", "seal-shift-order"),
        ("explain_shift_lifecycle_order_to_shift", "seal-shift-order"),
        ("explain_page_schedule", "seal-shift-order"),
        ("explain_page_schedule", "close-payroll-period"),
        ("explain_page_absence", "add-absence-for-employee"),
        ("explain_page_absence", "remove-absence-for-employee"),
        ("explain_page_absence", "move-absence-for-employee"),
        ("explain_page_availability", "record-availability-for-employee"),
        ("explain_page_shifts", "create-shift-order"),
        ("explain_page_shifts", "seal-shift-order"),
        ("explain_page_employees", "onboard-employee"),
        ("explain_page_period_closing", "seal-shift-order"),
        ("explain_page_period_closing", "close-payroll-period"),
    ];

    private sealed record RecipeUnderTest(string Name, RecipeTrigger Trigger, HashSet<string> StepSkills);

    private sealed record SkillUnderTest(string Name, IReadOnlyList<string> Keywords);

    [Test]
    public void SkillTriggerPhrases_MustNotFullyMatchAForeignRecipeTrigger()
    {
        var recipes = LoadRecipes();
        var violations = new List<string>();

        foreach (var skill in LoadSkills())
        {
            var phrases = BuildPhrases(skill.Keywords);
            if (phrases.Count == 0)
            {
                continue;
            }

            foreach (var recipe in recipes)
            {
                if (recipe.StepSkills.Contains(skill.Name)
                    || IntendedOverlaps.Contains((skill.Name, recipe.Name)))
                {
                    continue;
                }

                var hit = phrases.FirstOrDefault(p =>
                    CouldMatchAllConditions(recipe.Trigger, p)
                    && RecipeTriggerMatcher.Matches(recipe.Trigger, null, p));
                if (hit != null)
                {
                    violations.Add($"skill '{skill.Name}' phrase '{hit}' matches recipe '{recipe.Name}'");
                }
            }
        }

        violations.ShouldBeEmpty(
            "A phrase built from a skill's own trigger keywords must never fully match a recipe the " +
            "skill does not belong to — the recipe hijacks the chat turn deterministically before the " +
            "LLM can call the skill, so the user is silently misrouted. Fix: add a distinguishing " +
            "noneOf guard to the recipe in recipe-seeds.json (and bump its version), or — only when the " +
            "recipe is genuinely the guided flow for that exact action — add the pair to " +
            "IntendedOverlaps with a justification. Violations: " + string.Join("; ", violations));
    }

    [Test]
    public void PluginSkillSynonyms_MustNotContainAForeignRecipeSynonym()
    {
        var recipes = LoadRecipes();
        var violations = new List<string>();

        foreach (var (code, recipeSynonyms, skillSynonyms) in LoadSynonymFilePairs())
        {
            foreach (var (recipeSlug, recipeTerms) in recipeSynonyms)
            {
                var recipe = recipes.FirstOrDefault(r => r.Name == recipeSlug);

                foreach (var (skillName, skillTerms) in skillSynonyms)
                {
                    if ((recipe != null && recipe.StepSkills.Contains(skillName))
                        || IntendedOverlaps.Contains((skillName, recipeSlug)))
                    {
                        continue;
                    }

                    foreach (var recipeTerm in recipeTerms ?? [])
                    {
                        var embedding = (skillTerms ?? []).FirstOrDefault(skillTerm =>
                            !string.IsNullOrWhiteSpace(skillTerm)
                            && !string.IsNullOrWhiteSpace(recipeTerm)
                            && skillTerm.Contains(recipeTerm, StringComparison.OrdinalIgnoreCase));
                        if (embedding != null)
                        {
                            violations.Add(
                                $"{code}: skill '{skillName}' synonym '{embedding}' contains recipe " +
                                $"'{recipeSlug}' synonym '{recipeTerm}'");
                        }
                    }
                }
            }
        }

        violations.ShouldBeEmpty(
            "A plugin-language skill synonym must not contain a foreign recipe's synonym as a " +
            "substring: the recipe synonym path fires on plain substring containment (bypassing allOf, " +
            "and noneOf guards are core-language only), so every message carrying the skill phrase " +
            "would hijack the recipe in that language. Violations: " + string.Join("; ", violations));
    }

    [Test]
    public void OriginalCompanyRuleBugMessage_DoesNotMatchCreateShiftOrderTrigger()
    {
        // Live regression 2026-07-16: this exact message started the create-shift-order recipe. The
        // noneOf company-rule exclusion vocabulary in recipe-seeds.json must keep vetoing it.
        var trigger = LoadRecipes().Single(r => r.Name == CreateShiftOrderRecipeName).Trigger;

        RecipeTriggerMatcher.Matches(trigger, null, OriginalBugMessage).ShouldBeFalse(
            "the company-rule intake message must never start the create-shift-order recipe; the " +
            "noneOf guard with '" + CompanyRuleNoneOfMarker + "' vocabulary was removed or weakened");
    }

    [Test]
    public void PreFixCreateShiftOrderTrigger_MatchesTheBugMessage_ProvingTheBugIsReproduced()
    {
        // Removing the company-rule noneOf condition restores the pre-fix trigger. It MUST match the
        // bug message again — otherwise the regression below would prove nothing.
        var preFixTrigger = BuildPreFixCreateShiftOrderTrigger();

        RecipeTriggerMatcher.Matches(preFixTrigger, null, OriginalBugMessage).ShouldBeTrue(
            "the pre-fix trigger (without the company-rule noneOf vocabulary) no longer reproduces " +
            "the original over-match; the regression setup is stale");
    }

    [Test]
    public void CompetingSkillIntentDetector_WouldHaveCaughtThePreFixHijack()
    {
        // Had the runtime safety net existed before the noneOf fix, the silent hijack would have been
        // converted into a confirmation question: the message verbatim contains the multiword
        // start_company_rule keyword phrase "neue firmenregel", which the pre-fix trigger does not
        // own. This pins the safety net against the exact production incident.
        var preFixTrigger = BuildPreFixCreateShiftOrderTrigger();
        var recipe = LoadRecipes().Single(r => r.Name == CreateShiftOrderRecipeName);
        var seededSkills = LoadSkills()
            .Select(s => new AgentSkill
            {
                Name = s.Name,
                TriggerKeywords = JsonSerializer.Serialize(s.Keywords)
            })
            .ToList();

        var competing = CompetingSkillIntentDetector.FindCompetingSkillNames(
            seededSkills,
            OriginalBugMessage,
            GermanLanguageCode,
            preFixTrigger,
            matchedRecipeSynonyms: null,
            servedSkillNames: recipe.StepSkills);

        competing.ShouldContain(CompanyRuleSkillName,
            "the runtime competing-skill detector must flag the company-rule skill intent inside the " +
            "hijacked message, so the plan is gated behind a confirmation instead of silently starting " +
            "the create-shift-order flow");
    }

    private static RecipeTrigger BuildPreFixCreateShiftOrderTrigger()
    {
        var trigger = LoadRecipes().Single(r => r.Name == CreateShiftOrderRecipeName).Trigger;
        return new RecipeTrigger
        {
            AllOf = trigger.AllOf,
            NoneOf = trigger.NoneOf
                .Where(c => c.AnySubstring == null
                            || !c.AnySubstring.Any(t => t.Contains(CompanyRuleNoneOfMarker, StringComparison.OrdinalIgnoreCase)))
                .ToList()
        };
    }

    private static List<string> BuildPhrases(IReadOnlyList<string> keywords)
    {
        var terms = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Singles cover a user uttering one skill phrase; pairs cover a realistic message combining
        // two of the same skill's own phrases ("Füge einen Ferienwunsch-Platzhalter hinzu"). Terms
        // from OTHER skills or free message context are deliberately not mixed in — that unbounded
        // class is covered at runtime by CompetingSkillIntentDetector.
        var phrases = new List<string>(terms);
        for (var i = 0; i < terms.Count; i++)
        {
            for (var j = i + 1; j < terms.Count; j++)
            {
                phrases.Add($"{terms[i]} {terms[j]}");
            }
        }

        return phrases;
    }

    // Cheap over-approximation of RecipeTriggerMatcher's per-condition semantics (plain substring
    // containment ignores word boundaries): a phrase that fails this can never match the trigger, so
    // the expensive production matcher only runs on near-collisions.
    private static bool CouldMatchAllConditions(RecipeTrigger trigger, string phrase)
    {
        return trigger.AllOf.Count > 0 && trigger.AllOf.All(condition =>
            (condition.AnyWordStart ?? []).Concat(condition.AnySubstring ?? []).Concat(condition.StartsWith ?? [])
            .Any(term => phrase.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    private static List<RecipeUnderTest> LoadRecipes()
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        using var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile(RecipeSeedsFileName)));
        var result = new List<RecipeUnderTest>();

        foreach (var element in document.RootElement.GetProperty("recipes").EnumerateArray())
        {
            var name = element.GetProperty("name").GetString() ?? string.Empty;
            if (!element.TryGetProperty("trigger", out var triggerElement))
            {
                continue;
            }

            var trigger = JsonSerializer.Deserialize<RecipeTrigger>(triggerElement.GetRawText(), jsonOptions);
            if (trigger == null)
            {
                continue;
            }

            var stepSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (element.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
            {
                foreach (var step in steps.EnumerateArray())
                {
                    if (step.TryGetProperty("skill", out var skill) && skill.ValueKind == JsonValueKind.String)
                    {
                        stepSkills.Add(skill.GetString()!);
                    }
                }
            }

            result.Add(new RecipeUnderTest(name, trigger, stepSkills));
        }

        return result;
    }

    private static List<SkillUnderTest> LoadSkills()
    {
        var result = new List<SkillUnderTest>();
        CollectSkills(LocateDefinitionsFile(SkillSeedsFileName), result);

        var settingsReader = TryLocateDefinitionsFile(SettingsReaderSkillsFileName);
        if (settingsReader != null)
        {
            CollectSkills(settingsReader, result);
        }

        return result;
    }

    private static void CollectSkills(string filePath, List<SkillUnderTest> result)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(filePath));
        foreach (var skill in document.RootElement.GetProperty("skills").EnumerateArray())
        {
            if (!skill.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var keywords = new List<string>();
            if (skill.TryGetProperty("triggerKeywords", out var kw) && kw.ValueKind == JsonValueKind.Array)
            {
                keywords.AddRange(kw.EnumerateArray()
                    .Where(k => k.ValueKind == JsonValueKind.String)
                    .Select(k => k.GetString()!));
            }

            result.Add(new SkillUnderTest(name.GetString()!, keywords));
        }
    }

    private static List<(string Code, Dictionary<string, List<string>> RecipeSynonyms, Dictionary<string, List<string>> SkillSynonyms)> LoadSynonymFilePairs()
    {
        var result = new List<(string, Dictionary<string, List<string>>, Dictionary<string, List<string>>)>();
        var languagesDir = TryLocatePluginsLanguagesDir();
        if (languagesDir == null)
        {
            return result;
        }

        foreach (var languageDir in Directory.GetDirectories(languagesDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var recipeFile = Path.Combine(languageDir, RecipeSynonymsFileName);
            var skillFile = Path.Combine(languageDir, SkillSynonymsFileName);
            if (!File.Exists(recipeFile) || !File.Exists(skillFile))
            {
                continue;
            }

            var recipeMap = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(recipeFile))
                            ?? new Dictionary<string, List<string>>();
            var skillMap = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(skillFile))
                           ?? new Dictionary<string, List<string>>();
            result.Add((new DirectoryInfo(languageDir).Name, recipeMap, skillMap));
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
