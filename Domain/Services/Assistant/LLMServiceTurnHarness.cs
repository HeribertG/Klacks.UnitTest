// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// A whole-turn LLMService with a scripted provider, for tests that need to see both chat paths end to end:
/// the provider answers each call from a queue (tool calls or content), every provider request is recorded,
/// skills run through the real LLMFunctionExecutor against a substituted ILLMSkillBridge, and the persisted
/// assistant answer and the tracked usage totals are read back from the substituted repository. A streamed
/// response reports its usage through the request's stream-usage callback, as the real providers do, and a
/// response registered with StreamBreaksOffAfter streams its content and then fails. StartsRecipe hands the
/// turn a recipe plan, as the turn preparation does for a matched recipe.
/// </summary>

using System.Runtime.CompilerServices;
using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using RepositoryLLMMessage = Klacks.Api.Domain.Models.Assistant.LLMMessage;
using ProviderLLMUsage = Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage;
using RepositoryLLMUsage = Klacks.Api.Domain.Models.Assistant.LLMUsage;

namespace Klacks.UnitTest.Domain.Services.Assistant;

internal sealed class LLMServiceTurnHarness
{
    internal const string ConversationId = "conv-turn-harness";
    private const string ModelId = "model-under-test";
    private const string AssistantRole = "assistant";
    private const string StreamBreakOffError = "connection reset by peer";

    private readonly Queue<LLMProviderResponse> _script = new();
    private readonly HashSet<LLMProviderResponse> _breakingStreams = new(ReferenceEqualityComparer.Instance);
    private readonly ILLMRepository _repository;
    private readonly ITurnPreparationService _turnPreparation;

    internal LLMServiceTurnHarness(bool streaming)
    {
        _repository = Substitute.For<ILLMRepository>();
        _repository.GetModelByIdAsync(ModelId).Returns(new LLMModel
        {
            ModelId = ModelId,
            ApiModelId = ModelId,
            IsEnabled = true,
            MaxTokens = 4096
        });
        _repository.GetOrCreateConversationAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new LLMConversation { Id = Guid.NewGuid(), ConversationId = ConversationId });
        _repository.GetConversationMessagesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>())
            .Returns(new List<RepositoryLLMMessage>());
        _repository.SaveMessageAsync(Arg.Any<RepositoryLLMMessage>())
            .Returns(call =>
            {
                var message = call.Arg<RepositoryLLMMessage>();
                OnMessageSaved?.Invoke();
                if (message.Role == AssistantRole)
                {
                    PersistedAnswer = message.Content;
                }

                return message;
            });
        _repository.TrackUsageAsync(Arg.Any<RepositoryLLMUsage>())
            .Returns(call =>
            {
                TrackedUsage = call.Arg<RepositoryLLMUsage>();
                return TrackedUsage;
            });

        Provider = Substitute.For<ILLMProvider>();
        Provider.SupportsStreaming.Returns(streaming);
        Provider.GetEffectiveInputTokenLimit(Arg.Any<LLMModel>()).Returns(64_000);
        Provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Next(call.Arg<LLMProviderRequest>()));
        Provider.ProcessStreamAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<LLMProviderRequest>();
                ProviderStreamTokens.Add(call.Arg<CancellationToken>());
                return AsTokens(request, Next(request));
            });

        var providerFactory = Substitute.For<ILLMProviderFactory>();
        providerFactory.GetProviderForModelAsync(ModelId).Returns(Provider);

        var agentRepository = Substitute.For<IAgentRepository>();
        AgentRepository = agentRepository;
        agentRepository.GetDefaultAgentAsync().Returns((Agent?)null);
        agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns((Agent?)null);

        SkillBridge = Substitute.For<ILLMSkillBridge>();
        SkillsSucceed();

        _turnPreparation = Substitute.For<ITurnPreparationService>();
        StartsRecipe(null);

        var contextBudgetPolicy = Substitute.For<IContextBudgetPolicy>();
        contextBudgetPolicy.Resolve(Arg.Any<ILLMProvider>(), Arg.Any<LLMModel>())
            .Returns(new ContextBudgetProfile(20, 30, 5, 5, 8_000));

        var companyClock = Substitute.For<ICompanyClock>();
        companyClock.GetNowAsync(Arg.Any<CancellationToken>()).Returns(DateTimeOffset.UtcNow);
        companyClock.GetTimeZoneResolutionAsync(Arg.Any<CancellationToken>())
            .Returns(new CompanyTimeZoneResolution(TimeZoneInfo.Utc, CompanyTimeZoneSource.Utc));

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        BackgroundTasks = Substitute.For<ILLMBackgroundTaskService>();

        var conversationManager = new LLMConversationManager(
            Substitute.For<ILogger<LLMConversationManager>>(), _repository);

        Recorder = new TurnCompletionRecorder(
            Substitute.For<ILogger<TurnCompletionRecorder>>(),
            conversationManager, _turnPreparation, agentRepository, BackgroundTasks, TurnState, StoppedTurnCleanup);

        Service = new LLMService(
            logger: Logger,
            providerOrchestrator: new LLMProviderOrchestrator(
                Substitute.For<ILogger<LLMProviderOrchestrator>>(), providerFactory, _repository),
            conversationManager: conversationManager,
            functionExecutor: new LLMFunctionExecutor(
                Substitute.For<ILogger<LLMFunctionExecutor>>(),
                Substitute.For<IAgentSkillRepository>(),
                agentRepository,
                Substitute.For<IPendingConfirmationStore>(),
                Substitute.For<ITurnConfirmationScope>(),
                Substitute.For<ICancellableSkillPolicy>(),
                SkillBridge),
            responseBuilder: new LLMResponseBuilder(),
            promptBuilder: new LLMSystemPromptBuilder(
                new PromptTranslationProvider(
                    scopeFactory, Substitute.For<ILogger<PromptTranslationProvider>>()),
                companyClock),
            agentRepository: agentRepository,
            contextAssemblyPipeline: null!,
            backgroundTaskService: BackgroundTasks,
            recipeEngine: new RecipeEngineService(
                scopeFactory,
                PendingRecipes,
                Substitute.For<ILogger<RecipeEngineService>>()),
            recipeRunRecorder: RecipeRuns,
            suggestionEntityNameReader: Substitute.For<ISuggestionEntityNameReader>(),
            contextBudgetPolicy: contextBudgetPolicy,
            turnPreparation: _turnPreparation,
            turnCompletionRecorder: Recorder,
            turnState: TurnState);
    }

    internal LLMService Service { get; }

    internal TurnCompletionRecorder Recorder { get; }

    internal IRecipeRunRecorder RecipeRuns { get; } = Substitute.For<IRecipeRunRecorder>();

    internal IStoppedTurnCleanup StoppedTurnCleanup { get; } = Substitute.For<IStoppedTurnCleanup>();

    internal IPendingRecipeStore PendingRecipes { get; } = Substitute.For<IPendingRecipeStore>();

    internal List<CancellationToken> ProviderStreamTokens { get; } = new();

    internal RecordingLogger<LLMService> Logger { get; } = new();

    internal IAgentRepository AgentRepository { get; private set; } = null!;

    internal ITurnPreparationService TurnPreparation => _turnPreparation;

    internal Action? OnMessageSaved { get; set; }

    internal TurnRunState TurnState { get; } = new();

    internal ILLMProvider Provider { get; }

    internal ILLMSkillBridge SkillBridge { get; }

    internal ILLMBackgroundTaskService BackgroundTasks { get; }

    internal List<LLMProviderRequest> Requests { get; } = new();

    internal string? PersistedAnswer { get; private set; }

    internal RepositoryLLMUsage? TrackedUsage { get; private set; }

    internal static LLMProviderResponse ToolCall(string skill, Dictionary<string, object>? parameters = null) => new()
    {
        Success = true,
        Content = string.Empty,
        FunctionCalls = new List<LLMFunctionCall>
        {
            new() { FunctionName = skill, Parameters = parameters ?? new Dictionary<string, object>() }
        }
    };

    internal static LLMProviderResponse ToolCallWithProse(string prose, string skill)
    {
        var response = ToolCall(skill);
        response.Content = prose;
        return response;
    }

    internal static LLMProviderResponse WithInputTokens(LLMProviderResponse response, int inputTokens)
    {
        response.Usage = new ProviderLLMUsage { InputTokens = inputTokens };
        return response;
    }

    internal static LLMProviderResponse Text(string content) => new()
    {
        Success = true,
        Content = content,
        FunctionCalls = new List<LLMFunctionCall>()
    };

    internal static LLMContext Context(string message, string? language = "en") => new()
    {
        Message = message,
        UserId = Guid.NewGuid().ToString(),
        ConversationId = ConversationId,
        ModelId = ModelId,
        Language = language,
        TurnId = Guid.NewGuid(),
        AvailableFunctions = new List<LLMFunction>
        {
            new() { Name = "get_employee" },
            new() { Name = "list_groups" },
            new() { Name = SkillNames.ManagePendingNotes }
        }
    };

    internal void Script(params LLMProviderResponse[] responses)
    {
        foreach (var response in responses)
        {
            _script.Enqueue(response);
        }
    }

    internal void StartsRecipe(RecipeExecutionPlan? plan) =>
        _turnPreparation.PrepareAsync(Arg.Any<TurnPreparationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TurnPreparation(plan, false, null, null));

    internal LLMProviderResponse StreamBreaksOffAfter(string partialContent)
    {
        var response = Text(partialContent);
        _breakingStreams.Add(response);
        return response;
    }

    internal void ConversationStoreFails() =>
        _repository.GetOrCreateConversationAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns<LLMConversation>(_ => throw new InvalidOperationException("conversation store down"));

    internal void SkillsSucceed() =>
        SkillBridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new SkillBridgeResult { Success = true, ResultType = "Data", Message = "Skill result." });

    internal void SkillsFail() =>
        SkillBridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new SkillBridgeResult { Success = false, ResultType = "Error", Message = "Skill failed." });

    internal async Task<List<SseChunk>> StreamAsync(LLMContext context)
    {
        var chunks = new List<SseChunk>();
        await foreach (var chunk in Service.ProcessStreamAsync(context))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    internal static string StreamedContent(IEnumerable<SseChunk> chunks) =>
        string.Concat(chunks.Where(c => c.Type == SseChunkType.Content).Select(c => c.Text));

    private LLMProviderResponse Next(LLMProviderRequest request)
    {
        Requests.Add(request);
        return _script.Count > 0 ? _script.Dequeue() : Text(string.Empty);
    }

    private async IAsyncEnumerable<string> AsTokens(
        LLMProviderRequest request,
        LLMProviderResponse response,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        foreach (var token in SplitIntoTokens(response.Content))
        {
            yield return token;
        }

        if (_breakingStreams.Contains(response))
        {
            throw new InvalidOperationException(StreamBreakOffError);
        }

        for (var index = 0; index < response.FunctionCalls.Count; index++)
        {
            var call = response.FunctionCalls[index];
            yield return LLMStreamingTokens.ToolCallPrefix + JsonSerializer.Serialize(new
            {
                index,
                name = call.FunctionName,
                arguments = JsonSerializer.Serialize(call.Parameters)
            });
        }

        if (response.FunctionCalls.Count > 0)
        {
            yield return LLMStreamingTokens.ToolCallEnd;
        }

        request.OnStreamUsage?.Invoke(response.Usage);
    }

    private static IEnumerable<string> SplitIntoTokens(string content)
    {
        const int tokenLength = 4;
        for (var start = 0; start < content.Length; start += tokenLength)
        {
            yield return content.Substring(start, Math.Min(tokenLength, content.Length - start));
        }
    }
}
