// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for DeepgramSttProvider WebSocket message parsing.
/// </summary>
namespace Klacks.UnitTest.Services.Assistant.Providers;

using FluentAssertions;
using Klacks.Api.Application.Constants;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Stt;
using NUnit.Framework;

[TestFixture]
public class DeepgramSttProviderTests
{
    private DeepgramSttProvider _provider;

    [SetUp]
    public void SetUp()
    {
        _provider = new DeepgramSttProvider();
    }

    [Test]
    public void ProviderId_ShouldBeDeepgram()
    {
        _provider.ProviderId.Should().Be(SttProviderConstants.Deepgram);
    }

    [Test]
    public void ParseResult_ShouldParseFinalResult()
    {
        var json = """{"type":"Results","channel":{"alternatives":[{"transcript":"Hallo Welt","confidence":0.95}]},"is_final":true}""";

        var result = DeepgramSttProvider.ParseResult(json);

        result.Should().NotBeNull();
        result!.Text.Should().Be("Hallo Welt");
        result.IsFinal.Should().BeTrue();
        result.Confidence.Should().BeApproximately(0.95f, 0.01f);
    }

    [Test]
    public void ParseResult_ShouldParseInterimResult()
    {
        var json = """{"type":"Results","channel":{"alternatives":[{"transcript":"Hal","confidence":0.8}]},"is_final":false}""";

        var result = DeepgramSttProvider.ParseResult(json);

        result.Should().NotBeNull();
        result!.Text.Should().Be("Hal");
        result.IsFinal.Should().BeFalse();
    }

    [Test]
    public void ParseResult_ShouldReturnNull_ForEmptyTranscript()
    {
        var json = """{"type":"Results","channel":{"alternatives":[{"transcript":"","confidence":0}]},"is_final":false}""";

        var result = DeepgramSttProvider.ParseResult(json);

        result.Should().BeNull();
    }

    [Test]
    public void ParseResult_ShouldReturnNull_ForNonResultMessage()
    {
        var json = """{"type":"Metadata","request_id":"abc"}""";

        var result = DeepgramSttProvider.ParseResult(json);

        result.Should().BeNull();
    }
}
