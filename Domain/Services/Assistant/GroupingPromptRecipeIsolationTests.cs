// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Live regression 2026-10-03: "Gruppiere alle Mitarbeitenden nach ihrer Adresse: Region, Kanton, Stadt und darunter
/// Gemeinden" was taken over by the semantic recipe fallback (bulk-add-employees-to-group asked for a group name)
/// instead of reaching partition_clients_by_address. For the 25-language video prompts (TestData/grouping-prompts.json:
/// A = address tree with municipalities, B = qualification groups, C = qualification sub-groups, D = confirmation)
/// this pins three things: every group recipe the semantic fallback could pick vetoes A, B and C (noneOf plus every
/// installed pack veto, as RecipeEngineService does); no recipe keyword trigger (pack synonyms and pack vetoes of the
/// prompt's language included) fires for A-D; and GroupingIntentResolver guarantees the right bulk skill for A-D, with
/// the pack grouping and affirmation words loaded as at startup.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Models.Assistant.Recipes;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class GroupingPromptRecipeIsolationTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string UnitTestProjectDirectory = "Klacks.UnitTest";
    private const string AddressSkill = "partition_clients_by_address";
    private const string QualificationSkill = "partition_clients_by_qualification";

    private static readonly string[] SemanticCandidateRecipes =
    [
        "add-employee-to-group",
        "bulk-add-employees-to-group",
        "create-group",
        "move-group",
        "add-shift-to-group",
        "add-selected-clients-to-group",
        "prepare-groups-for-planning",
        "bulk-add-customers-to-nearest-group",
        "bulk-add-employees-to-nearest-group",
        "bulk-add-externs-to-nearest-group",
        "add-extern-employee-to-nearest-group",
    ];

    // Known pack conflict, reported to the owner: the Malay pack lists "tak" (= not) as a negation and the
    // detector merges every pack's negations, so a Polish "Tak, ..." (= yes) is never an affirmation. The
    // Polish video prompt therefore starts with "Dobrze"; this pin fails once the conflict is fixed.
    private const string PolishYesConfirmation = "Tak, przyjmij te grupy.";

    private static readonly string[] VetoedPromptKeys = ["A", "B", "C"];
    private static readonly string[] AllPromptKeys = ["A", "B", "C", "D"];

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> Prompts = new(LoadPrompts);
    private static readonly Lazy<Dictionary<string, RecipeTrigger>> Triggers = new(LoadTriggers);
    private static readonly Lazy<Dictionary<string, Dictionary<string, List<string>>>> PackVetoes = new(() => LoadPackFile("recipe-vetoes.json"));
    private static readonly Lazy<Dictionary<string, Dictionary<string, List<string>>>> PackSynonyms = new(() => LoadPackFile("recipe-synonyms.json"));

    [OneTimeSetUp]
    public void LoadPluginSignals()
    {
        GroupingIntentPluginLoader.Load(ApiDirectory());
        LoadPackAffirmationsOnly();
    }

    [OneTimeTearDown]
    public void ResetAffirmations() => AffirmationDetector.Reset();

    [Test]
    public void PolishConfirmation_IsStillBlockedByTheMalayNegation_RemoveTheExceptionOnceFixed()
    {
        AffirmationDetector.IsAffirmation(PolishYesConfirmation).ShouldBeFalse();
    }

    [Test]
    public void ConfirmationPrompt_LeadsWithAnAffirmation_InEveryLanguage()
    {
        var misses = Prompts.Value
            .Where(p => !AffirmationDetector.IsAffirmation(p.Value["D"]) || !AffirmationDetector.LeadsWithAffirmation(p.Value["D"]))
            .Select(p => $"{p.Key}: {p.Value["D"]}")
            .ToList();

        misses.ShouldBeEmpty(string.Join(Environment.NewLine, misses));
    }

    [Test]
    public void PromptFile_CoversAll25LanguagesWithFourPrompts()
    {
        Prompts.Value.Count.ShouldBe(25);
        Prompts.Value.Values.ShouldAllBe(p => AllPromptKeys.All(k => p.ContainsKey(k) && !string.IsNullOrWhiteSpace(p[k])));
    }

    [Test]
    public void EverySemanticGroupRecipe_VetoesTheGroupingPrompts_InEveryLanguage()
    {
        var misses = new List<string>();
        foreach (var recipe in SemanticCandidateRecipes)
        {
            Triggers.Value.ShouldContainKey(recipe);
            var allVetoTerms = AllVetoTerms(recipe);
            foreach (var (locale, prompts) in Prompts.Value)
            {
                foreach (var key in VetoedPromptKeys)
                {
                    if (!RecipeTriggerMatcher.IsVetoed(Triggers.Value[recipe], prompts[key], null, locale, allVetoTerms))
                    {
                        misses.Add($"{recipe} / {locale} / {key}: {prompts[key]}");
                    }
                }
            }
        }

        misses.ShouldBeEmpty(string.Join(Environment.NewLine, misses));
    }

    [Test]
    public void NoRecipeKeywordTrigger_FiresForTheGroupingPrompts_InAnyLanguage()
    {
        var hits = new List<string>();
        foreach (var (recipe, trigger) in Triggers.Value)
        {
            foreach (var (locale, prompts) in Prompts.Value)
            {
                var synonyms = ForLocale(PackSynonyms.Value, locale, recipe);
                var vetoes = ForLocale(PackVetoes.Value, locale, recipe);
                foreach (var key in AllPromptKeys)
                {
                    if (RecipeTriggerMatcher.Matches(trigger, synonyms, prompts[key], null, locale, vetoes))
                    {
                        hits.Add($"{recipe} / {locale} / {key}: {prompts[key]}");
                    }
                }
            }
        }

        hits.ShouldBeEmpty(string.Join(Environment.NewLine, hits));
    }

    [Test]
    public void GroupingIntentResolver_GuaranteesTheBulkSkill_ForEveryPromptInEveryLanguage()
    {
        var misses = new List<string>();
        foreach (var (locale, prompts) in Prompts.Value)
        {
            Expect(misses, locale, "A", prompts["A"], AddressSkill);
            Expect(misses, locale, "B", prompts["B"], QualificationSkill);
            Expect(misses, locale, "C", prompts["C"], QualificationSkill);
            Expect(misses, locale, "D", prompts["D"], AddressSkill);
            Expect(misses, locale, "D", prompts["D"], QualificationSkill);
        }

        misses.ShouldBeEmpty(string.Join(Environment.NewLine, misses));
    }

    // Only the AffirmationDetector is configured (and reset afterwards): ConversationSignalsPluginLoader would also
    // feed the decline, correction and cancellation detectors, whose prefix-matched pack entries ("ne" ...) leak
    // into every later fixture of the run.
    private static void LoadPackAffirmationsOnly()
    {
        var affirmations = new List<string>();
        var negations = new List<string>();
        foreach (var directory in Directory.GetDirectories(Path.Combine(ApiDirectory(), "Plugins", "Languages")))
        {
            var file = Path.Combine(directory, "conversation-signals.json");
            if (!File.Exists(file))
            {
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(file));
            affirmations.AddRange(ReadArray(document.RootElement, "affirmations"));
            negations.AddRange(ReadArray(document.RootElement, "negations"));
        }

        AffirmationDetector.Configure(affirmations, negations);
    }

    private static IEnumerable<string> ReadArray(JsonElement root, string property) =>
        root.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
            : [];

    private static void Expect(List<string> misses, string locale, string key, string message, string skill)
    {
        if (!GroupingIntentResolver.GuaranteedSkillNames(message).Contains(skill))
        {
            misses.Add($"{locale} / {key} lacks {skill}: {message}");
        }
    }

    private static IReadOnlyCollection<string> AllVetoTerms(string recipe) =>
        PackVetoes.Value.Values
            .Where(byRecipe => byRecipe.ContainsKey(recipe))
            .SelectMany(byRecipe => byRecipe[recipe])
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyCollection<string>? ForLocale(
        Dictionary<string, Dictionary<string, List<string>>> byLocale, string locale, string recipe) =>
        byLocale.TryGetValue(locale, out var byRecipe) && byRecipe.TryGetValue(recipe, out var terms) ? terms : null;

    private static Dictionary<string, Dictionary<string, string>> LoadPrompts()
    {
        var path = Path.Combine(RepositoryRoot(), UnitTestProjectDirectory, "Domain", "Services", "Assistant", "TestData", "grouping-prompts.json");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path))!;
    }

    private static Dictionary<string, RecipeTrigger> LoadTriggers()
    {
        var path = Path.Combine(ApiDirectory(), "Application", "Skills", "Definitions", "recipe-seeds.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var triggers = new Dictionary<string, RecipeTrigger>(StringComparer.Ordinal);
        foreach (var recipe in document.RootElement.GetProperty("recipes").EnumerateArray())
        {
            if (recipe.TryGetProperty("trigger", out var trigger) && trigger.ValueKind == JsonValueKind.Object)
            {
                triggers[recipe.GetProperty("name").GetString()!] =
                    JsonSerializer.Deserialize<RecipeTrigger>(trigger.GetRawText(), JsonOptions)!;
            }
        }

        return triggers;
    }

    private static Dictionary<string, Dictionary<string, List<string>>> LoadPackFile(string fileName)
    {
        var result = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.GetDirectories(Path.Combine(ApiDirectory(), "Plugins", "Languages")))
        {
            var file = Path.Combine(directory, fileName);
            if (File.Exists(file))
            {
                result[Path.GetFileName(directory)] =
                    JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(file))!;
            }
        }

        return result;
    }

    private static string ApiDirectory() => Path.Combine(RepositoryRoot(), ApiProjectDirectory);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, ApiProjectDirectory)))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root with Klacks.Api not found.");
    }
}
