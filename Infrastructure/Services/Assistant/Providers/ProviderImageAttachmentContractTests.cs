// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Wire contract behind the Wizard 3 vision check: every provider that can reach a vision model must put
/// LLMProviderRequest.ImagePng into the request body. A provider that drops the image makes every model behind
/// it fail the capability check, and the failure looks exactly like "the model cannot see" (MistralProvider and
/// DeepSeekProvider did this until 2026-10-06). The guard test fails when a new provider appears without being added here.
/// </summary>

using System.Net;
using System.Text;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Anthropic;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Azure;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Base;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.DeepSeek;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Gemini;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Generic;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Mistral;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant.Providers;

[TestFixture]
public class ProviderImageAttachmentContractTests
{
    private const string OpenAiCompletion =
        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}";

    private const string AnthropicCompletion =
        "{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}";

    private const string GeminiCompletion =
        "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"ok\"}]}}]," +
        "\"usageMetadata\":{\"promptTokenCount\":1,\"candidatesTokenCount\":1}}";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x42, 0x17, 0x99];
    private static readonly string PngBase64 = Convert.ToBase64String(Png);

    /// <summary>
    /// Providers that deliberately never send an image. Each entry needs a reason. Keep this empty unless the API
    /// rejects image content for every model: DeepSeek was listed here until 2026-10-06 although deepseek-flash
    /// reads images, which made it fail the vision check for our own reason.
    /// </summary>
    private static readonly Dictionary<Type, string> TextOnlyProviders = new();

    private static readonly Type[] ContractCoveredProviders =
    [
        typeof(OpenAIProvider),
        typeof(AzureOpenAIProvider),
        typeof(GenericOpenAICompatibleProvider),
        typeof(MistralProvider),
        typeof(DeepSeekProvider),
        typeof(AnthropicProvider),
        typeof(GeminiProvider),
    ];

    [Test]
    public async Task OpenAI_SendsImageAsDataUri()
    {
        var handler = new CapturingHandler(OpenAiCompletion);
        var provider = new OpenAIProvider(new HttpClient(handler), NullLogger<OpenAIProvider>.Instance, EmptyConfiguration());
        provider.Configure(Config("openai", "OpenAI", "https://api.openai.test/v1/"));

        await provider.ProcessAsync(Request("gpt-5.4-mini"));

        AssertOpenAiImage(handler.Body);
    }

    [Test]
    public async Task Azure_SendsImageAsDataUri()
    {
        var handler = new CapturingHandler(OpenAiCompletion);
        var provider = new AzureOpenAIProvider(
            new HttpClient(handler), NullLogger<AzureOpenAIProvider>.Instance, EmptyConfiguration(), Substitute.For<IHttpClientFactory>());
        var config = Config("azure", "Azure OpenAI", "https://klacks.openai.azure.test/");
        config.Settings = new Dictionary<string, object> { ["deploymentName"] = "vision" };
        provider.Configure(config);

        await provider.ProcessAsync(Request("gpt-5.4-mini"));

        AssertOpenAiImage(handler.Body);
    }

    [Test]
    public async Task Generic_SendsImageAsDataUri()
    {
        var handler = new CapturingHandler(OpenAiCompletion);
        var provider = new GenericOpenAICompatibleProvider(
            new HttpClient(handler), NullLogger<GenericOpenAICompatibleProvider>.Instance, EmptyConfiguration());
        provider.Configure(Config("groq", "Groq", "https://api.groq.test/openai/v1/"));

        await provider.ProcessAsync(Request("qwen/qwen3.8-27b"));

        AssertOpenAiImage(handler.Body);
    }

    [Test]
    public async Task Mistral_SendsImageAsDataUri()
    {
        var handler = new CapturingHandler(OpenAiCompletion);
        var provider = new MistralProvider(new HttpClient(handler), NullLogger<MistralProvider>.Instance, EmptyConfiguration());
        provider.Configure(Config("mistral", "Mistral AI", "https://api.mistral.test/v1/"));

        await provider.ProcessAsync(Request("mistral-small-2603"));

        AssertOpenAiImage(handler.Body);
    }

    [Test]
    public async Task DeepSeek_SendsImageAsDataUri()
    {
        var handler = new CapturingHandler(OpenAiCompletion);
        var provider = new DeepSeekProvider(new HttpClient(handler), NullLogger<DeepSeekProvider>.Instance, EmptyConfiguration());
        provider.Configure(Config("deepseek", "DeepSeek", "https://api.deepseek.test/v1/"));

        await provider.ProcessAsync(Request("deepseek-flash"));

        AssertOpenAiImage(handler.Body);
    }

    [Test]
    public async Task Anthropic_SendsImageAsBase64Source()
    {
        var handler = new CapturingHandler(AnthropicCompletion);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.test/v1/") };
        var provider = new AnthropicProvider(httpClient, NullLogger<AnthropicProvider>.Instance, EmptyConfiguration());
        var config = Config("anthropic", "Anthropic", "https://api.anthropic.test/v1/");
        config.ApiVersion = "2023-06-01";
        provider.Configure(config);

        await provider.ProcessAsync(Request("claude-haiku-4-5-20251001"));

        handler.Body.ShouldContain("\"type\":\"image\"");
        handler.Body.ShouldContain("\"media_type\":\"image/png\"");
        handler.Body.ShouldContain(PngBase64);
    }

    [Test]
    public async Task Gemini_SendsImageAsInlineData()
    {
        var handler = new CapturingHandler(GeminiCompletion);
        var provider = new GeminiProvider(new HttpClient(handler), NullLogger<GeminiProvider>.Instance, EmptyConfiguration());
        provider.Configure(Config("google", "Google Gemini", "https://generativelanguage.googleapis.test/v1beta/"));

        await provider.ProcessAsync(Request("gemini-2.5-flash"));

        handler.Body.ShouldContain("image/png");
        handler.Body.ShouldContain(PngBase64);
    }

    [Test]
    public async Task OpenAI_WithoutImage_KeepsPlainStringContent()
    {
        var handler = new CapturingHandler(OpenAiCompletion);
        var provider = new OpenAIProvider(new HttpClient(handler), NullLogger<OpenAIProvider>.Instance, EmptyConfiguration());
        provider.Configure(Config("openai", "OpenAI", "https://api.openai.test/v1/"));
        var request = Request("gpt-5.4-mini");
        request.ImagePng = null;

        await provider.ProcessAsync(request);

        handler.Body.ShouldNotContain("image_url");
    }

    [Test]
    public void EveryLlmProvider_IsCoveredByTheImageContractOrDeclaredTextOnly()
    {
        var concreteProviders = typeof(BaseHttpProvider).Assembly.GetTypes()
            .Where(t => typeof(ILLMProvider).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .ToList();

        var uncovered = concreteProviders
            .Where(t => !ContractCoveredProviders.Contains(t) && !TextOnlyProviders.ContainsKey(t))
            .Select(t => t.FullName)
            .ToList();

        uncovered.ShouldBeEmpty(
            "Every ILLMProvider must either get an image-attachment contract test here or be listed as text-only " +
            "with a reason. Otherwise Wizard 3 silently rejects every vision model behind it.");
    }

    private static void AssertOpenAiImage(string body)
    {
        body.ShouldContain("\"type\":\"image_url\"");
        body.ShouldContain("data:image/png;base64," + PngBase64);
    }

    private static LLMProviderRequest Request(string modelId) => new()
    {
        Message = "Read the token",
        SystemPrompt = "system",
        ModelId = modelId,
        MaxTokens = 16,
        ImagePng = Png,
    };

    private static IConfiguration EmptyConfiguration() => new ConfigurationBuilder().Build();

    private static LLMProvider Config(string id, string name, string baseUrl) => new()
    {
        ProviderId = id,
        ProviderName = name,
        ApiKey = "test-key",
        IsEnabled = true,
        BaseUrl = baseUrl,
    };

    private sealed class CapturingHandler(string responseBody) : HttpMessageHandler
    {
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
