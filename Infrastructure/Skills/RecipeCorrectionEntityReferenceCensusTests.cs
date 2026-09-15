// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Census of what RecipeExecutionPlan.CurrentAskSlotFeedsACapturingSearch admits, run against the real
/// recipe-seeds.json instead of hand-built plans.
///
/// The correction gate's entire precision rests on that predicate, and the predicate is a pure function of
/// the seed file: a seed edit can widen or narrow it with no code change and nothing going red. Adding
/// inject+capture to a chip slot - a perfectly reasonable improvement - would silently turn every
/// negation-bearing chip reply into a recipe abort. Both directions are pinned here.
///
/// The second test records the known gap (B1 in the 2026-09-15 hand-off) as an executable fact rather
/// than prose: entity-reference slots resolved by name inside a mutate step are not admitted, so the
/// correction guard covers the read paths and not the write paths. Fixing B1 must move entries from that
/// list into the census above, which is the point of asserting it.
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
    /// Every (recipe, slot) pair the correction gate treats as an entity reference, in engine order
    /// (sortOrder, then name). All 18 are *-name slots that a capturing search resolves to exactly one
    /// row; no chip slot and no free-text slot is among them, which is the property the gate's precision
    /// rests on. Change this list only together with the seed file or with the predicate - and read B1
    /// below first.
    /// </summary>
    private static readonly List<string> ExpectedAdmittedEntityReferenceSlots =
    [
        "record-employee-address-change::clientName",
        "offboard-employee::clientName",
        "add-employee-to-group::groupName",
        "add-employee-to-group::clientName",
        "add-absence-for-employee::clientName",
        "remove-absence-for-employee::clientName",
        "move-absence-for-employee::clientName",
        "record-availability-for-employee::clientName",
        "clear-availability-for-employee::clientName",
        "create-group::calendarName",
        "add-shift-to-group::shiftName",
        "add-shift-to-group::groupName",
        "create-shift-order::customerName",
        "create-shift-order::groupName",
        "add-task-to-container::containerName",
        "add-task-to-container::taskName",
        "add-extern-employee-to-nearest-group::clientName",
        "setup-owner-address::calendarName"
    ];

    /// <summary>
    /// B1: entity-reference ask slots whose value is resolved by name inside a MUTATE step, so the
    /// capturing-search predicate does not see them. A correction at one of these is raw-filled today and
    /// reaches a name resolver whose fuzzy token-cover stage asks whether every word of the STORED name
    /// appears in the QUERY - a longer query covers more likely, not less likely. move_group then writes.
    /// The cheapest sound remedies are a schema marker on the ask step or an allowlist of name-resolving
    /// skills; loosening the predicate to "any later step injects the slot" would also admit free-text
    /// slots such as note -> create_shift_order and destroy the false-positive protection.
    /// </summary>
    private static readonly string[] KnownGapEntityReferenceSlotsInsideAMutateStep =
    [
        "move-group::groupName",
        "move-group::newParentName",
        "onboard-employee::contractName",
        "onboard-employee::groupName",
        "bulk-add-employees-to-group::groupName",
        "add-selected-clients-to-group::groupName"
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

    /// <summary>
    /// Derived through the real predicate over the real steps, not restated from the design document.
    /// </summary>
    private static List<string> AdmittedEntityReferenceSlots()
    {
        var admitted = new List<string>();
        foreach (var recipe in _recipes)
        {
            for (var index = 0; index < recipe.Steps.Count; index++)
            {
                var step = recipe.Steps[index];
                if (!string.Equals(step.Kind, RecipeStepKinds.Ask, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(step.Slot))
                {
                    continue;
                }

                var plan = new RecipeExecutionPlan(recipe.Name, recipe.Steps, stepIndex: index);
                if (plan.CurrentAskSlotFeedsACapturingSearch())
                {
                    admitted.Add($"{recipe.Name}::{step.Slot}");
                }
            }
        }

        return admitted;
    }

    [Test]
    public void TheAdmittedEntityReferenceSlots_MatchTheAuditedCensus()
    {
        var admitted = AdmittedEntityReferenceSlots();

        admitted.ShouldBe(
            ExpectedAdmittedEntityReferenceSlots,
            "the correction gate admits a different set of ask slots than the audited census. If a seed " +
            "change caused this, check whether the slot really must resolve to exactly one entity - a chip " +
            "or free-text slot admitted here turns every negation-bearing answer into a recipe abort. " +
            "Actual: " + string.Join(", ", admitted));
    }

    [Test]
    public void KnownGap_SlotsResolvedByNameInsideAMutateStep_AreStillNotAdmitted()
    {
        var admitted = AdmittedEntityReferenceSlots();
        var nowCovered = KnownGapEntityReferenceSlotsInsideAMutateStep.Where(admitted.Contains).ToList();

        nowCovered.ShouldBeEmpty(
            "these slots moved out of the B1 known gap, so the gap list above is stale. Move them into " +
            "ExpectedAdmittedEntityReferenceSlots and update the hand-off - do not just delete them: " +
            string.Join(", ", nowCovered));
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
