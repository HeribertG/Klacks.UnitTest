// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Verifies that the synthesize endpoint chunks long texts at sentence boundaries,
/// synthesizes every chunk via the resolved provider and concatenates the audio,
/// so long assistant answers are spoken completely instead of being rejected.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Presentation.Controllers.Assistant;
using Klacks.Api.Presentation.DTOs.Assistant;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Controllers.Assistant;

[TestFixture]
public class TtsControllerSynthesizeTests
{
    private ITtsProvider _provider = null!;
    private TtsController _controller = null!;

    [SetUp]
    public void Setup()
    {
        _provider = Substitute.For<ITtsProvider>();
        _provider.ProviderId.Returns(TtsProviderConstants.Edge);
        _provider.SynthesizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([1, 2, 3]);

        _controller = new TtsController(
            Substitute.For<ILogger<TtsController>>(),
            new[] { _provider });
    }

    [Test]
    public async Task Synthesize_LongText_SynthesizesAllChunksAndConcatenatesAudio()
    {
        var longText = string.Concat(Enumerable.Repeat("Dies ist ein langer Erklärungssatz für die Planung. ", 200));
        longText.Length.ShouldBeGreaterThan(TtsProviderConstants.SynthesisChunkLength * 3);

        var result = await _controller.Synthesize(
            new TtsSynthesizeRequest(longText, null), CancellationToken.None);

        var file = result.ShouldBeOfType<FileContentResult>();
        file.ContentType.ShouldBe("audio/mpeg");

        var expectedChunkCount = (int)Math.Ceiling(
            longText.Length / (double)TtsProviderConstants.SynthesisChunkLength);
        await _provider.Received().SynthesizeAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        var callCount = _provider.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ITtsProvider.SynthesizeAsync));
        callCount.ShouldBeGreaterThanOrEqualTo(expectedChunkCount);
        file.FileContents.Length.ShouldBe(callCount * 3);
    }

    [Test]
    public async Task Synthesize_ShortText_SynthesizesExactlyOnce()
    {
        var result = await _controller.Synthesize(
            new TtsSynthesizeRequest("Kurzer Text.", null), CancellationToken.None);

        result.ShouldBeOfType<FileContentResult>();
        await _provider.Received(1).SynthesizeAsync(
            "Kurzer Text.", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Synthesize_TextAboveAbsoluteLimit_ReturnsBadRequest()
    {
        var tooLong = new string('a', TtsProviderConstants.MaxSynthesisTextLength + 1);

        var result = await _controller.Synthesize(
            new TtsSynthesizeRequest(tooLong, null), CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }
}
