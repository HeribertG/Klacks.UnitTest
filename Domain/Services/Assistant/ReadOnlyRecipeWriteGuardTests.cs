// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

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
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class ReadOnlyRecipeWriteGuardTests
{
    private const string FinalNote = "final-note";
    private const string Rejected = LLMLoopConstants.ReadOnlyRecipeWriteRejectedResult;

    private static LLMFunctionCall Call(string name) =>
        new() { FunctionName = name, Parameters = new Dictionary<string, object>(), Success = true };

    [TestCase("set_period_close_lag")]
    [TestCase(AutonomyDefaults.ConfirmPendingActionSkillName)]
    [TestCase("close_period")]
    public void CompletedReadOnlyRecipe_RejectsSideEffectingCalls(string skill)
    {
        var call = Call(skill);

        var executable = ReadOnlyRecipeWriteGuard.Reject(new List<LLMFunctionCall> { call }, true, Rejected, null);

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

        ReadOnlyRecipeWriteGuard.Reject(new List<LLMFunctionCall> { call }, true, Rejected, null).ShouldBe(new[] { call });
        call.Success.ShouldBeTrue();
    }

    [Test]
    public void WithoutCompletedReadOnlyRecipe_NothingIsRejected()
    {
        var calls = new List<LLMFunctionCall> { Call("set_period_close_lag") };

        ReadOnlyRecipeWriteGuard.Reject(calls, false, Rejected, null).ShouldBeSameAs(calls);
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

    private static LLMFunction Function(string name, SkillEffect? effect) => new() { Name = name, Effect = effect };

    [TestCase("explain_period_closing", SkillEffect.Explain)]
    [TestCase("select_group", SkillEffect.Read)]
    [TestCase("open_order_export", SkillEffect.Read)]
    [TestCase("suggest_next_step", SkillEffect.Advise)]
    public void CompletedRecipe_LetsExplainReadAndAdviseSkillsRunWhateverTheirName(string skill, SkillEffect effect)
    {
        var call = Call(skill);

        ReadOnlyRecipeWriteGuard.Reject(new List<LLMFunctionCall> { call }, true, Rejected, new[] { Function(skill, effect) })
            .ShouldBe(new[] { call });
    }

    [TestCase("start_guided_tour")]
    [TestCase("create_plan")]
    [TestCase("check_erp_drop_point_folder_health")]
    [TestCase(AutonomyDefaults.ConfirmPendingActionSkillName)]
    public void CompletedRecipe_RejectsMutateSkills_EvenWithAReadPrefixOrAnUngatedRiskClass(string skill)
    {
        var call = Call(skill);

        ReadOnlyRecipeWriteGuard.Reject(new List<LLMFunctionCall> { call }, true, Rejected, new[] { Function(skill, SkillEffect.Mutate) })
            .ShouldBeEmpty();
        call.Result.ShouldBe(Rejected);
    }

    [Test]
    public void CompletedRecipe_ReadActionOfAMultiActionSkill_Runs()
    {
        var call = new LLMFunctionCall
        {
            FunctionName = SkillNames.ManagePendingNotes,
            Parameters = new Dictionary<string, object> { [ReadOnlySkillActions.ActionParameter] = ReadOnlySkillActions.PendingNotesRead },
            Success = true
        };

        ReadOnlyRecipeWriteGuard.Reject(
                new List<LLMFunctionCall> { call }, true, Rejected, new[] { Function(SkillNames.ManagePendingNotes, SkillEffect.Mutate) })
            .ShouldBe(new[] { call });
    }

    [TestCase("get_period_close_schedule", true)]
    [TestCase("explain_period_closing", false)]
    [TestCase("set_period_close_lag", false)]
    public void SkillOutsideTheToolset_FallsBackToTheReadPrefix(string skill, bool expected)
    {
        ReadOnlyRecipeWriteGuard.IsSideEffectFree(Call(skill), Array.Empty<LLMFunction>()).ShouldBe(expected);
    }

    [Test]
    public void FunctionWithoutAnEffect_FallsBackToTheReadPrefix()
    {
        ReadOnlyRecipeWriteGuard.IsSideEffectFree(Call("explain_x"), new[] { Function("explain_x", null) }).ShouldBeFalse();
        ReadOnlyRecipeWriteGuard.IsSideEffectFree(Call("get_x"), new[] { Function("get_x", null) }).ShouldBeTrue();
    }

    [Test]
    public void CompletedWritingRecipe_UsesItsOwnRejectionText()
    {
        var call = Call("delete_break");

        ReadOnlyRecipeWriteGuard.Reject(
                new List<LLMFunctionCall> { call }, true, LLMLoopConstants.CompletedRecipeWriteRejectedResult, null)
            .ShouldBeEmpty();
        call.Result.ShouldBe(LLMLoopConstants.CompletedRecipeWriteRejectedResult);
    }

    [Test]
    public void Plan_FinalStepHeldByTheAutonomyGate_DoesNotCountAsCompleted()
    {
        var plan = new RecipeExecutionPlan(
            "r",
            new List<RecipeStep> { new() { Kind = RecipeStepKinds.Mutate, Skill = "close_period", Note = FinalNote } });
        var held = Call("close_period");
        held.Success = false;
        held.RequiresConfirmation = true;

        plan.Observe(new[] { held });

        plan.CompletedThisTurn.ShouldBeFalse();
        plan.CompletionNote.ShouldBeNull();
    }

    [TestCase(false)]
    [TestCase(null)]
    public void CompletedRecipe_StillRejectsTheGroupingPlanPreview(bool? apply)
    {
        var parameters = new Dictionary<string, object>();
        if (apply is bool value)
        {
            parameters[PreviewApplySkillCalls.ApplyParameter] = value;
        }

        var call = new LLMFunctionCall { FunctionName = GroupingSkillNames.Apply, Parameters = parameters, Success = true };

        ReadOnlyRecipeWriteGuard.Reject(
                new List<LLMFunctionCall> { call }, true, Rejected, new[] { Function(GroupingSkillNames.Apply, SkillEffect.Mutate) })
            .ShouldBeEmpty();
        ReadOnlyRecipeWriteGuard.IsSideEffectFree(call, null).ShouldBeFalse();
    }
}
