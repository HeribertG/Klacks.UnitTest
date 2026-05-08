// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Text.Json;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Gemini;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Shared;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Services.Assistant.Providers;

[TestFixture]
public class MultimodalDtoTests
{
    [Test]
    public void OpenAIMessage_WithStringContent_SerializesAsString()
    {
        var message = new OpenAIMessage { Role = "user", Content = "hello world" };
        var json = JsonSerializer.Serialize(message);
        json.ShouldContain("\"content\":\"hello world\"");
    }

    [Test]
    public void OpenAIMessage_WithImageBlock_SerializesAsContentArray()
    {
        var message = new OpenAIMessage
        {
            Role = "user",
            Content = new object[]
            {
                new OpenAITextContent("describe this image"),
                new OpenAIImageContent(new OpenAIImageUrl("data:image/png;base64,AAA")),
            },
        };
        var json = JsonSerializer.Serialize(message);
        json.ShouldContain("\"type\":\"text\"");
        json.ShouldContain("\"text\":\"describe this image\"");
        json.ShouldContain("\"type\":\"image_url\"");
        json.ShouldContain("\"image_url\":{\"url\":\"data:image/png;base64,AAA\"}");
    }

    [Test]
    public void OpenAIMessage_GetContentString_HandlesStringAssignment()
    {
        var message = new OpenAIMessage { Content = "plain text" };
        message.GetContentString().ShouldBe("plain text");
    }

    [Test]
    public void OpenAIMessage_GetContentString_HandlesDeserializedJsonElement()
    {
        var json = "{\"role\":\"assistant\",\"content\":\"hello there\"}";
        var deserialized = JsonSerializer.Deserialize<OpenAIMessage>(json)!;
        deserialized.GetContentString().ShouldBe("hello there");
    }

    [Test]
    public void GeminiPart_WithInlineData_SerializesMimeTypeAndData()
    {
        var part = new GeminiPart
        {
            Text = "describe this",
            InlineData = new GeminiInlineData("image/png", "AAA"),
        };
        var json = JsonSerializer.Serialize(part);
        json.ShouldContain("\"text\":\"describe this\"");
        json.ShouldContain("\"inline_data\":{\"mime_type\":\"image/png\",\"data\":\"AAA\"}");
    }

    [Test]
    public void GeminiPart_TextOnly_OmitsInlineData()
    {
        var part = new GeminiPart { Text = "plain" };
        var json = JsonSerializer.Serialize(part);
        json.ShouldContain("\"text\":\"plain\"");
        json.ShouldNotContain("inline_data");
    }
}
