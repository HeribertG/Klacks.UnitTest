// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ProviderCandidateParser: tolerant JSON extraction, slug sanitization,
/// URL normalization and rejection of malformed entries.
/// </summary>

using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Services.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class ProviderCandidateParserTests
{
    [Test]
    public void Parse_CleanJsonArray_ReturnsCandidate()
    {
        const string content =
            "[{\"providerId\":\"groq\",\"providerName\":\"Groq\",\"baseUrl\":\"https://api.groq.com/openai/v1/\",\"requiresApiKey\":true}]";

        var result = ProviderCandidateParser.Parse(content);

        result.Count.ShouldBe(1);
        result[0].ProviderId.ShouldBe("groq");
        result[0].ProviderName.ShouldBe("Groq");
        result[0].BaseUrl.ShouldBe("https://api.groq.com/openai/v1/");
        result[0].RequiresApiKey.ShouldBeTrue();
        result[0].Source.ShouldBe(ProviderCandidateSource.Web);
    }

    [Test]
    public void Parse_JsonWrappedInProseAndCodeFence_ExtractsArray()
    {
        const string content =
            "Sure, here are the providers:\n```json\n" +
            "[{\"providerName\":\"Together\",\"baseUrl\":\"https://api.together.xyz/v1/\",\"requiresApiKey\":true}]" +
            "\n```\nHope that helps!";

        var result = ProviderCandidateParser.Parse(content);

        result.Count.ShouldBe(1);
        result[0].ProviderName.ShouldBe("Together");
    }

    [Test]
    public void Parse_BaseUrlWithoutTrailingSlash_GetsNormalized()
    {
        const string content =
            "[{\"providerName\":\"xAI\",\"baseUrl\":\"https://api.x.ai/v1\",\"requiresApiKey\":true}]";

        var result = ProviderCandidateParser.Parse(content);

        result[0].BaseUrl.ShouldBe("https://api.x.ai/v1/");
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("no json here")]
    [TestCase("not even close")]
    public void Parse_NoUsableJson_ReturnsEmpty(string content)
    {
        ProviderCandidateParser.Parse(content).ShouldBeEmpty();
    }

    [Test]
    public void Parse_MalformedJson_ReturnsEmpty()
    {
        ProviderCandidateParser.Parse("[{\"providerName\": ").ShouldBeEmpty();
    }

    [Test]
    public void Parse_EntryWithInvalidUrl_IsSkipped()
    {
        const string content =
            "[{\"providerName\":\"Bad\",\"baseUrl\":\"not-a-url\",\"requiresApiKey\":true}," +
            "{\"providerName\":\"Good\",\"baseUrl\":\"https://api.good.ai/v1/\",\"requiresApiKey\":false}]";

        var result = ProviderCandidateParser.Parse(content);

        result.Count.ShouldBe(1);
        result[0].ProviderName.ShouldBe("Good");
        result[0].RequiresApiKey.ShouldBeFalse();
    }

    [Test]
    public void Parse_EntryWithoutName_IsSkipped()
    {
        const string content =
            "[{\"providerName\":\"\",\"baseUrl\":\"https://api.x.ai/v1/\",\"requiresApiKey\":true}]";

        ProviderCandidateParser.Parse(content).ShouldBeEmpty();
    }

    [Test]
    public void Parse_TruncatesToMaxCandidates()
    {
        var entries = string.Join(",", Enumerable.Range(0, 20).Select(i =>
            $"{{\"providerName\":\"P{i}\",\"baseUrl\":\"https://p{i}.example.com/v1/\",\"requiresApiKey\":true}}"));

        var result = ProviderCandidateParser.Parse($"[{entries}]");

        result.Count.ShouldBe(12);
    }

    [TestCase("Novita AI", null, "novita-ai")]
    [TestCase("Fireworks!!", null, "fireworks")]
    [TestCase("  Spaced  Name  ", null, "spaced-name")]
    [TestCase("ignored", "My_Provider.v2", "my-provider-v2")]
    [TestCase("Provider", "", "provider")]
    [TestCase("Provider", null, "provider")]
    public void SlugFromName_SanitizesCorrectly(string name, string? id, string expected)
    {
        ProviderCandidateParser.SlugFromName(id, name).ShouldBe(expected);
    }
}
