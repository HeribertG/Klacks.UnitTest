// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// A turn whose answer is an empty-answer notice must not feed the hooks that read the answer text: no
/// auto-memory, no learning case, no grounding verdict. The hooks that never read it keep running -
/// compaction, the skill-execution audit and the trajectory, which also labels the previous turn from this
/// turn's message. An ordinary answer still reaches every hook. The hooks run fire-and-forget, so each test
/// waits for the hooks it expects and, on the notice turn, gives a skipped hook a grace period to show up.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMBackgroundTaskServiceNoticeTurnTests
{
    private const string UserMessage = "Show me the employee.";
    private const string OrdinaryAnswer = "Anna works in Bern.";
    private const string SkillName = "get_employee";
    private static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SkippedHookGrace = TimeSpan.FromMilliseconds(300);

    private IConversationCompactionService _compaction = null!;
    private IAutoMemoryExtractionService _memory = null!;
    private ISkillLearningCaseCollector _learning = null!;
    private IAgentSkillRepository _skills = null!;
    private ITrajectoryCaptureService _trajectory = null!;
    private IAnswerGroundingEvaluator _grounding = null!;
    private TaskCompletionSource _compactionRan = null!;
    private TaskCompletionSource _auditRan = null!;
    private TaskCompletionSource _trajectoryRan = null!;
    private TaskCompletionSource _memoryRan = null!;
    private TaskCompletionSource _learningRan = null!;
    private TaskCompletionSource _groundingRan = null!;
    private LLMBackgroundTaskService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _compactionRan = NewSignal();
        _auditRan = NewSignal();
        _trajectoryRan = NewSignal();
        _memoryRan = NewSignal();
        _learningRan = NewSignal();
        _groundingRan = NewSignal();

        _compaction = Substitute.For<IConversationCompactionService>();
        _compaction.CompactIfNeededAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Signal(_compactionRan));
        _memory = Substitute.For<IAutoMemoryExtractionService>();
        _memory.ExtractAndStoreMemoriesAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(_ => Signal(_memoryRan));
        _learning = Substitute.For<ISkillLearningCaseCollector>();
        _learning.CollectFromTurnAsync(Arg.Any<SkillLearningTurn>(), Arg.Any<CancellationToken>())
            .Returns(_ => Signal(_learningRan));
        _skills = Substitute.For<IAgentSkillRepository>();
        _skills.GetByNameAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _auditRan.TrySetResult();
                return Task.FromResult<AgentSkill?>(null);
            });
        _trajectory = Substitute.For<ITrajectoryCaptureService>();
        _trajectory.CaptureAsync(Arg.Any<Guid>(), Arg.Any<LLMContext>(), Arg.Any<string>(), Arg.Any<List<LLMFunctionCall>>())
            .Returns(_ => Signal(_trajectoryRan));
        _grounding = Substitute.For<IAnswerGroundingEvaluator>();
        _grounding.EvaluateAsync(
                Arg.Any<Guid>(), Arg.Any<LLMContext>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<LLMFunctionCall>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Signal(_groundingRan));

        var services = new ServiceCollection();
        services.AddSingleton(_compaction);
        services.AddSingleton(_memory);
        services.AddSingleton(_learning);
        services.AddSingleton(_skills);
        services.AddSingleton(_trajectory);
        services.AddSingleton(_grounding);
        var provider = services.BuildServiceProvider();

        _service = new LLMBackgroundTaskService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<ILogger<LLMBackgroundTaskService>>());
    }

    [Test]
    public async Task NoticeTurn_SkipsMemoryLearningAndGrounding()
    {
        _service.RunBackgroundTasks(
            new Agent { Id = Guid.NewGuid() }, Conversation(), Context(),
            EmptyAnswerRecoveryConstants.FallbackNotice, SuccessfulCall(), answeredWithNotice: true);

        await WaitFor(_compactionRan, _auditRan, _trajectoryRan);
        await Task.Delay(SkippedHookGrace);

        await _memory.DidNotReceiveWithAnyArgs().ExtractAndStoreMemoriesAsync(default, default!, default!, default!);
        await _learning.DidNotReceiveWithAnyArgs().CollectFromTurnAsync(default!, default);
        await _grounding.DidNotReceiveWithAnyArgs().EvaluateAsync(default, default!, default!, default!, default);
    }

    [Test]
    public async Task NoticeTurn_StillRunsCompactionAuditAndTrajectory()
    {
        _service.RunBackgroundTasks(
            new Agent { Id = Guid.NewGuid() }, Conversation(), Context(),
            EmptyAnswerRecoveryConstants.NoActionNotice, new List<LLMFunctionCall> { new() { FunctionName = SkillName } },
            answeredWithNotice: true);

        await WaitFor(_compactionRan, _auditRan, _trajectoryRan);

        await _trajectory.Received(1).CaptureAsync(
            Arg.Any<Guid>(), Arg.Any<LLMContext>(), Arg.Any<string>(), Arg.Any<List<LLMFunctionCall>>());
    }

    [Test]
    public async Task OrdinaryAnswer_ReachesEveryHook()
    {
        _service.RunBackgroundTasks(
            new Agent { Id = Guid.NewGuid() }, Conversation(), Context(), OrdinaryAnswer, SuccessfulCall());

        await WaitFor(_compactionRan, _auditRan, _trajectoryRan, _memoryRan, _learningRan, _groundingRan);

        await _memory.Received(1).ExtractAndStoreMemoriesAsync(
            Arg.Any<Guid>(), UserMessage, OrdinaryAnswer, Arg.Any<string>());
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task Signal(TaskCompletionSource signal)
    {
        signal.TrySetResult();
        return Task.CompletedTask;
    }

    private static async Task WaitFor(params TaskCompletionSource[] signals) =>
        await Task.WhenAll(signals.Select(signal => signal.Task)).WaitAsync(HookTimeout);

    private static List<LLMFunctionCall> SuccessfulCall() =>
        new() { new() { FunctionName = SkillName, Success = true } };

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
