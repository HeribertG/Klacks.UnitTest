// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the execution-time guard of the reply after a read-only recipe: side-effecting calls - including
/// confirm_pending_action, the known token trap - are rejected without running, read-only skills and navigation
/// still run, and without a completed read-only recipe the guard changes nothing. Also pins the plan facts the
/// guard depends on: which recipes count as read-only and that the final step's note is captured only when this
/// turn executed the final step.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class ReadOnlyRecipeWriteGuardTests
{
    private const string FinalNote = "final-note";

    private static LLMFunctionCall Call(string name) =>
        new() { FunctionName = name, Parameters = new Dictionary<string, object>(), Success = true };

    [TestCase("set_period_close_lag")]
    [TestCase(AutonomyDefaults.ConfirmPendingActionSkillName)]
    [TestCase("close_period")]
    public void CompletedReadOnlyRecipe_RejectsSideEffectingCalls(string skill)
    {
        var call = Call(skill);

        var executable = ReadOnlyRecipeWriteGuard.Reject(new List<LLMFunctionCall> { call }, true);

        executable.ShouldBeEmpty();
        call.Success.ShouldBeFalse();
        call.Result.ShouldBe(LLMLoopConstants.ReadOnlyRecipeWriteRejectedResult);
    }

    [TestCase("get_period_close_schedule")]
    [TestCase("list_open_periods")]
    [TestCase(SkillNames.NavigateTo)]
    public void CompletedReadOnlyRecipe_LetsReadsAndNavigationRun(string skill)
    {
        var call = Call(skill);

        ReadOnlyRecipeWriteGuard.Reject(new List<LLMFunctionCall> { call }, true).ShouldBe(new[] { call });
        call.Success.ShouldBeTrue();
    }

    [Test]
    public void WithoutCompletedReadOnlyRecipe_NothingIsRejected()
    {
        var calls = new List<LLMFunctionCall> { Call("set_period_close_lag") };

        ReadOnlyRecipeWriteGuard.Reject(calls, false).ShouldBeSameAs(calls);
    }

    [Test]
    public void Plan_OfAskAndSearchSteps_IsReadOnly_AndAMutateStepMakesItWriting()
    {
        Plan(RecipeStepKinds.Ask, RecipeStepKinds.Search).IsReadOnly.ShouldBeTrue();
        Plan(RecipeStepKinds.Ask, RecipeStepKinds.Mutate).IsReadOnly.ShouldBeFalse();
        Plan(RecipeStepKinds.Search, RecipeStepKinds.Verify).IsReadOnly.ShouldBeFalse();
    }

    [Test]
    public void Plan_CapturesTheFinalStepNoteOnlyWhenTheFinalStepRan()
    {
        var plan = new RecipeExecutionPlan(
            "r",
            new List<RecipeStep>
            {
                new() { Kind = RecipeStepKinds.Search, Skill = "get_a", Note = "first-note" },
                new() { Kind = RecipeStepKinds.Search, Skill = "get_b", Note = FinalNote }
            });

        plan.Observe(new[] { Call("get_a") });
        plan.CompletedThisTurn.ShouldBeFalse();
        plan.CompletionNote.ShouldBeNull();

        plan.Observe(new[] { Call("get_b") });
        plan.IsActive.ShouldBeFalse();
        plan.CompletedThisTurn.ShouldBeTrue();
        plan.CompletionNote.ShouldBe(FinalNote);
    }

    [Test]
    public void Plan_FailedFinalStep_DoesNotCountAsCompleted()
    {
        var plan = Plan(RecipeStepKinds.Search);
        var failed = Call("get_0");
        failed.Success = false;

        plan.Observe(new[] { failed });

        plan.CompletedThisTurn.ShouldBeFalse();
    }

    private static RecipeExecutionPlan Plan(params string[] kinds) =>
        new("r", kinds.Select((kind, index) => new RecipeStep
        {
            Kind = kind,
            Skill = kind == RecipeStepKinds.Ask ? null : $"get_{index}",
            Slot = kind == RecipeStepKinds.Ask ? $"slot{index}" : null,
            Prompt = kind == RecipeStepKinds.Ask ? "?" : null
        }).ToList());
}
