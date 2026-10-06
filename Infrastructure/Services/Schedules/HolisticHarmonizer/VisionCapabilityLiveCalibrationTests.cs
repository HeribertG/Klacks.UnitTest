// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Live calibration of the Wizard 3 vision capability check against real models. Never part of CI
/// ([Explicit], category Wizard3VisionCalibration): it costs API money and needs keys. For each model it sends
/// single reads through the production request + evaluator to measure the per-read pass / misread /
/// inconclusive rates, then runs the full probe several times to observe the cached verdict. CalibrateGrid sends
/// production-size schedules (VisionGridChallengeFactory) and measures how many asked cells each model reads correctly;
/// KLACKS_VISION_CAL_GRID_READS (default 20) sets its sample size.
/// Known vision models must never be reported as "not vision-capable"; known text-only models must never pass.
/// Keys come only from environment variables (KLACKS_VISION_CAL_KEY_ANTHROPIC, _GOOGLE, _MISTRAL, _OPENAI,
/// _GROQ, _DEEPSEEK); a model without key is skipped. KLACKS_VISION_CAL_READS (default 20) and
/// KLACKS_VISION_CAL_PROBES (default 5) set the sample sizes, KLACKS_VISION_CAL_OUT appends a markdown row
/// per model to that file.
/// </summary>

using System.Diagnostics;
using System.Globalization;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Anthropic;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.DeepSeek;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Gemini;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Generic;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Mistral;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.OpenAI;
using Klacks.Api.Infrastructure.Services.Schedules.HolisticHarmonizer;
using Klacks.UnitTest.Infrastructure.Services.Schedules.HolisticHarmonizer.VisionGrid;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Bitmap;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.HolisticHarmonizer;

[TestFixture]
[Explicit("Live LLM calls; costs money and needs API keys")]
[Category("Wizard3VisionCalibration")]
public class VisionCapabilityLiveCalibrationTests
{
    private const string KeyEnvPrefix = "KLACKS_VISION_CAL_KEY_";
    private const string ReadsEnv = "KLACKS_VISION_CAL_READS";
    private const string ProbesEnv = "KLACKS_VISION_CAL_PROBES";
    private const string GridReadsEnv = "KLACKS_VISION_CAL_GRID_READS";
    private const string OutEnv = "KLACKS_VISION_CAL_OUT";
    private const int DefaultReads = 20;
    private const int DefaultProbes = 5;
    private const double MinVisionPassRate = 0.9;
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(120);

    public static IEnumerable<TestCaseData> Models()
    {
        yield return Case("anthropic", "https://api.anthropic.com/v1/", "claude-haiku-4-5-20251001", vision: true);
        yield return Case("google", "https://generativelanguage.googleapis.com/v1/", "gemini-2.5-flash", vision: true);
        yield return Case("openai", "https://api.openai.com/v1/", "gpt-5.4-mini", vision: true);
        yield return Case("mistral", "https://api.mistral.ai/v1/", "mistral-small-2603", vision: true);
        yield return Case("mistral", "https://api.mistral.ai/v1/", "ministral-8b-2512", vision: true);
        yield return Case("groq", "https://api.groq.com/openai/v1/", "openai/gpt-oss-20b", vision: false);
        yield return Case("deepseek", "https://api.deepseek.com/v1/", "deepseek-flash", vision: true);
        yield return Case("deepseek", "https://api.deepseek.com/v1/", "deepseek-v4-pro", vision: false);
    }

    [TestCaseSource(nameof(Models))]
    public async Task Calibrate(string providerId, string baseUrl, string apiModelId, bool vision)
    {
        var apiKey = Environment.GetEnvironmentVariable(KeyEnvPrefix + providerId.ToUpperInvariant());
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Assert.Ignore($"No key in {KeyEnvPrefix}{providerId.ToUpperInvariant()}");
        }

        var provider = CreateProvider(providerId, baseUrl, apiKey!);
        var model = new LLMModel { ModelId = apiModelId, ApiModelId = apiModelId, ProviderId = providerId };
        var reads = ReadInt(ReadsEnv, DefaultReads);
        var probes = ReadInt(ProbesEnv, DefaultProbes);

        var counts = new Dictionary<VisionCapabilityOutcome, int>();
        var latencies = new List<long>();
        var samples = new List<string>();
        for (var i = 0; i < reads; i++)
        {
            var token = NextToken(i);
            var request = HolisticHarmonizerProbeRequests.Capability(model, VisionCapabilityPngRenderer.Render(token));
            var stopwatch = Stopwatch.StartNew();
            var response = await provider.ProcessAsync(request, CancellationToken.None);
            latencies.Add(stopwatch.ElapsedMilliseconds);
            var verdict = VisionCapabilityResponseEvaluator.Evaluate(response, token, VisionCapabilityProbe.TokenAlphabet);
            counts[verdict.Outcome] = counts.GetValueOrDefault(verdict.Outcome) + 1;
            if (verdict.Outcome != VisionCapabilityOutcome.Passed && samples.Count < 3)
            {
                samples.Add($"{verdict.Outcome}: {verdict.Error}");
            }
        }

        var probe = new VisionCapabilityProbe(NullLogger.Instance);
        var probeHealthy = 0;
        var probeNoVision = 0;
        var probeInconclusive = 0;
        for (var i = 0; i < probes; i++)
        {
            var result = await probe.RunAsync(model, provider, CancellationToken.None);
            if (result.IsHealthy)
            {
                probeHealthy++;
            }
            else if (result.AnsweredButFailedImageCheck)
            {
                probeNoVision++;
            }
            else
            {
                probeInconclusive++;
            }
        }

        var passed = counts.GetValueOrDefault(VisionCapabilityOutcome.Passed);
        var misread = counts.GetValueOrDefault(VisionCapabilityOutcome.Misread);
        var inconclusive = counts.GetValueOrDefault(VisionCapabilityOutcome.Inconclusive);
        latencies.Sort();
        var row = string.Format(
            CultureInfo.InvariantCulture,
            "| {0} | {1} | {2} | {3}/{4} | {5} | {6} | {7} ms | {8}/{9}/{10} | {11} |",
            providerId, apiModelId, vision ? "vision" : "text-only", passed, reads, misread, inconclusive,
            latencies[latencies.Count / 2], probeHealthy, probeNoVision, probeInconclusive,
            string.Join(" / ", samples).Replace("|", "/").Replace("\n", " "));
        TestContext.Out.WriteLine(row);
        AppendReport(row);

        if (vision)
        {
            if (passed + misread == 0)
            {
                Assert.Inconclusive($"No read was answered (account, quota or provider problem): {samples.FirstOrDefault()}");
            }

            probeNoVision.ShouldBe(0, "a vision model must never be cached as not vision-capable");
            ((double)passed / (passed + misread)).ShouldBeGreaterThanOrEqualTo(MinVisionPassRate);
        }
        else
        {
            passed.ShouldBe(0, "a text-only model must never read the token");
            probeHealthy.ShouldBe(0, "a text-only model must never pass the probe");
        }
    }

    [TestCaseSource(nameof(Models))]
    public async Task CalibrateGrid(string providerId, string baseUrl, string apiModelId, bool vision)
    {
        var apiKey = Environment.GetEnvironmentVariable(KeyEnvPrefix + providerId.ToUpperInvariant());
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Assert.Ignore($"No key in {KeyEnvPrefix}{providerId.ToUpperInvariant()}");
        }

        var provider = CreateProvider(providerId, baseUrl, apiKey!);
        var model = new LLMModel { ModelId = apiModelId, ApiModelId = apiModelId, ProviderId = providerId };
        var reads = ReadInt(GridReadsEnv, DefaultReads);

        var passed = 0;
        var misread = 0;
        var inconclusive = 0;
        var correctCells = 0;
        var answeredCells = 0;
        var latencies = new List<long>();
        var inputTokens = new List<int>();
        var samples = new List<string>();
        for (var i = 0; i < reads; i++)
        {
            var challenge = VisionGridChallengeFactory.Create(new Random(i));
            var stopwatch = Stopwatch.StartNew();
            var response = await provider.ProcessAsync(VisionGridRequests.Create(model, challenge), CancellationToken.None);
            latencies.Add(stopwatch.ElapsedMilliseconds);
            inputTokens.Add(response.Usage?.InputTokens ?? 0);
            var verdict = VisionGridResponseEvaluator.Evaluate(response, challenge);
            switch (verdict.Outcome)
            {
                case VisionCapabilityOutcome.Passed:
                    passed++;
                    break;
                case VisionCapabilityOutcome.Misread:
                    misread++;
                    break;
                default:
                    inconclusive++;
                    break;
            }

            if (verdict.Outcome != VisionCapabilityOutcome.Inconclusive)
            {
                correctCells += verdict.CorrectAnswers;
                answeredCells += challenge.Questions.Count;
            }

            if (verdict.Outcome != VisionCapabilityOutcome.Passed && samples.Count < 2)
            {
                samples.Add($"{verdict.Outcome}: {verdict.Error}");
            }
        }

        latencies.Sort();
        inputTokens.Sort();
        var row = string.Format(
            CultureInfo.InvariantCulture,
            "| grid | {0} | {1} | {2} | {3}/{4} | {5} | {6} | {7}/{8} cells | {9} ms | {10} in-tokens | {11} |",
            providerId, apiModelId, vision ? "vision" : "text-only", passed, reads, misread, inconclusive,
            correctCells, answeredCells, latencies[latencies.Count / 2], inputTokens[inputTokens.Count / 2],
            string.Join(" / ", samples).Replace("|", "/").Replace("\n", " "));
        TestContext.Out.WriteLine(row);
        AppendReport(row);

        if (!vision)
        {
            passed.ShouldBe(0, "a text-only model must never read all asked cells");
        }
    }

    private static TestCaseData Case(string providerId, string baseUrl, string apiModelId, bool vision) =>
        new TestCaseData(providerId, baseUrl, apiModelId, vision).SetName($"{{m}}_{providerId}_{apiModelId}");

    private static string NextToken(int index)
    {
        var letters = VisionCapabilityProbe.TokenAlphabet.ToCharArray();
        new Random(index).Shuffle(letters);
        return new string(letters, 0, 3);
    }

    private static ILLMProvider CreateProvider(string providerId, string baseUrl, string apiKey)
    {
        var configuration = new ConfigurationBuilder().Build();
        var httpClient = new HttpClient { Timeout = HttpTimeout };
        ILLMProvider provider = providerId switch
        {
            "anthropic" => new AnthropicProvider(httpClient, NullLogger<AnthropicProvider>.Instance, configuration),
            "google" => new GeminiProvider(httpClient, NullLogger<GeminiProvider>.Instance, configuration),
            "openai" => new OpenAIProvider(httpClient, NullLogger<OpenAIProvider>.Instance, configuration),
            "mistral" => new MistralProvider(httpClient, NullLogger<MistralProvider>.Instance, configuration),
            "deepseek" => new DeepSeekProvider(httpClient, NullLogger<DeepSeekProvider>.Instance, configuration),
            _ => new GenericOpenAICompatibleProvider(httpClient, NullLogger<GenericOpenAICompatibleProvider>.Instance, configuration),
        };
        provider.Configure(ProviderConfig(providerId, baseUrl, apiKey));
        return provider;
    }

    private static LLMProvider ProviderConfig(string providerId, string baseUrl, string apiKey) =>
        new()
        {
            ProviderId = providerId,
            ProviderName = providerId,
            ApiKey = apiKey,
            IsEnabled = true,
            BaseUrl = baseUrl,
            ApiVersion = providerId == "anthropic" ? "2023-06-01" : null,
        };

    private static int ReadInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;

    private static void AppendReport(string row)
    {
        var path = Environment.GetEnvironmentVariable(OutEnv);
        if (!string.IsNullOrWhiteSpace(path))
        {
            File.AppendAllText(path, row + Environment.NewLine);
        }
    }
}
