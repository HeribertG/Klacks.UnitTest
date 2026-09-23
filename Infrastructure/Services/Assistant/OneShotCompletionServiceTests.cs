// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for OneShotCompletionService — the pipeline-free single LLM call used by the inbound intent
/// analysis and the spam filter. Verifies the request shape (system prompt + one user message,
/// temperature 0, no tools, no history, the model's own token budget), default vs. explicit model
/// resolution, and that provider errors, missing models and exceptions come back as a failed result
/// while transient errors are retried and cancellation is propagated. The orchestrator is concrete and
/// non-virtual, so a real LLMProviderOrchestrator runs over substituted repository/factory.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class OneShotCompletionServiceTests
{
    private const string DefaultModelId = "default-model";
    private const string DefaultApiModelId = "provider-default-model";
    private const string ExplicitModelId = "explicit-model";
    private const string ExplicitApiModelId = "provider-explicit-model";
    private const int DefaultModelMaxTokens = 8192;
    private const string SystemPrompt = "You classify things. Reply with JSON only.";
    private const string UserMessage = "From: a@example.com\nBody: hello";
    private const string TransientError = "429 Too Many Requests";
    private const string PermanentError = "Invalid API key";

    private ILLMRepository _repository = null!;
    private ILLMProviderFactory _providerFactory = null!;
    private ILLMProvider _provider = null!;
    private OneShotCompletionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ILLMRepository>();
        _providerFactory = Substitute.For<ILLMProviderFactory>();
        _provider = Substitute.For<ILLMProvider>();

        var defaultModel = new LLMModel
        {
            ModelId = DefaultModelId,
            ApiModelId = DefaultApiModelId,
            IsEnabled = true,
            MaxTokens = DefaultModelMaxTokens
        };
        _repository.GetDefaultModelAsync().Returns(defaultModel);
        _repository.GetModelByIdAsync(DefaultModelId).Returns(defaultModel);
        _providerFactory.GetProviderForModelAsync(DefaultModelId).Returns(_provider);

        var orchestrator = new LLMProviderOrchestrator(
            Substitute.For<ILogger<LLMProviderOrchestrator>>(), _providerFactory, _repository);
        _service = new OneShotCompletionService(orchestrator, Substitute.For<ILogger<OneShotCompletionService>>());
    }

    private void ProviderReturns(params LLMProviderResponse[] responses) =>
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(responses[0], responses.Skip(1).ToArray());

    [Test]
    public async Task Success_ReturnsContent_AndSendsAToolFreeSingleMessageRequest()
    {
        LLMProviderRequest? captured = null;
        _provider.ProcessAsync(Arg.Do<LLMProviderRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = "{\"intent\":\"Other\"}" });

        var result = await _service.CompleteAsync(SystemPrompt, UserMessage);

        result.Success.ShouldBeTrue();
        result.Content.ShouldBe("{\"intent\":\"Other\"}");
        result.Error.ShouldBeNull();
        captured.ShouldNotBeNull();
        captured!.SystemPrompt.ShouldBe(SystemPrompt);
        captured.Message.ShouldBe(UserMessage);
        captured.ModelId.ShouldBe(DefaultApiModelId);
        captured.Temperature.ShouldBe(0.0);
        captured.MaxTokens.ShouldBe(DefaultModelMaxTokens);
        captured.AvailableFunctions.ShouldBeEmpty();
        captured.ConversationHistory.ShouldBeEmpty();
        captured.VolatileSystemPrompt.ShouldBeNull();
    }

    [Test]
    public async Task ExplicitModelId_ResolvesThatModel()
    {
        var explicitModel = new LLMModel { ModelId = ExplicitModelId, ApiModelId = ExplicitApiModelId, IsEnabled = true };
        _repository.GetModelByIdAsync(ExplicitModelId).Returns(explicitModel);
        _providerFactory.GetProviderForModelAsync(ExplicitModelId).Returns(_provider);
        LLMProviderRequest? captured = null;
        _provider.ProcessAsync(Arg.Do<LLMProviderRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = "HAM" });

        var result = await _service.CompleteAsync(SystemPrompt, UserMessage, ExplicitModelId);

        result.Success.ShouldBeTrue();
        captured.ShouldNotBeNull();
        captured!.ModelId.ShouldBe(ExplicitApiModelId);
    }

    [Test]
    public async Task PermanentProviderError_ReturnsFailedResultWithTheError_WithoutRetrying()
    {
        ProviderReturns(new LLMProviderResponse { Success = false, Error = PermanentError });

        var result = await _service.CompleteAsync(SystemPrompt, UserMessage);

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe(PermanentError);
        result.Content.ShouldBeEmpty();
        await _provider.Received(1).ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TransientProviderError_IsRetried_ThenSucceeds()
    {
        ProviderReturns(
            new LLMProviderResponse { Success = false, Error = TransientError },
            new LLMProviderResponse { Success = true, Content = "SPAM" });

        var result = await _service.CompleteAsync(SystemPrompt, UserMessage);

        result.Success.ShouldBeTrue();
        result.Content.ShouldBe("SPAM");
        await _provider.Received(2).ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TransientProviderError_GivesUpAfterMaxRetries_WithFailedResult()
    {
        ProviderReturns(new LLMProviderResponse { Success = false, Error = TransientError });

        var result = await _service.CompleteAsync(SystemPrompt, UserMessage);

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe(TransientError);
        await _provider.Received(LLMRetryConstants.MaxTransientRetries + 1)
            .ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoUsableModel_ReturnsFailedResult_WithoutCallingAProvider()
    {
        _repository.GetModelByIdAsync(DefaultModelId).Returns((LLMModel?)null);

        var result = await _service.CompleteAsync(SystemPrompt, UserMessage);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrWhiteSpace();
        await _provider.DidNotReceiveWithAnyArgs().ProcessAsync(default!, default);
    }

    [Test]
    public async Task ProviderThrows_ReturnsFailedResultWithTheExceptionMessage()
    {
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns<LLMProviderResponse>(_ => throw new HttpRequestException("connection reset"));

        var result = await _service.CompleteAsync(SystemPrompt, UserMessage);

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe("connection reset");
    }

    [Test]
    public async Task Cancellation_IsPropagated()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns<LLMProviderResponse>(_ => throw new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(
            () => _service.CompleteAsync(SystemPrompt, UserMessage, null, cts.Token));
    }
}
