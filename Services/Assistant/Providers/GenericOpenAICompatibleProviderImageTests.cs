// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests that the generic OpenAI-compatible provider (Qwen, Together, Cerebras, Groq, OpenRouter, Ollama ...)
/// forwards an attached PNG as an OpenAI image_url content block instead of dropping it, keeps a text-only
/// message a plain string, and turns an upstream rejection of an image request into an explainable error.
/// No real endpoint is called: a capturing HttpMessageHandler stands in for the network.
/// </summary>

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Generic;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Assistant.Providers;

[TestFixture]
public class GenericOpenAICompatibleProviderImageTests
{
    private const string BaseUrl = "https://openai-compatible.test/v1/";
    private const string ApiKey = "test-key";
    private const string UserText = "Read the plan";
    private const string ModelId = "qwen-vl-max";
    private const string JsonMediaType = "application/json";
    private const string StreamMediaType = "text/event-stream";

    private const string CompletionBody =
        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}";

    private const string StreamBody =
        "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
        "data: [DONE]\n\n";

    private const string ImageRejectedBody =
        "{\"error\":{\"message\":\"This model does not support image input\",\"type\":\"invalid_request_error\"}}";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02];

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;
        private readonly string _mediaType;

        public CapturingHandler(HttpStatusCode statusCode, string body, string mediaType)
        {
            _statusCode = statusCode;
            _body = body;
            _mediaType = mediaType;
        }

        public string CapturedRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, _mediaType),
            };
        }
    }

    private static (GenericOpenAICompatibleProvider Provider, CapturingHandler Handler) CreateProvider(
        HttpStatusCode statusCode, string body, string mediaType)
    {
        var handler = new CapturingHandler(statusCode, body, mediaType);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };

        var provider = new GenericOpenAICompatibleProvider(
            httpClient,
            Substitute.For<ILogger<GenericOpenAICompatibleProvider>>(),
            Substitute.For<IConfiguration>());

        provider.Configure(new Klacks.Api.Domain.Models.Assistant.LLMProvider
        {
            ProviderId = "qwen",
            ProviderName = "Qwen (Alibaba)",
            ApiKey = ApiKey,
            IsEnabled = true,
            BaseUrl = BaseUrl,
        });

        return (provider, handler);
    }

    private static LLMProviderRequest CreateRequest(byte[]? imagePng) => new()
    {
        Message = UserText,
        SystemPrompt = "system",
        ModelId = ModelId,
        Temperature = 0.2,
        MaxTokens = 16,
        ImagePng = imagePng,
    };

    private static JsonElement LastMessageContent(string requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        var messages = document.RootElement.GetProperty("messages");
        var last = messages[messages.GetArrayLength() - 1];
        last.GetProperty("role").GetString().ShouldBe(OpenAIUserMessageFactory.UserRole);
        return last.GetProperty("content").Clone();
    }

    private static void AssertImagePayload(JsonElement content)
    {
        content.ValueKind.ShouldBe(JsonValueKind.Array);
        content.GetArrayLength().ShouldBe(2);

        var text = content[0];
        text.GetProperty("type").GetString().ShouldBe("text");
        text.GetProperty("text").GetString().ShouldBe(UserText);

        var image = content[1];
        image.GetProperty("type").GetString().ShouldBe("image_url");
        image.GetProperty("image_url").GetProperty("url").GetString()
            .ShouldBe(OpenAIUserMessageFactory.PngDataUriPrefix + Convert.ToBase64String(Png));
    }

    [Test]
    public async Task ProcessAsync_WithImage_SendsTextAndImageUrlBlocks()
    {
        var (provider, handler) = CreateProvider(HttpStatusCode.OK, CompletionBody, JsonMediaType);

        var response = await provider.ProcessAsync(CreateRequest(Png), CancellationToken.None);

        response.Success.ShouldBeTrue();
        AssertImagePayload(LastMessageContent(handler.CapturedRequestBody));
    }

    [Test]
    public async Task ProcessAsync_WithoutImage_KeepsContentAPlainString()
    {
        var (provider, handler) = CreateProvider(HttpStatusCode.OK, CompletionBody, JsonMediaType);

        await provider.ProcessAsync(CreateRequest(null), CancellationToken.None);

        var content = LastMessageContent(handler.CapturedRequestBody);
        content.ValueKind.ShouldBe(JsonValueKind.String);
        content.GetString().ShouldBe(UserText);
    }

    [Test]
    public async Task ProcessAsync_WithEmptyImage_KeepsContentAPlainString()
    {
        var (provider, handler) = CreateProvider(HttpStatusCode.OK, CompletionBody, JsonMediaType);

        await provider.ProcessAsync(CreateRequest([]), CancellationToken.None);

        LastMessageContent(handler.CapturedRequestBody).ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Test]
    public async Task ProcessStreamAsync_WithImage_SendsTextAndImageUrlBlocks()
    {
        var (provider, handler) = CreateProvider(HttpStatusCode.OK, StreamBody, StreamMediaType);

        await foreach (var _ in provider.ProcessStreamAsync(CreateRequest(Png), CancellationToken.None))
        {
        }

        AssertImagePayload(LastMessageContent(handler.CapturedRequestBody));
    }

    [Test]
    public async Task ProcessAsync_ImageRejectedByModelWithoutVision_FailsWithExplainableError()
    {
        var (provider, _) = CreateProvider(HttpStatusCode.BadRequest, ImageRejectedBody, JsonMediaType);

        var response = await provider.ProcessAsync(CreateRequest(Png), CancellationToken.None);

        response.Success.ShouldBeFalse();
        response.Error.ShouldNotBeNull();
        response.Error.ShouldContain(GenericOpenAICompatibleProvider.ImageRejectedHint);
        response.Error.ShouldContain(ModelId);
        response.Error.ShouldNotContain(ApiKey);
    }

    [Test]
    public async Task ProcessAsync_ClientErrorWithoutImage_HasNoVisionHint()
    {
        var (provider, _) = CreateProvider(HttpStatusCode.BadRequest, ImageRejectedBody, JsonMediaType);

        var response = await provider.ProcessAsync(CreateRequest(null), CancellationToken.None);

        response.Success.ShouldBeFalse();
        response.Error!.ShouldNotContain(GenericOpenAICompatibleProvider.ImageRejectedHint);
    }

    [TestCase(HttpStatusCode.ServiceUnavailable)]
    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.NotFound)]
    public async Task ProcessAsync_TransientAuthOrRoutingErrorWithImage_HasNoVisionHint(HttpStatusCode statusCode)
    {
        var (provider, _) = CreateProvider(statusCode, ImageRejectedBody, JsonMediaType);

        var response = await provider.ProcessAsync(CreateRequest(Png), CancellationToken.None);

        response.Success.ShouldBeFalse();
        response.Error!.ShouldNotContain(GenericOpenAICompatibleProvider.ImageRejectedHint);
    }
}
