// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for AssemblyAiSttProvider v3 Universal-Streaming message parsing.
/// </summary>
namespace Klacks.UnitTest.Services.Assistant.Providers;

using Shouldly;
using Klacks.Api.Application.Constants;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Stt;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

[TestFixture]
public class AssemblyAiSttProviderTests
{
    private AssemblyAiSttProvider _provider;

    [SetUp]
    public void SetUp()
    {
        _provider = new AssemblyAiSttProvider(NullLogger<AssemblyAiSttProvider>.Instance);
    }

    [Test]
    public void ProviderId_ShouldBeAssemblyAi()
    {
        _provider.ProviderId.ShouldBe(SttProviderConstants.AssemblyAi);
    }

    [Test]
    public void ParseResult_ShouldParseFinalTurn()
    {
        var json = """{"type":"Turn","transcript":"Hallo Welt","end_of_turn":true,"end_of_turn_confidence":0.92}""";

        var result = AssemblyAiSttProvider.ParseResult(json);

        result.ShouldNotBeNull();
        result!.Text.ShouldBe("Hallo Welt");
        result.IsFinal.ShouldBeTrue();
        result.Confidence.ShouldBe(0.92f, 0.01f);
    }

    [Test]
    public void ParseResult_ShouldParsePartialTurn()
    {
        var json = """{"type":"Turn","transcript":"Hal","end_of_turn":false}""";

        var result = AssemblyAiSttProvider.ParseResult(json);

        result.ShouldNotBeNull();
        result!.Text.ShouldBe("Hal");
        result.IsFinal.ShouldBeFalse();
    }

    [Test]
    public void ParseResult_ShouldReturnNull_ForEmptyTranscript()
    {
        var json = """{"type":"Turn","transcript":"","end_of_turn":false}""";

        var result = AssemblyAiSttProvider.ParseResult(json);

        result.ShouldBeNull();
    }

    [Test]
    public void ParseResult_ShouldReturnNull_ForBeginMessage()
    {
        var json = """{"type":"Begin","id":"session-123","expires_at":1700000000}""";

        var result = AssemblyAiSttProvider.ParseResult(json);

        result.ShouldBeNull();
    }

    [Test]
    public void ParseResult_ShouldReturnNull_ForTerminationMessage()
    {
        var json = """{"type":"Termination","audio_duration_seconds":12,"session_duration_seconds":15}""";

        var result = AssemblyAiSttProvider.ParseResult(json);

        result.ShouldBeNull();
    }
}
