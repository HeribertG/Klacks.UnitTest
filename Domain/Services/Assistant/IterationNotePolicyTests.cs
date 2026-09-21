// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the precedence of the per-iteration instruction note, which both chat loops used to carry as a
/// hand-copied conditional expression. The three candidates are alternatives, not additions: appending two
/// of them would give the model contradictory instructions in one turn, and the plan nudge must stop as
/// soon as the turn has called a tool, or every follow-up iteration would keep nagging for a plan.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class IterationNotePolicyTests
{
    private const string PendingNote = "pending-note";
    private const string RecipeNote = "recipe-note";

    [Test]
    public void ConfirmationGate_WinsOverRecipeAndPlanNudge()
    {
        var note = IterationNotePolicy.Select(
            confirmThisIteration: true, PendingNote, forceRecipe: true, RecipeNote,
            suggestPlan: true, functionCallCount: 0);

        note.ShouldBe(PendingNote);
    }

    [Test]
    public void ForcedRecipeStep_WinsOverPlanNudge()
    {
        var note = IterationNotePolicy.Select(
            confirmThisIteration: false, PendingNote, forceRecipe: true, RecipeNote,
            suggestPlan: true, functionCallCount: 0);

        note.ShouldBe(RecipeNote);
    }

    [Test]
    public void PlanNudge_AppliesOnlyWhileNoToolHasBeenCalled()
    {
        IterationNotePolicy.Select(false, PendingNote, false, RecipeNote, true, 0)
            .ShouldBe(PlanSkillDefaults.PlanNudgeNote);

        IterationNotePolicy.Select(false, PendingNote, false, RecipeNote, true, 1)
            .ShouldBeNull();
    }

    [Test]
    public void NoCandidate_YieldsNoNote()
    {
        IterationNotePolicy.Select(false, PendingNote, false, RecipeNote, false, 0)
            .ShouldBeNull();
    }
}
