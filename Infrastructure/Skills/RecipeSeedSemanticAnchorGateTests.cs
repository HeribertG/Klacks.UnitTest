// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Seed gate for the lexical anchor of the semantic recipe fallback. RecipeTriggerMatcher treats
/// allOf[0] as the verb group and every later condition as an anchor, so this gate keeps recipe-seeds.json
/// in the shape that convention needs: a recipe with more than one condition has an anyWordStart-only verb
/// group first, every anchor condition carries at least one term, enough anchors exist to satisfy the
/// requirement, and each anchor is reachable, alone and combined, by a message built from its own
/// vocabulary. Messages that only talk about deferred notes must hit no anchor of any recipe. Single-condition
/// recipes are exempt from the anchor, so the set of them is pinned: a new one must be a conscious decision.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed.Models;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class RecipeSeedSemanticAnchorGateTests
{
    private const string RecipeSeedsFileName = "recipe-seeds.json";
    private const string German = "de";
    private const string TermSeparator = " ";
    private const string MessageTail = " .";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly HashSet<string> ExemptSingleConditionRecipes =
        new(StringComparer.Ordinal) { "setup-planning-profile", "setup-consultation" };

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static List<RecipeSeedDefinition> _recipes = null!;

    [OneTimeSetUp]
    public void LoadSeededRecipes()
    {
        var file = LocateDefinitionsFile(RecipeSeedsFileName);
        var seed = JsonSerializer.Deserialize<RecipeSeedFile>(File.ReadAllText(file), JsonReadOptions);
        _recipes = seed!.Recipes.OrderBy(r => r.SortOrder).ToList();
    }

    private static IEnumerable<RecipeSeedDefinition> MultiConditionRecipes() =>
        _recipes.Where(r => r.Trigger.AllOf.Count > 1);

    private static bool HasTerms(RecipeCondition condition) =>
        condition.AnyWordStart is { Count: > 0 }
        || condition.AnySubstring is { Count: > 0 }
        || condition.StartsWith is { Count: > 0 }
        || condition.AnyWordStartByLocale is { Count: > 0 };

    private static string? OwnTerm(RecipeCondition condition) =>
        condition.AnySubstring?.FirstOrDefault()
        ?? condition.AnyWordStart?.FirstOrDefault()
        ?? condition.AnyWordStartByLocale?.GetValueOrDefault(German)?.FirstOrDefault();

    [Test]
    public void EveryRecipe_HasAtLeastOneAllOfCondition()
    {
        var empty = _recipes.Where(r => r.Trigger.AllOf.Count == 0).Select(r => r.Name).ToList();

        empty.ShouldBeEmpty();
    }

    [Test]
    public void SingleConditionRecipes_AreExactlyTheKnownPhraseListRecipes()
    {
        var single = _recipes
            .Where(r => r.Trigger.AllOf.Count == 1)
            .Select(r => r.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        single.ShouldBe(ExemptSingleConditionRecipes.OrderBy(name => name, StringComparer.Ordinal).ToList());
    }

    [Test]
    public void MultiConditionRecipes_StartWithAnAnyWordStartOnlyVerbGroup()
    {
        var offenders = MultiConditionRecipes()
            .Where(r =>
            {
                var verb = r.Trigger.AllOf[0];
                return verb.AnyWordStart is not { Count: > 0 }
                       || verb.AnySubstring is { Count: > 0 }
                       || verb.StartsWith is { Count: > 0 }
                       || verb.AnyWordStartByLocale is { Count: > 0 };
            })
            .Select(r => r.Name)
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Test]
    public void MultiConditionRecipes_EveryAnchorConditionCarriesTerms()
    {
        var offenders = MultiConditionRecipes()
            .Where(r => r.Trigger.AllOf.Skip(1).Any(c => !HasTerms(c)))
            .Select(r => r.Name)
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Test]
    public void MultiConditionRecipes_HaveEnoughAnchorConditionsForTheRequirement()
    {
        var offenders = MultiConditionRecipes()
            .Where(r => r.Trigger.AllOf.Skip(1).Count(HasTerms)
                        < RecipeTriggerMatcher.MinRequiredAnchors)
            .Select(r => r.Name)
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Test]
    public void MultiConditionRecipes_AMessageBuiltFromTheirOwnAnchorTermsSatisfiesTheAnchor()
    {
        var offenders = new List<string>();

        foreach (var recipe in MultiConditionRecipes())
        {
            var terms = recipe.Trigger.AllOf.Skip(1).Select(OwnTerm).ToList();
            if (terms.Any(string.IsNullOrEmpty))
            {
                offenders.Add($"{recipe.Name}: an anchor condition has no term usable in a German message");
                continue;
            }

            var message = string.Join(TermSeparator, terms) + MessageTail;
            var count = RecipeTriggerMatcher.CountAnchors(recipe.Trigger, message, language: German);
            if (count != terms.Count || !RecipeTriggerMatcher.HasSemanticAnchor(recipe.Trigger, message, language: German))
            {
                offenders.Add($"{recipe.Name}: '{message}' hit {count} of {terms.Count} anchors");
            }
        }

        offenders.ShouldBeEmpty();
    }

    [Test]
    public void MultiConditionRecipes_AMessageBuiltFromASingleAnchorTermSatisfiesTheAnchor()
    {
        var offenders = new List<string>();

        foreach (var recipe in MultiConditionRecipes())
        {
            for (var index = 1; index < recipe.Trigger.AllOf.Count; index++)
            {
                var term = OwnTerm(recipe.Trigger.AllOf[index]);
                if (string.IsNullOrEmpty(term))
                {
                    offenders.Add($"{recipe.Name}: anchor condition {index} has no term usable in a German message");
                    continue;
                }

                var message = term + MessageTail;
                if (!RecipeTriggerMatcher.HasSemanticAnchor(recipe.Trigger, message, language: German))
                {
                    offenders.Add($"{recipe.Name}: '{message}' does not satisfy the anchor");
                }
            }
        }

        offenders.ShouldBeEmpty();
    }

    [Test]
    [TestCase("Lies bitte meine zurückgestellten Notizen vor.")]
    [TestCase("Hast du noch offene Hinweise für mich?")]
    [TestCase("Zurückgestellte Notizen verwalten, bitte.")]
    public void DeferredNotesMessage_HitsNoAnchorOfAnyMultiConditionRecipe(string message)
    {
        var anchored = MultiConditionRecipes()
            .Where(r => RecipeTriggerMatcher.HasSemanticAnchor(r.Trigger, message, language: German))
            .Select(r => r.Name)
            .ToList();

        anchored.ShouldBeEmpty();
    }

    private static string LocateDefinitionsFile(string fileName)
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

        throw new FileNotFoundException($"Could not locate {fileName} from {AppContext.BaseDirectory}");
    }
}
