// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The post-turn hooks of a turn the user cut off: compaction, the skill-execution audit and the trajectory
/// capture run, the last one with the phase the turn was interrupted in and only the calls that really ran.
/// Everything that would learn from a truncated answer - memory extraction, learning-case collection,
/// grounding, failure reflection - does not. The hooks run fire-and-forget, so the test waits for those it
/// expects and gives the skipped ones a grace period to show up.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMBackgroundTaskServiceStoppedTurnTests
{
    private const string UserMessage = "Create the employee Anna Meier.";
    private const string StoredAnswer = "Partial\n[interrupted by user]";
    private const string SkillName = "create_employee";
    private static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SkippedHookGrace = TimeSpan.FromMilliseconds(300);

    private IConversationCompactionService _compaction = null!;
    private IAutoMemoryExtractionService _memory = null!;
    private ISkillLearningCaseCollector _learning = null!;
    private IAgentSkillRepository _skills = null!;
    private ITrajectoryCaptureService _trajectory = null!;
    private IAnswerGroundingEvaluator _grounding = null!;
    private ITurnReflectionService _reflection = null!;
    private TaskCompletionSource _compactionRan = null!;
    private TaskCompletionSource _auditRan = null!;
    private TaskCompletionSource _trajectoryRan = null!;
    private LLMBackgroundTaskService _service = null!;
    private List<string> _auditedSkills = null!;

    [SetUp]
    public void SetUp()
    {
        _compactionRan = NewSignal();
        _auditRan = NewSignal();
        _trajectoryRan = NewSignal();
        _auditedSkills = new List<string>();

        _compaction = Substitute.For<IConversationCompactionService>();
        _compaction.CompactIfNeededAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Signal(_compactionRan));
        _memory = Substitute.For<IAutoMemoryExtractionService>();
        _learning = Substitute.For<ISkillLearningCaseCollector>();
        _grounding = Substitute.For<IAnswerGroundingEvaluator>();
        _reflection = Substitute.For<ITurnReflectionService>();
        _skills = Substitute.For<IAgentSkillRepository>();
        _skills.GetByNameAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _auditedSkills.Add(call.ArgAt<string>(1));
                _auditRan.TrySetResult();
                return Task.FromResult<AgentSkill?>(null);
            });
        _trajectory = Substitute.For<ITrajectoryCaptureService>();
        _trajectory.CaptureAsync(
                Arg.Any<Guid>(), Arg.Any<LLMContext>(), Arg.Any<string>(), Arg.Any<List<LLMFunctionCall>>(),
                Arg.Any<string?>())
            .Returns(_ => Signal(_trajectoryRan));

        var services = new ServiceCollection();
        services.AddSingleton(_compaction);
        services.AddSingleton(_memory);
        services.AddSingleton(_learning);
        services.AddSingleton(_skills);
        services.AddSingleton(_trajectory);
        services.AddSingleton(_grounding);
        services.AddSingleton(_reflection);
        var provider = services.BuildServiceProvider();

        _service = new LLMBackgroundTaskService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<ILogger<LLMBackgroundTaskService>>());
    }

    [Test]
    public async Task ACutOffTurn_RunsCompactionAuditAndTheInterruptedTrajectoryCapture()
    {
        var executed = new List<LLMFunctionCall> { new() { FunctionName = SkillName, Success = true } };

        _service.RunStoppedTurnTasks(
            new Agent { Id = Guid.NewGuid() }, Conversation(), Context(), StoredAnswer, executed,
            InterruptedTurnPhases.DuringTools);

        await WaitFor(_compactionRan, _auditRan, _trajectoryRan);

        await _trajectory.Received(1).CaptureAsync(
            Arg.Any<Guid>(), Arg.Any<LLMContext>(), StoredAnswer, executed, InterruptedTurnPhases.DuringTools);
        _auditedSkills.ShouldBe([SkillName]);
    }

    [Test]
    public async Task ACutOffTurn_NeverFeedsMemoryLearningGroundingOrReflection()
    {
        var failedButExecuted = new List<LLMFunctionCall>
        {
            new() { FunctionName = SkillName, Success = false, Result = "Error: no such employee" }
        };

        _service.RunStoppedTurnTasks(
            new Agent { Id = Guid.NewGuid() }, Conversation(), Context(), StoredAnswer, failedButExecuted,
            InterruptedTurnPhases.DuringText);

        await WaitFor(_compactionRan, _auditRan, _trajectoryRan);
        await Task.Delay(SkippedHookGrace);

        await _memory.DidNotReceiveWithAnyArgs().ExtractAndStoreMemoriesAsync(default, default!, default!, default!);
        await _learning.DidNotReceiveWithAnyArgs().CollectFromTurnAsync(default!, default);
        await _grounding.DidNotReceiveWithAnyArgs().EvaluateAsync(default, default!, default!, default!, default);
        await _reflection.DidNotReceiveWithAnyArgs().ReflectAsync(default!, default);
    }

    [Test]
    public async Task WithoutAnAgent_OnlyTheCompactionRuns()
    {
        _service.RunStoppedTurnTasks(
            null, Conversation(), Context(), StoredAnswer, new List<LLMFunctionCall>(), InterruptedTurnPhases.BeforeText);

        await WaitFor(_compactionRan);
        await Task.Delay(SkippedHookGrace);

        await _trajectory.DidNotReceiveWithAnyArgs().CaptureAsync(default, default!, default!, default!, default);
        await _skills.DidNotReceiveWithAnyArgs().GetByNameAsync(default, default!, default);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task Signal(TaskCompletionSource signal)
    {
        signal.TrySetResult();
        return Task.CompletedTask;
    }

    private static async Task WaitFor(params TaskCompletionSource[] signals) =>
        await Task.WhenAll(signals.Select(signal => signal.Task)).WaitAsync(HookTimeout);

    private static LLMConversation Conversation() => new()
    {
        ConversationId = Guid.NewGuid().ToString(),
        UserId = Guid.NewGuid().ToString()
    };

    private static LLMContext Context() => new()
    {
        Message = UserMessage,
        UserId = Guid.NewGuid().ToString(),
        Language = "en",
        AvailableFunctions = new List<LLMFunction> { new() { Name = SkillName } }
    };
}
