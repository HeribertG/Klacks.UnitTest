// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Census of what RecipeExecutionPlan.CurrentAskSlotExpectsAnEntityName admits, run against the real
/// recipe-seeds.json instead of hand-built plans.
///
/// The correction gate's entire precision rests on that predicate, and the predicate is a pure function of
/// the seed file: a seed edit can widen or narrow it with no code change and nothing going red. Adding
/// inject+capture to a chip slot - a perfectly reasonable improvement - would silently turn every
/// negation-bearing chip reply into a recipe abort.
///
/// Two directions are pinned. The admitted census catches an unintended widening. The negative cases catch
/// the opposite and, because each one is first asserted to still exist as an ask step, they cannot rot into
/// passing vacantly when a recipe is renamed or a slot disappears.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed.Models;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class RecipeCorrectionEntityReferenceCensusTests
{
    private const string RecipeSeedsFileName = "recipe-seeds.json";

    /// <summary>
    /// Every (recipe, slot) pair the correction gate treats as an entity name, in engine order (sortOrder,
    /// then name). Four of these are names of entities being CREATED rather than resolved
    /// (onboard-employee/employeeName, user-onboarding/fullName, create-group/groupName,
    /// create-shift-order/shiftName); they are admitted because a multi-clause sentence is not a plausible
    /// value for them either, and raw-filling one would create the entity under that sentence as its name.
    /// Change this list only together with the seed file or with the predicate.
    /// </summary>
    private static readonly List<string> ExpectedAdmittedEntityNameSlots =
    [
        "onboard-employee::employeeName",
        "onboard-employee::contractName",
        "onboard-employee::groupName",
        "user-onboarding::fullName",
        "record-employee-address-change::clientName",
        "offboard-employee::clientName",
        "add-employee-to-group::groupName",
        "add-employee-to-group::clientName",
        "bulk-add-employees-to-group::groupName",
        "add-selected-clients-to-group::groupName",
        "add-absence-for-employee::clientName",
        "remove-absence-for-employee::clientName",
        "move-absence-for-employee::clientName",
        "record-availability-for-employee::clientName",
        "clear-availability-for-employee::clientName",
        "create-group::groupName",
        "create-group::calendarName",
        "move-group::groupName",
        "move-group::newParentName",
        "add-shift-to-group::shiftName",
        "add-shift-to-group::groupName",
        "create-shift-order::shiftName",
        "create-shift-order::customerName",
        "create-shift-order::groupName",
        "add-task-to-container::containerName",
        "add-task-to-container::taskName",
        "add-extern-employee-to-nearest-group::clientName",
        "setup-owner-address::calendarName"
    ];

    /// <summary>
    /// Chip and typed-scalar slots, whose offered replies include bare negations ("Nein, eigener Betrieb"
    /// is one of setup-consultation's own chips). Admitting one of these would turn an ordinary answer
    /// into a recipe abort, which is the false positive the detector exists to avoid.
    /// </summary>
    private static readonly string[] KnownNonEntityNameSlots =
    [
        "setup-consultation::attribution",
        "setup-consultation::orderSource",
        "setup-consultation::nextStep",
        "onboard-employee::startDate",
        "record-employee-address-change::validFrom",
        "offboard-employee::exitDate"
    ];

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

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
        _recipes = seed!.Recipes
            .Where(r => r.IsEnabled)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<(RecipeSeedDefinition Recipe, RecipeStep Step, int Index)> AskSteps()
    {
        foreach (var recipe in _recipes)
        {
            for (var index = 0; index < recipe.Steps.Count; index++)
            {
                var step = recipe.Steps[index];
                if (string.Equals(step.Kind, RecipeStepKinds.Ask, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(step.Slot))
                {
                    yield return (recipe, step, index);
                }
            }
        }
    }

    /// <summary>
    /// Derived through the real predicate over the real steps, not restated from the design document.
    /// </summary>
    private static List<string> AdmittedEntityNameSlots() =>
        AskSteps()
            .Where(ask => new RecipeExecutionPlan(ask.Recipe.Name, ask.Recipe.Steps, stepIndex: ask.Index)
                .CurrentAskSlotExpectsAnEntityName())
            .Select(ask => $"{ask.Recipe.Name}::{ask.Step.Slot}")
            .ToList();

    [Test]
    public void TheAdmittedEntityNameSlots_MatchTheAuditedCensus()
    {
        var admitted = AdmittedEntityNameSlots();

        admitted.ShouldBe(
            ExpectedAdmittedEntityNameSlots,
            "the correction gate admits a different set of ask slots than the audited census. If a seed " +
            "change caused this, check whether the slot really expects an entity name - a chip or free-text " +
            "slot admitted here turns every negation-bearing answer into a recipe abort. " +
            "Actual: " + string.Join(", ", admitted));
    }

    [Test]
    public void ChipAndTypedSlots_AreNotAdmittedAsEntityNames()
    {
        var admitted = AdmittedEntityNameSlots();
        var everyAskSlot = AskSteps().Select(ask => $"{ask.Recipe.Name}::{ask.Step.Slot}").ToList();

        foreach (var slot in KnownNonEntityNameSlots)
        {
            everyAskSlot.ShouldContain(
                slot,
                "this negative case no longer exists as an ask step in recipe-seeds.json, so asserting it " +
                "is not admitted would pass vacantly. Replace it with a real chip or typed slot.");
            admitted.ShouldNotContain(slot);
        }
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
