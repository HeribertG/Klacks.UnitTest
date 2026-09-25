// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the running record of a streamed turn: the phase a stop or a dropped connection interrupted is
/// derived from what the turn has produced, the outcome can be claimed exactly once even when the turn
/// and the safety net race for it, and a new turn starts from a clean record.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Assistant.Providers;

namespace Klacks.UnitTest.Domain.Models.Assistant;

[TestFixture]
public class TurnRunStateTests
{
    private const int RacingClaimants = 64;

    private TurnRunState _state = null!;

    [SetUp]
    public void SetUp()
    {
        _state = new TurnRunState();
        _state.Begin(new LLMContext { TurnId = Guid.NewGuid() });
    }

    [Test]
    public void Phase_WithoutTextAndWithoutCalls_IsBeforeText()
    {
        _state.Phase.ShouldBe(InterruptedTurnPhases.BeforeText);
    }

    [Test]
    public void Phase_WithTextAndWithoutCalls_IsDuringText()
    {
        _state.StreamedContent.Append("Sure, I will");

        _state.Phase.ShouldBe(InterruptedTurnPhases.DuringText);
    }

    [Test]
    public void Phase_WithCallsAndNoTextAfterThem_IsDuringTools()
    {
        _state.StreamedContent.Append("Let me create that.");
        _state.RegisterCalls([new LLMFunctionCall { FunctionName = "create_employee" }]);

        _state.Phase.ShouldBe(InterruptedTurnPhases.DuringTools);
    }

    [Test]
    public void Phase_WithCallsAndNoTextAtAll_IsDuringTools()
    {
        _state.RegisterCalls([new LLMFunctionCall { FunctionName = "create_employee" }]);

        _state.Phase.ShouldBe(InterruptedTurnPhases.DuringTools);
    }

    [Test]
    public void Phase_WithTextStreamedAfterTheLastCalls_IsDuringText()
    {
        _state.RegisterCalls([new LLMFunctionCall { FunctionName = "create_employee" }]);
        _state.StreamedContent.Append("The employee is created.");

        _state.Phase.ShouldBe(InterruptedTurnPhases.DuringText);
    }

    [Test]
    public void RegisterCalls_KeepsTheCallsInTheOrderTheyWereRegistered()
    {
        var first = new LLMFunctionCall { FunctionName = "first" };
        var second = new LLMFunctionCall { FunctionName = "second" };

        _state.RegisterCalls([first]);
        _state.RegisterCalls([second]);

        _state.Calls.ShouldBe([first, second]);
    }

    [Test]
    public void Outcome_BeforeAnyClaim_IsNull()
    {
        _state.Outcome.ShouldBeNull();
    }

    [Test]
    public void TrySetOutcome_TheFirstClaimWins_TheSecondIsRefusedAndTheValueStays()
    {
        _state.TrySetOutcome(TurnOutcome.Stopped).ShouldBeTrue();
        _state.TrySetOutcome(TurnOutcome.Completed).ShouldBeFalse();

        _state.Outcome.ShouldBe(TurnOutcome.Stopped);
    }

    [Test]
    public void TrySetOutcome_WhenManyThreadsRace_ExactlyOneWins()
    {
        var winners = 0;
        var outcomes = Enum.GetValues<TurnOutcome>();

        Parallel.For(0, RacingClaimants, index =>
        {
            if (_state.TrySetOutcome(outcomes[index % outcomes.Length]))
            {
                Interlocked.Increment(ref winners);
            }
        });

        winners.ShouldBe(1);
        _state.Outcome.ShouldNotBeNull();
    }

    [Test]
    public void StopRequested_FollowsTheStopTokenOfTheContext()
    {
        using var stop = new CancellationTokenSource();
        _state.Begin(new LLMContext { StopToken = stop.Token });

        _state.StopRequested.ShouldBeFalse();

        stop.Cancel();

        _state.StopRequested.ShouldBeTrue();
    }

    [Test]
    public void Begin_StartsFromACleanRecord()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        _state.Begin(new LLMContext { StopToken = stop.Token });
        _state.StreamedContent.Append("text of an earlier turn");
        _state.RegisterCalls([new LLMFunctionCall { FunctionName = "create_employee" }]);
        _state.ToolIterations = 3;
        _state.TrySetOutcome(TurnOutcome.Completed);

        var nextContext = new LLMContext { TurnId = Guid.NewGuid() };
        _state.Begin(nextContext);

        _state.Context.ShouldBeSameAs(nextContext);
        _state.StreamedContent.Length.ShouldBe(0);
        _state.Calls.ShouldBeEmpty();
        _state.ToolIterations.ShouldBe(0);
        _state.Outcome.ShouldBeNull();
        _state.StopRequested.ShouldBeFalse();
        _state.Phase.ShouldBe(InterruptedTurnPhases.BeforeText);
    }
}
