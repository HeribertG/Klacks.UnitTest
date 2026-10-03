// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/**
 * LLM arm of the stage-3 benchmark: the production HolisticHarmonizerEngine + LlmPlanProposalProvider +
 * LLMProviderOrchestrator, fed DB-free. Only the two persistence seams are replaced: ILLMRepository returns one
 * in-memory model, ILLMProviderFactory returns an OpenAIProvider pointed at the configured OpenAI-compatible base
 * URL. OpenAIProvider is used on purpose: it inherits the multimodal BuildMessages of BaseOpenAICompatibleProvider,
 * whereas GenericOpenAICompatibleProvider (the production mapping for unknown provider ids such as 'qwen') has its
 * own BuildMessages that drops ImagePng, so no model behind it can pass the stage-3 image pre-flight. Configured from
 * environment variables (the API key is never read from anywhere else and never logged). A counting decorator records
 * tokens and calls for the cost estimate.
 * @param KLACKS_BENCH_LLM_API_KEY - provider API key (required, env only)
 * @param KLACKS_BENCH_LLM_BASE_URL - OpenAI-compatible base URL ending with '/'
 * @param KLACKS_BENCH_LLM_API_MODEL - provider model id, e.g. qwen3-vl-flash
 */

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.OpenAI;
using Klacks.Api.Infrastructure.Services.Schedules.HolisticHarmonizer;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Benchmark;

public sealed record Stage3LlmSettings(string ApiKey, string BaseUrl, string ApiModelId)
{
    public const string ApiKeyVariable = "KLACKS_BENCH_LLM_API_KEY";
    public const string BaseUrlVariable = "KLACKS_BENCH_LLM_BASE_URL";
    public const string ApiModelVariable = "KLACKS_BENCH_LLM_API_MODEL";

    public static Stage3LlmSettings? FromEnvironment()
    {
        var key = Environment.GetEnvironmentVariable(ApiKeyVariable);
        var baseUrl = Environment.GetEnvironmentVariable(BaseUrlVariable);
        var model = Environment.GetEnvironmentVariable(ApiModelVariable);
        return string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model)
            ? null
            : new Stage3LlmSettings(key, baseUrl, model);
    }
}

public sealed record Stage3LlmResult(
    HarmonyBitmap FinalBitmap,
    double FitnessBefore,
    double FitnessAfter,
    int AcceptedBatches,
    int RejectedBatches,
    int WouldDegradeBatches,
    int LlmCalls,
    long InputTokens,
    long OutputTokens,
    string? ParsingError);

public static class Stage3LlmArm
{
    private const string BenchModelId = "bench-stage3-model";
    private const string BenchProviderId = "bench-openai-compatible";
    private const string Language = "en";
    private const string EngineLogVariable = "KLACKS_BENCH_ENGINE_LOG";
    private static readonly Guid PlaceholderAgentId = Guid.Empty;

    public static async Task<Stage3LlmResult> RunAsync(BitmapInput input, Stage3LlmSettings settings, CancellationToken ct)
    {
        var counter = new CountingProvider(BuildProvider(settings));
        var repository = Substitute.For<ILLMRepository>();
        repository.GetModelByIdAsync(BenchModelId).Returns(new LLMModel
        {
            ModelId = BenchModelId,
            ModelName = settings.ApiModelId,
            ApiModelId = settings.ApiModelId,
            ProviderId = BenchProviderId,
            IsEnabled = true,
            MaxTokens = 8192,
        });
        var factory = Substitute.For<ILLMProviderFactory>();
        factory.GetProviderForModelAsync(BenchModelId).Returns(counter);

        var orchestrator = new LLMProviderOrchestrator(NullLogger<LLMProviderOrchestrator>.Instance, factory, repository);
        var proposals = new LlmPlanProposalProvider(orchestrator, NullLogger<LlmPlanProposalProvider>.Instance);
        var engine = new HolisticHarmonizerEngine(
            new FixedContextBuilder(input),
            proposals,
            new HolisticHarmonizerModelCapabilityCache(),
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EngineLogVariable))
                ? NullLogger<HolisticHarmonizerEngine>.Instance
                : new Stage3ProgressLogger<HolisticHarmonizerEngine>());

        var request = new HolisticHarmonizerEngineRequest(
            input.StartDate, input.EndDate, [PlaceholderAgentId], AnalyseToken: null, BenchModelId, Language);
        var result = await engine.RunAsync(request, ct);

        return new Stage3LlmResult(
            result.FinalBitmap,
            result.FitnessBefore,
            result.FitnessAfter,
            result.Iterations.Count(i => i.Result is BatchAcceptance.Accepted or BatchAcceptance.PartiallyAccepted),
            result.Iterations.Count(i => i.Result == BatchAcceptance.Rejected),
            result.Iterations.Count(i => i.Result == BatchAcceptance.WouldDegrade),
            counter.Calls,
            counter.InputTokens,
            counter.OutputTokens,
            result.LlmParsingError);
    }

    private static ILLMProvider BuildProvider(Stage3LlmSettings settings)
    {
        var provider = new OpenAIProvider(
            new HttpClient(),
            NullLogger<OpenAIProvider>.Instance,
            new ConfigurationBuilder().Build());
        provider.Configure(new LLMProvider
        {
            ProviderId = BenchProviderId,
            ProviderName = BenchProviderId,
            ApiKey = settings.ApiKey,
            BaseUrl = settings.BaseUrl,
            IsEnabled = true,
            RequiresApiKey = true,
        });
        return provider;
    }

    private sealed class FixedContextBuilder(BitmapInput input) : IHarmonizerContextBuilder
    {
        public Task<BitmapInput> BuildContextAsync(HarmonizerContextRequest request, CancellationToken ct) => Task.FromResult(input);
    }

    private sealed class CountingProvider(ILLMProvider inner) : ILLMProvider
    {
        private long _inputTokens;
        private long _outputTokens;
        private int _calls;

        public long InputTokens => Interlocked.Read(ref _inputTokens);
        public long OutputTokens => Interlocked.Read(ref _outputTokens);
        public int Calls => _calls;

        public string ProviderId => inner.ProviderId;
        public string ProviderName => inner.ProviderName;
        public bool IsEnabled => inner.IsEnabled;

        public void Configure(LLMProvider providerConfig) => inner.Configure(providerConfig);

        public async Task<LLMProviderResponse> ProcessAsync(LLMProviderRequest request, CancellationToken cancellationToken = default)
        {
            var response = await inner.ProcessAsync(request, cancellationToken);
            Interlocked.Increment(ref _calls);
            Interlocked.Add(ref _inputTokens, response.Usage.InputTokens);
            Interlocked.Add(ref _outputTokens, response.Usage.OutputTokens);
            return response;
        }

        public Task<bool> ValidateApiKeyAsync(string apiKey) => inner.ValidateApiKeyAsync(apiKey);
    }
}
