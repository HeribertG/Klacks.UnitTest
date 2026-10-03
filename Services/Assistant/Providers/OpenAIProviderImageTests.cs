// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Regression tests for the image path of the OpenAI provider (BaseOpenAICompatibleProvider), whose user-message
/// construction now goes through the shared OpenAIUserMessageFactory: an attached PNG still yields a text block
/// plus an image_url data URI, a text-only request still sends a plain string. No real endpoint is called.
/// </summary>

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.OpenAI;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Assistant.Providers;

[TestFixture]
public class OpenAIProviderImageTests
{
    private const string BaseUrl = "https://openai.test/v1/";
    private const string UserText = "Read the plan";

    private const string CompletionBody =
        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string CapturedRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CompletionBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static async Task<JsonElement> SendAndReadLastContentAsync(byte[]? imagePng)
    {
        var handler = new CapturingHandler();
        var provider = new OpenAIProvider(
            new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) },
            Substitute.For<ILogger<OpenAIProvider>>(),
            Substitute.For<IConfiguration>());

        provider.Configure(new Klacks.Api.Domain.Models.Assistant.LLMProvider
        {
            ProviderId = "openai",
            ProviderName = "OpenAI",
            ApiKey = "test-key",
            IsEnabled = true,
            BaseUrl = BaseUrl,
        });

        var response = await provider.ProcessAsync(new LLMProviderRequest
        {
            Message = UserText,
            ModelId = "gpt-4o",
            MaxTokens = 16,
            ImagePng = imagePng,
        }, CancellationToken.None);

        response.Success.ShouldBeTrue();

        using var document = JsonDocument.Parse(handler.CapturedRequestBody);
        var messages = document.RootElement.GetProperty("messages");
        return messages[messages.GetArrayLength() - 1].GetProperty("content").Clone();
    }

    [Test]
    public async Task ProcessAsync_WithImage_SendsTextAndImageUrlBlocks()
    {
        var content = await SendAndReadLastContentAsync(Png);

        content.ValueKind.ShouldBe(JsonValueKind.Array);
        content[0].GetProperty("type").GetString().ShouldBe("text");
        content[0].GetProperty("text").GetString().ShouldBe(UserText);
        content[1].GetProperty("type").GetString().ShouldBe("image_url");
        content[1].GetProperty("image_url").GetProperty("url").GetString()
            .ShouldBe(OpenAIUserMessageFactory.PngDataUriPrefix + Convert.ToBase64String(Png));
    }

    [Test]
    public async Task ProcessAsync_WithoutImage_KeepsContentAPlainString()
    {
        var content = await SendAndReadLastContentAsync(null);

        content.ValueKind.ShouldBe(JsonValueKind.String);
        content.GetString().ShouldBe(UserText);
    }
}
