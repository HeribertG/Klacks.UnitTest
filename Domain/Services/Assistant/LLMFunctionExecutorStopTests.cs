// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The tool half of a stopped turn: once the user asked for a stop, no further call of the round starts and
/// every call that did not run says so. A write skill is never handed the stop token - it runs to its end,
/// because repositories commit step by step - while a read the policy allows is. A read the stop cut short
/// counts as not executed, exactly like a call that was skipped before it started.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMFunctionExecutorStopTests
{
    private const string ReadSkill = "search_employees";
    private const string WriteSkill = "create_employee";
    private const string ApplySkill = "apply_proposal";
    private const int CallsOfTheRound = 5;
    private const int CallsThatRunBeforeTheStop = 2;

    private static readonly Guid AgentId = Guid.NewGuid();

    private ILLMSkillBridge _bridge = null!;
    private IPendingConfirmationStore _pending = null!;
    private ICancellableSkillPolicy _policy = null!;
    private LLMFunctionExecutor _executor = null!;
    private LLMContext _context = null!;
    private List<CancellationToken> _tokensSeen = null!;

    [SetUp]
    public void SetUp()
    {
        _bridge = Substitute.For<ILLMSkillBridge>();
        _pending = Substitute.For<IPendingConfirmationStore>();
        _policy = Substitute.For<ICancellableSkillPolicy>();
        _tokensSeen = new List<CancellationToken>();

        var agents = Substitute.For<IAgentRepository>();
        agents.GetDefaultAgentAsync().Returns(new Agent { Id = AgentId });
        var skills = Substitute.For<IAgentSkillRepository>();
        skills.GetEnabledAsync(AgentId).Returns(
            Enumerable.Range(1, CallsOfTheRound)
                .Select(index => new AgentSkill { Name = ProposeSkill(index), PairedApplySkill = ApplySkill })
                .ToList());

        _bridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _tokensSeen.Add(call.ArgAt<CancellationToken>(2));
                return Succeeded();
            });

        _executor = new LLMFunctionExecutor(
            Substitute.For<ILogger<LLMFunctionExecutor>>(), skills, agents, _pending, _bridge, _policy);
        _context = new LLMContext { UserId = Guid.NewGuid().ToString(), Message = "Do it." };
    }

    [Test]
    public async Task AStopAfterTheSecondOfFiveCalls_RunsExactlyTwoAndSkipsTheRest()
    {
        using var stop = new CancellationTokenSource();
        var runs = 0;
        _bridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (++runs == CallsThatRunBeforeTheStop)
                {
                    stop.Cancel();
                }

                return Succeeded();
            });
        var calls = Enumerable.Range(1, CallsOfTheRound).Select(index => Call(ProposeSkill(index))).ToList();

        await _executor.ProcessFunctionCallsAsync(_context, calls, stop.Token);

        runs.ShouldBe(CallsThatRunBeforeTheStop);
        calls.Take(CallsThatRunBeforeTheStop).ShouldAllBe(c => c.Success && !c.SkippedByStop);
        var skipped = calls.Skip(CallsThatRunBeforeTheStop).ToList();
        skipped.ShouldAllBe(c => c.SkippedByStop && !c.Success);
        skipped.ShouldAllBe(c => c.Result == TurnInterruptionDefaults.SkippedCallResult);
    }

    [Test]
    public async Task ASkippedCall_LeavesNoProposalHintBehind()
    {
        using var stop = new CancellationTokenSource();
        var runs = 0;
        _bridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (++runs == CallsThatRunBeforeTheStop)
                {
                    stop.Cancel();
                }

                return Succeeded();
            });
        var calls = Enumerable.Range(1, CallsOfTheRound).Select(index => Call(ProposeSkill(index))).ToList();

        await _executor.ProcessFunctionCallsAsync(_context, calls, stop.Token);

        _pending.Received(CallsThatRunBeforeTheStop).CreateProposalHint(Arg.Any<Guid>(), ApplySkill);
    }

    [Test]
    public async Task AStopBeforeTheFirstCall_RunsNothing()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var calls = Enumerable.Range(1, CallsOfTheRound).Select(index => Call(ProposeSkill(index))).ToList();

        await _executor.ProcessFunctionCallsAsync(_context, calls, stop.Token);

        await _bridge.DidNotReceiveWithAnyArgs().ExecuteSkillFromLLMCallAsync(default!, default!, default);
        calls.ShouldAllBe(c => c.SkippedByStop);
    }

    [Test]
    public async Task AReadTheStopMayCut_GetsTheStopTokenAndAWriteSkillGetsNone()
    {
        using var stop = new CancellationTokenSource();
        _policy.ReceivesStopToken(ReadSkill).Returns(true);
        _policy.ReceivesStopToken(WriteSkill).Returns(false);

        await _executor.ProcessFunctionCallsAsync(_context, [Call(ReadSkill), Call(WriteSkill)], stop.Token);

        _tokensSeen.Count.ShouldBe(2);
        _tokensSeen[0].ShouldBe(stop.Token);
        _tokensSeen[0].CanBeCanceled.ShouldBeTrue();
        _tokensSeen[1].CanBeCanceled.ShouldBeFalse();
    }

    [Test]
    public async Task WithoutAStopToken_NoSkillIsCancellableAndThePolicyIsNotAsked()
    {
        await _executor.ProcessFunctionCallsAsync(_context, [Call(ReadSkill), Call(WriteSkill)]);

        _tokensSeen.ShouldAllBe(token => !token.CanBeCanceled);
        _policy.DidNotReceiveWithAnyArgs().ReceivesStopToken(default!);
    }

    [Test]
    public async Task WithoutAPolicy_NoSkillIsCancellable()
    {
        using var stop = new CancellationTokenSource();
        var executor = new LLMFunctionExecutor(
            Substitute.For<ILogger<LLMFunctionExecutor>>(),
            Substitute.For<IAgentSkillRepository>(),
            Substitute.For<IAgentRepository>(),
            _pending,
            _bridge);

        await executor.ProcessFunctionCallsAsync(_context, [Call(ReadSkill)], stop.Token);

        _tokensSeen.ShouldHaveSingleItem().CanBeCanceled.ShouldBeFalse();
    }

    [Test]
    public async Task ThePolicyIsAskedForTheNormalizedSkillName()
    {
        using var stop = new CancellationTokenSource();

        await _executor.ProcessFunctionCallsAsync(_context, [Call("search_clients")], stop.Token);

        _policy.Received(1).ReceivesStopToken(ReadSkill);
    }

    [Test]
    public async Task AReadTheStopCutShort_CountsAsNotExecuted()
    {
        using var stop = new CancellationTokenSource();
        _policy.ReceivesStopToken(ReadSkill).Returns(true);
        _bridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                stop.Cancel();
                return new SkillBridgeResult { Success = false, ResultType = nameof(SkillResultType.Cancelled), Message = "cancelled" };
            });
        var call = Call(ReadSkill);

        await _executor.ProcessFunctionCallsAsync(_context, [call], stop.Token);

        call.SkippedByStop.ShouldBeTrue();
        call.Success.ShouldBeFalse();
        call.Result.ShouldBe(TurnInterruptionDefaults.SkippedCallResult);
        call.ResultKind.ShouldBe(LLMFunctionResultKind.Error);
    }

    [Test]
    public async Task AReadThatFailsWithAnOrdinaryErrorWhileTheStopCutIt_CountsAsNotExecuted()
    {
        using var stop = new CancellationTokenSource();
        _policy.ReceivesStopToken(ReadSkill).Returns(true);
        _bridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                stop.Cancel();
                return new SkillBridgeResult { Success = false, ResultType = nameof(SkillResultType.Error), Message = "search failed" };
            });
        var call = Call(ReadSkill);

        await _executor.ProcessFunctionCallsAsync(_context, [call], stop.Token);

        call.SkippedByStop.ShouldBeTrue();
        call.Success.ShouldBeFalse();
        call.Result.ShouldBe(TurnInterruptionDefaults.SkippedCallResult);
    }

    [Test]
    public async Task AWriteThatFailsWhileTheStopIsRequested_StaysAnOrdinaryFailure()
    {
        using var stop = new CancellationTokenSource();
        _policy.ReceivesStopToken(WriteSkill).Returns(false);
        _bridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                stop.Cancel();
                return new SkillBridgeResult { Success = false, ResultType = nameof(SkillResultType.Error), Message = "write failed" };
            });
        var call = Call(WriteSkill);

        await _executor.ProcessFunctionCallsAsync(_context, [call], stop.Token);

        call.SkippedByStop.ShouldBeFalse();
        call.Success.ShouldBeFalse();
        call.Result.ShouldContain("write failed");
    }

    [Test]
    public async Task AReadThatSucceedsAndIsStoppedAfterwards_StaysExecuted()
    {
        using var stop = new CancellationTokenSource();
        _policy.ReceivesStopToken(ReadSkill).Returns(true);
        _bridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                stop.Cancel();
                return Succeeded();
            });
        var call = Call(ReadSkill);

        await _executor.ProcessFunctionCallsAsync(_context, [call], stop.Token);

        call.SkippedByStop.ShouldBeFalse();
        call.Success.ShouldBeTrue();
    }

    [Test]
    public async Task ACancelledResultWithoutAStopRequest_IsAnOrdinaryFailureNotASkip()
    {
        using var stop = new CancellationTokenSource();
        _bridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new SkillBridgeResult { Success = false, ResultType = nameof(SkillResultType.Cancelled), Message = "declined" });
        var call = Call(ReadSkill);

        await _executor.ProcessFunctionCallsAsync(_context, [call], stop.Token);

        call.SkippedByStop.ShouldBeFalse();
        call.Success.ShouldBeFalse();
    }

    private static string ProposeSkill(int index) => $"propose_{index}";

    private static LLMFunctionCall Call(string skill) => new() { FunctionName = skill };

    private static SkillBridgeResult Succeeded() =>
        new() { Success = true, ResultType = nameof(SkillResultType.Data), Message = "Done." };
}
