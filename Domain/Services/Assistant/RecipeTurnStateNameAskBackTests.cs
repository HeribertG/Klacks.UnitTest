// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The same-turn self-confirmation hole of recipe forcing: a forced step that failed is forced again on the next
/// iteration with only that skill and tool_choice=required. When the failure was a name-resolution ask-back
/// ("Did the user mean ...?", several matching names), the model could only call the step again with the
/// suggested name instead of asking the user. Such an ask-back now releases the forcing (recorded as an aborted
/// run), so the following iteration is unforced and the repeated-write guard rejects the repeat as on main. Any
/// other failure keeps the forcing, and the forced retry with corrected parameters still runs in the same turn.
/// </summary>

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
    private const string RecipeName = "add-employee-to-group";
    private const string MutateSkill = "add_client_to_group_by_name";
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
    public async Task NameAskBack_ReleasesTheForcing_AndTheRepeatIsRejected()
    {
        var state = await BeginAsync(Plan());
        var functions = new List<LLMFunction> { new() { Name = MutateSkill } };
        var called = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var (firstForced, _, _) = state.ResolveIteration(false, functions, functions);
        var first = Call("Region South");
        RepeatedWriteCallGuard.RejectAndRecord([first], called, firstForced);
        Fail(first, "Error: " + NameResolutionMessages.SuggestedName("Region North"));
        state.ObserveExecuted([first]);

        var (secondForced, secondFunctions, _) = state.ResolveIteration(false, functions, functions);
        var repeat = Call("Region North");
        var executable = RepeatedWriteCallGuard.RejectAndRecord([repeat], called, secondForced);
        await state.FinalizeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(firstForced, Is.True);
            Assert.That(state.Forcing, Is.Null);
            Assert.That(secondForced, Is.False);
            Assert.That(secondFunctions, Is.SameAs(functions));
            Assert.That(executable, Is.Empty);
            Assert.That(repeat.IsRejectedRepeat, Is.True);
        });
        await _recorder.Received(1).AbortAsync(_run, RecipeAbortReasons.NameAskBackEndedRecipe, Arg.Any<CancellationToken>());
        await _recorder.DidNotReceiveWithAnyArgs().CompleteAsync(default!, default);
    }

    [Test]
    public async Task FixableFailure_KeepsTheForcing_AndTheCorrectedRetryRuns()
    {
        var state = await BeginAsync(Plan());
        var functions = new List<LLMFunction> { new() { Name = MutateSkill } };
        var called = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var (firstForced, _, _) = state.ResolveIteration(false, functions, functions);
        var first = Call("Region North");
        RepeatedWriteCallGuard.RejectAndRecord([first], called, firstForced);
        Fail(first, InvalidDateResult);
        state.ObserveExecuted([first]);

        var (secondForced, _, _) = state.ResolveIteration(false, functions, functions);
        var retry = Call("Region North");
        var executable = RepeatedWriteCallGuard.RejectAndRecord([retry], called, secondForced);
        retry.Result = "Client added to group 'Region North'.";
        state.ObserveExecuted([retry]);
        await state.FinalizeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(secondForced, Is.True);
            Assert.That(executable, Has.Count.EqualTo(1));
            Assert.That(retry.IsRejectedRepeat, Is.False);
            Assert.That(state.RecipeCompleted, Is.True);
        });
        await _recorder.Received(1).CompleteAsync(_run, Arg.Any<CancellationToken>());
        await _recorder.DidNotReceiveWithAnyArgs().AbortAsync(default!, default!, default);
    }

    [Test]
    public async Task WithoutARecipe_AnAskBackChangesNothing_AndARepeatIsRejectedAsOnMain()
    {
        _preparation.PrepareAsync(Arg.Any<TurnPreparationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TurnPreparation(null, false, null, null));
        var state = await StartAsync();
        var called = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var first = Call("Region South");
        RepeatedWriteCallGuard.RejectAndRecord([first], called, forceRecipe: false);
        Fail(first, "Error: " + NameResolutionMessages.SuggestedName("Region North"));
        state.ObserveExecuted([first]);

        var repeat = Call("Region North");
        var executable = RepeatedWriteCallGuard.RejectAndRecord([repeat], called, forceRecipe: false);

        Assert.Multiple(() =>
        {
            Assert.That(state.Forcing, Is.Null);
            Assert.That(executable, Is.Empty);
            Assert.That(repeat.IsRejectedRepeat, Is.True);
        });
        await _recorder.DidNotReceiveWithAnyArgs().AbortAsync(default!, default!, default);
    }

    private static void Fail(LLMFunctionCall call, string result)
    {
        call.Success = false;
        call.Result = result;
    }

    private static LLMFunctionCall Call(string groupName) => new()
    {
        FunctionName = MutateSkill,
        Parameters = new Dictionary<string, object> { ["groupName"] = groupName }
    };

    private static RecipeExecutionPlan Plan() => new(
        RecipeName,
        [new RecipeStep { Kind = RecipeStepKinds.Mutate, Skill = MutateSkill }]);

    private Task<RecipeTurnState> BeginAsync(RecipeExecutionPlan plan)
    {
        _preparation.PrepareAsync(Arg.Any<TurnPreparationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TurnPreparation(plan, false, null, null));
        return StartAsync();
    }

    private Task<RecipeTurnState> StartAsync()
    {
        var context = new LLMContext
        {
            Message = "Add Max to the group Region South",
            UserId = _userId.ToString(),
            TurnId = Guid.NewGuid()
        };

        return RecipeTurnState.BeginAsync(
            _preparation, _recorder, _engine, NullLogger.Instance, context,
            Substitute.For<ILLMProvider>(), new LLMModel { ApiModelId = "m" }, ConversationId, CancellationToken.None);
    }
}
