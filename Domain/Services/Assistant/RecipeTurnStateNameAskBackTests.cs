// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Name ask-backs in a chat turn ("Did the user mean ...?", several matching names): a forced step that failed
/// with one rewinds to the question step that filled its name (onboard-employee shape), so the turn asks the user
/// and the recipe continues afterwards; without such a question step the forcing is released and the run aborted.
/// From the ask-back on, every side-effecting call of the turn is rejected, whatever its skill (also a write by id
/// after a lenient read), while reads and previews still run. Any other failure keeps the forcing and its
/// corrected retry, and a successful call that merely carries the marker text (a search suggestion) changes
/// nothing.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeTurnStateNameAskBackTests
{
    private const string ConversationId = "conv-recipe-ask-back";
    private const string RecipeName = "onboard-employee";
    private const string ContractSkill = "assign_contract_by_name";
    private const string GroupSkill = "add_client_to_group_by_name";
    private const string ContractSlot = "contractName";
    private const string GroupSlot = "groupName";
    private const string ReadSkill = "get_group_details";
    private const string WriteByIdSkill = "add_client_to_group";
    private const string InvalidDateResult = "Error: Invalid validFrom '01.13.2026'. Use the format yyyy-MM-dd.";

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
    public async Task NameAskBackOfAnInjectedStep_RewindsToItsQuestion_AndTheRecipeContinues()
    {
        var plan = OnboardingPlan(stepIndex: 1, new Dictionary<string, string> { [ContractSlot] = "Volzeit 160" });
        var state = await BeginAsync(plan);
        var functions = new List<LLMFunction> { new() { Name = ContractSkill } };

        var (forced, _, _) = state.ResolveIteration(false, functions, functions);
        var call = Call(ContractSkill, "Vollzeit 160 BE");
        Fail(call, AskBack("Vollzeit 160 BE"));
        state.ObserveExecuted([call]);

        var (forcedAfter, _, _) = state.ResolveIteration(false, functions, functions);
        await state.PauseOnAskAsync(CancellationToken.None);
        await state.FinalizeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(forced, Is.True);
            Assert.That(state.NameAskBackSeen, Is.True);
            Assert.That(plan.IsActive, Is.True);
            Assert.That(plan.CurrentIsAsk, Is.True);
            Assert.That(plan.CurrentStep!.Slot, Is.EqualTo(ContractSlot));
            Assert.That(plan.StepIndex, Is.EqualTo(0));
            Assert.That(state.Forcing, Is.SameAs(plan));
            Assert.That(forcedAfter, Is.False);
            Assert.That(state.AskedSlot, Is.EqualTo(ContractSlot));
        });
        await _recorder.DidNotReceiveWithAnyArgs().AbortAsync(default!, default!, default);
        await _recorder.DidNotReceiveWithAnyArgs().CompleteAsync(default!, default);
    }

    [Test]
    public async Task NameAskBackOfAStepWithoutQuestion_ReleasesTheForcing_AndAbortsTheRun()
    {
        var plan = new RecipeExecutionPlan(
            RecipeName, [new RecipeStep { Kind = RecipeStepKinds.Mutate, Skill = GroupSkill }]);
        var state = await BeginAsync(plan);
        var functions = new List<LLMFunction> { new() { Name = GroupSkill } };
        var called = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var (forced, _, _) = state.ResolveIteration(false, functions, functions);
        var first = Call(GroupSkill, "Region South");
        RepeatedWriteCallGuard.RejectAndRecord([first], called, forced);
        Fail(first, AskBack("Region North"));
        state.ObserveExecuted([first]);

        var (forcedAfter, _, _) = state.ResolveIteration(false, functions, functions);
        var repeat = Call(GroupSkill, "Region North");
        var executable = state.RejectWrites(RepeatedWriteCallGuard.RejectAndRecord([repeat], called, forcedAfter));
        await state.FinalizeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(forced, Is.True);
            Assert.That(state.Forcing, Is.Null);
            Assert.That(forcedAfter, Is.False);
            Assert.That(executable, Is.Empty);
            Assert.That(repeat.IsRejectedWithoutRunning, Is.True);
        });
        await _recorder.Received(1).AbortAsync(_run, RecipeAbortReasons.NameAskBackEndedRecipe, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FixableFailure_KeepsTheForcing_AndTheCorrectedRetryRuns()
    {
        var plan = new RecipeExecutionPlan(
            RecipeName, [new RecipeStep { Kind = RecipeStepKinds.Mutate, Skill = GroupSkill }]);
        var state = await BeginAsync(plan);
        var functions = new List<LLMFunction> { new() { Name = GroupSkill } };
        var called = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var (firstForced, _, _) = state.ResolveIteration(false, functions, functions);
        var first = Call(GroupSkill, "Region North");
        state.RejectWrites(RepeatedWriteCallGuard.RejectAndRecord([first], called, firstForced));
        Fail(first, InvalidDateResult);
        state.ObserveExecuted([first]);

        var (secondForced, _, _) = state.ResolveIteration(false, functions, functions);
        var retry = Call(GroupSkill, "Region North");
        var executable = state.RejectWrites(RepeatedWriteCallGuard.RejectAndRecord([retry], called, secondForced));
        retry.Result = "Client added to group 'Region North'.";
        state.ObserveExecuted([retry]);
        await state.FinalizeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(state.NameAskBackSeen, Is.False);
            Assert.That(secondForced, Is.True);
            Assert.That(executable, Has.Count.EqualTo(1));
            Assert.That(retry.IsRejectedWithoutRunning, Is.False);
            Assert.That(state.RecipeCompleted, Is.True);
        });
        await _recorder.Received(1).CompleteAsync(_run, Arg.Any<CancellationToken>());
        await _recorder.DidNotReceiveWithAnyArgs().AbortAsync(default!, default!, default);
    }

    [Test]
    public async Task AfterANameAskBack_EveryWriteOfTheTurnIsRejected_ReadsAndPreviewsStillRun()
    {
        var state = await BeginWithoutRecipeAsync();
        var asked = Call(GroupSkill, "Region South");
        Fail(asked, AskBack("Region North"));
        state.ObserveExecuted([asked]);

        var writeById = Call(WriteByIdSkill, "Region North");
        var read = Call(ReadSkill, "Region South");
        var preview = new LLMFunctionCall
        {
            FunctionName = GroupingSkillNames.Apply,
            Parameters = new Dictionary<string, object>
            {
                [PreviewApplySkillCalls.ApplyParameter] = JsonSerializer.SerializeToElement(false)
            }
        };
        var executable = state.RejectWrites([writeById, read, preview]);

        Assert.Multiple(() =>
        {
            Assert.That(state.NameAskBackSeen, Is.True);
            Assert.That(executable, Is.EquivalentTo(new[] { read, preview }));
            Assert.That(writeById.IsRejectedAfterNameAskBack, Is.True);
            Assert.That(writeById.Success, Is.False);
            Assert.That(writeById.Result, Is.EqualTo(LLMLoopConstants.NameAskBackWriteRejectedResult));
        });
    }

    [Test]
    public async Task SuccessfulCallCarryingTheMarkerText_IsIgnored()
    {
        var plan = new RecipeExecutionPlan(
            RecipeName, [new RecipeStep { Kind = RecipeStepKinds.Mutate, Skill = GroupSkill }]);
        var state = await BeginAsync(plan);
        var search = new LLMFunctionCall
        {
            FunctionName = "search_shifts",
            Success = true,
            Result = NameResolutionMessages.SuggestedName("Nachtdienst") + "\nData: {\"TotalCount\":0,\"Shifts\":[]}"
        };
        state.ObserveExecuted([search]);

        var write = Call(WriteByIdSkill, "Region North");
        var executable = state.RejectWrites([write]);

        Assert.Multiple(() =>
        {
            Assert.That(state.NameAskBackSeen, Is.False);
            Assert.That(state.Forcing, Is.SameAs(plan));
            Assert.That(executable, Has.Count.EqualTo(1));
        });
    }

    private static string AskBack(string suggestedName) => "Error: " + NameResolutionMessages.SuggestedName(suggestedName);

    private static void Fail(LLMFunctionCall call, string result)
    {
        call.Success = false;
        call.Result = result;
    }

    private static LLMFunctionCall Call(string skill, string name) => new()
    {
        FunctionName = skill,
        Parameters = new Dictionary<string, object> { [GroupSlot] = name }
    };

    private static RecipeExecutionPlan OnboardingPlan(int stepIndex, Dictionary<string, string> slots) => new(
        RecipeName,
        [
            new RecipeStep { Kind = RecipeStepKinds.Ask, Slot = ContractSlot, Prompt = "Which contract?" },
            new RecipeStep
            {
                Kind = RecipeStepKinds.Mutate, Skill = ContractSkill,
                Inject = new Dictionary<string, string> { [ContractSlot] = "$" + ContractSlot }
            },
            new RecipeStep { Kind = RecipeStepKinds.Ask, Slot = GroupSlot, Prompt = "Which group?" },
            new RecipeStep
            {
                Kind = RecipeStepKinds.Mutate, Skill = GroupSkill,
                Inject = new Dictionary<string, string> { [GroupSlot] = "$" + GroupSlot }
            },
        ],
        slots,
        stepIndex);

    private Task<RecipeTurnState> BeginAsync(RecipeExecutionPlan plan)
    {
        _preparation.PrepareAsync(Arg.Any<TurnPreparationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TurnPreparation(plan, false, null, null));
        return StartAsync();
    }

    private Task<RecipeTurnState> BeginWithoutRecipeAsync()
    {
        _preparation.PrepareAsync(Arg.Any<TurnPreparationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TurnPreparation(null, false, null, null));
        return StartAsync();
    }

    private Task<RecipeTurnState> StartAsync()
    {
        var context = new LLMContext
        {
            Message = "Add Max to the group Region South",
            UserId = _userId.ToString(),
            TurnId = Guid.NewGuid(),
            AvailableFunctions =
            [
                new LLMFunction { Name = GroupSkill, Effect = SkillEffect.Mutate },
                new LLMFunction { Name = WriteByIdSkill, Effect = SkillEffect.Mutate },
                new LLMFunction { Name = ReadSkill, Effect = SkillEffect.Read },
                new LLMFunction { Name = GroupingSkillNames.Apply, Effect = SkillEffect.Mutate },
            ]
        };

        return RecipeTurnState.BeginAsync(
            _preparation, _recorder, _engine, NullLogger.Instance, context,
            Substitute.For<ILLMProvider>(), new LLMModel { ApiModelId = "m" }, ConversationId, CancellationToken.None);
    }
}
