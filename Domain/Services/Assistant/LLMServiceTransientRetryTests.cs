// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the transient-error retry seam in LLMService: ProcessWithTransientRetryAsync
/// retries transient provider failures (rate limit, overload) with backoff and gives up on
/// permanent errors or once retries are exhausted. The multi-turn loop is covered end-to-end
/// to prove a transient first attempt no longer aborts the turn with an error response.
/// </summary>

using System.Diagnostics;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProviderLLMMessage = Klacks.Api.Domain.Services.Assistant.Providers.LLMMessage;
using ProviderLLMUsage = Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceTransientRetryTests
{
    private const string TransientError = "429 Too Many Requests";
    private const string PermanentError = "Invalid API key";

    private ILLMProvider _provider = null!;
    private LLMService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _provider = Substitute.For<ILLMProvider>();

        var pendingRecipeStore = Substitute.For<IPendingRecipeStore>();
        var retrieval = Substitute.For<IKnowledgeRetrievalService>();
        retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new RetrievalResult([]));
        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe>());

        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
        serviceProvider.GetService(typeof(IKnowledgeRetrievalService)).Returns(retrieval);
        scope.ServiceProvider.Returns(serviceProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var recipeEngine = new RecipeEngineService(
            scopeFactory, pendingRecipeStore, Substitute.For<ILogger<RecipeEngineService>>());

        // ProcessWithTransientRetryAsync only touches the logger, and the multi-turn success path
        // exercised here breaks out of the loop before reaching the function executor or the
        // conversation manager (no function calls, no provider error) — null! keeps the test
        // focused on the retry seam. IPendingConfirmationStore is a real substitute because
        // ResolvePendingConfirmation runs for every message.
        _service = new LLMService(
            logger: Substitute.For<ILogger<LLMService>>(),
            providerOrchestrator: null!,
            conversationManager: null!,
            functionExecutor: null!,
            responseBuilder: null!,
            promptBuilder: null!,
            agentRepository: null!,
            contextAssemblyPipeline: null!,
            backgroundTaskService: null!,
            pendingConfirmationStore: Substitute.For<IPendingConfirmationStore>(),
            recipeEngine: recipeEngine,
            slotExtractor: new RecipeSlotExtractor(Substitute.For<ILogger<RecipeSlotExtractor>>()),
            suggestionEntityNameReader: null!);
    }

    private static LLMProviderRequest Request() => new()
    {
        Message = "Erzähl mir etwas über das Wetter heute bitte",
        SystemPrompt = "system",
        ModelId = "model-x"
    };

    [Test]
    public async Task Retry_SucceedsOnSecondAttempt_AfterTransientError()
    {
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>()).Returns(
            new LLMProviderResponse { Success = false, Error = TransientError },
            new LLMProviderResponse { Success = true, Content = "Hello" });

        var response = await _service.ProcessWithTransientRetryAsync(_provider, Request());

        response.Success.ShouldBeTrue();
        response.Content.ShouldBe("Hello");
        await _provider.Received(2).ProcessAsync(Arg.Any<LLMProviderRequest>());
    }

    [Test]
    public async Task Retry_DoesNotRetry_PermanentErrors()
    {
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>())
            .Returns(new LLMProviderResponse { Success = false, Error = PermanentError });

        var response = await _service.ProcessWithTransientRetryAsync(_provider, Request());

        response.Success.ShouldBeFalse();
        response.Error.ShouldBe(PermanentError);
        await _provider.Received(1).ProcessAsync(Arg.Any<LLMProviderRequest>());
    }

    [Test]
    public async Task Retry_GivesUp_AfterMaxTransientRetries()
    {
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>())
            .Returns(new LLMProviderResponse { Success = false, Error = TransientError });

        var response = await _service.ProcessWithTransientRetryAsync(_provider, Request());

        response.Success.ShouldBeFalse();
        await _provider.Received(LLMRetryConstants.MaxTransientRetries + 1)
            .ProcessAsync(Arg.Any<LLMProviderRequest>());
    }

    [Test]
    public async Task MultiTurnLoop_RecoversFromTransientFirstAttempt()
    {
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>()).Returns(
            new LLMProviderResponse { Success = false, Error = TransientError, Usage = new ProviderLLMUsage() },
            new LLMProviderResponse { Success = true, Content = "Hello", Usage = new ProviderLLMUsage() });

        var ctx = new MultiTurnContext(
            new LLMContext
            {
                Message = "Erzähl mir etwas über das Wetter heute bitte",
                UserId = Guid.NewGuid().ToString()
            },
            new LLMModel { ApiModelId = "model-x", MaxTokens = 1000 },
            _provider,
            "system",
            new List<ProviderLLMMessage>(),
            new ProviderLLMUsage(),
            new LLMConversation { ConversationId = "conv-retry" },
            Stopwatch.StartNew());

        var (responseContent, lastResponse, _, allFunctionCalls, _) =
            await _service.ExecuteMultiTurnLoopAsync(ctx);

        lastResponse.ShouldNotBeNull();
        lastResponse!.Success.ShouldBeTrue();
        responseContent.ShouldBe("Hello");
        allFunctionCalls.ShouldBeEmpty();
    }
}
