// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A recipe step whose skill is not in the turn's toolset cannot be forced. The forcing used to do nothing in silence:
/// the plan stayed active without ever pausing and the run row lingered as Running until the 24-hour expiry. Now the
/// plan ends, the run is aborted with its own reason and the turn continues with the full toolset. A plan waiting on
/// a question step has no skill to force and must be left alone, which the topic-switch turn relies on.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeTurnStateMissingStepSkillTests
{
    private const string ConversationId = "conv-recipe-missing-skill";
    private const string RecipeName = "create-group";
    private const string StepSkill = "create_group";
    private const string OtherSkill = "list_groups";

    private readonly Guid _userId = Guid.NewGuid();

    private ITurnPreparationService _preparation = null!;
    private IRecipeRunRecorder _recorder = null!;
    private RecipeEngineService _engine = null!;
    private RecipeRunHandle _run = null!;

    [SetUp]
    public void SetUp()
    {
        _preparation = Substitute.For<ITurnPreparationService>();
        _recorder = Substitute.For<IRecipeRunRecorder>();
        _run = new RecipeRunHandle(Guid.NewGuid(), RecipeName, _userId, ConversationId);
        _recorder.BeginOrResumeAsync(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(_run);
        _engine = new RecipeEngineService(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<IPendingRecipeStore>(),
            NullLogger<RecipeEngineService>.Instance);
    }

    [Test]
    public async Task StepSkillMissingFromTheToolset_EndsThePlan_AndAbortsTheRun()
    {
        var plan = new RecipeExecutionPlan(
            RecipeName, [new RecipeStep { Kind = RecipeStepKinds.Mutate, Skill = StepSkill }]);
        var state = await BeginAsync(plan);
        var toolset = new List<LLMFunction> { new() { Name = OtherSkill } };

        var (forced, functions, note) = state.ResolveIteration(false, toolset, toolset);
        await state.FinalizeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(forced, Is.False);
            Assert.That(functions, Is.SameAs(toolset));
            Assert.That(note, Is.Null);
            Assert.That(state.Forcing, Is.Null);
            Assert.That(plan.IsActive, Is.False);
        });
        await _recorder.Received(1).AbortAsync(
            _run, RecipeAbortReasons.StepSkillMissingFromToolset, Arg.Any<CancellationToken>());
        await _recorder.DidNotReceiveWithAnyArgs().CompleteAsync(default!, default);
    }

    [Test]
    public async Task StepSkillPresent_IsForcedAsBefore()
    {
        var plan = new RecipeExecutionPlan(
            RecipeName, [new RecipeStep { Kind = RecipeStepKinds.Mutate, Skill = StepSkill }]);
        var state = await BeginAsync(plan);
        var toolset = new List<LLMFunction> { new() { Name = StepSkill }, new() { Name = OtherSkill } };

        var (forced, functions, _) = state.ResolveIteration(false, toolset, toolset);

        Assert.Multiple(() =>
        {
            Assert.That(forced, Is.True);
            Assert.That(functions.Select(f => f.Name), Is.EqualTo(new[] { StepSkill }));
            Assert.That(plan.IsActive, Is.True);
        });
    }

    [Test]
    public async Task PlanWaitingOnAQuestionStep_IsLeftAlone()
    {
        var plan = new RecipeExecutionPlan(
            RecipeName,
            [
                new RecipeStep { Kind = RecipeStepKinds.Ask, Slot = "groupName", Prompt = "Which name?" },
                new RecipeStep { Kind = RecipeStepKinds.Mutate, Skill = StepSkill }
            ]);
        var state = await BeginAsync(plan);
        var toolset = new List<LLMFunction> { new() { Name = OtherSkill } };

        var (forced, _, _) = state.ResolveIteration(false, toolset, toolset);

        Assert.Multiple(() =>
        {
            Assert.That(forced, Is.False);
            Assert.That(plan.IsActive, Is.True);
            Assert.That(state.Forcing, Is.SameAs(plan));
        });
    }

    private Task<RecipeTurnState> BeginAsync(RecipeExecutionPlan plan)
    {
        _preparation.PrepareAsync(Arg.Any<TurnPreparationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TurnPreparation(plan, false, null, null));

        var context = new LLMContext
        {
            Message = "Erstelle die Gruppe Region Süd",
            UserId = _userId.ToString(),
            TurnId = Guid.NewGuid(),
            AvailableFunctions = [new LLMFunction { Name = OtherSkill, Effect = SkillEffect.Read }]
        };

        return RecipeTurnState.BeginAsync(
            _preparation, _recorder, _engine, NullLogger.Instance, context,
            Substitute.For<ILLMProvider>(), new LLMModel { ApiModelId = "m" }, ConversationId, CancellationToken.None);
    }
}
