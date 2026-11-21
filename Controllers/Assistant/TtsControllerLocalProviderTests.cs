// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Verifies the synthesize endpoint for a provider that chunks internally (local): the whole sanitized text reaches
/// the provider in one call, the provider's media type (audio/wav) is returned, empty audio becomes 204 "no audio", and
/// a failing local provider never falls back to another registered provider, and a busy one answers 503 with its
/// Retry-After delay.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Presentation.Controllers.Assistant;
using Klacks.Api.Presentation.DTOs.Assistant;
using Klacks.Api.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Controllers.Assistant;

[TestFixture]
public class TtsControllerLocalProviderTests
{
    private ITtsProvider _local = null!;
    private ITtsProvider _edge = null!;
    private TtsController _controller = null!;

    [SetUp]
    public void Setup()
    {
        _local = Substitute.For<ITtsProvider>();
        _local.ProviderId.Returns(TtsProviderConstants.Local);
        _local.MediaType.Returns(TtsProviderConstants.WavMediaType);
        _local.ChunksInternally.Returns(true);

        _edge = Substitute.For<ITtsProvider>();
        _edge.ProviderId.Returns(TtsProviderConstants.Edge);
        _edge.MediaType.Returns(TtsProviderConstants.MpegMediaType);
        _edge.SynthesizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([9, 9]);

        _controller = new TtsController(
            Substitute.For<ILogger<TtsController>>(),
            new[] { _edge, _local },
            Substitute.For<IPlaygroundMode>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    [Test]
    public async Task Synthesize_Local_LongText_IsSentInOneCallAndReturnedAsWav()
    {
        var longText = string.Concat(Enumerable.Repeat("Dies ist ein langer Erklärungssatz für die Planung. ", 200));
        longText.Length.ShouldBeGreaterThan(TtsProviderConstants.SynthesisChunkLength * 3);
        _local.SynthesizeAsync(TtsTextSanitizer.Sanitize(longText), "supertonic:F2", "de", Arg.Any<CancellationToken>()).Returns([1, 2, 3]);

        var result = await _controller.Synthesize(
            new TtsSynthesizeRequest(longText, "de", TtsProviderConstants.Local, "supertonic:F2"), CancellationToken.None);

        var file = result.ShouldBeOfType<FileContentResult>();
        file.ContentType.ShouldBe(TtsProviderConstants.WavMediaType);
        file.FileContents.ShouldBe(new byte[] { 1, 2, 3 });
        _local.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ITtsProvider.SynthesizeAsync)).ShouldBe(1);
    }

    [Test]
    public async Task Synthesize_Local_EmptyAudio_ReturnsNoContentWithoutFallback()
    {
        _local.SynthesizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<byte>());

        var result = await _controller.Synthesize(
            new TtsSynthesizeRequest("Bonġu.", "mt", TtsProviderConstants.Local), CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        await _edge.DidNotReceiveWithAnyArgs().SynthesizeAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task Synthesize_Local_Failure_Returns500WithoutFallback()
    {
        _local.SynthesizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<byte[]>(_ => throw new InvalidOperationException(LocalTtsConstants.NotInstalledMessage));

        var result = await _controller.Synthesize(
            new TtsSynthesizeRequest("Hallo.", "de", TtsProviderConstants.Local), CancellationToken.None);

        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(500);
        await _edge.DidNotReceiveWithAnyArgs().SynthesizeAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task Synthesize_Edge_KeepsMpegMediaType()
    {
        var result = await _controller.Synthesize(new TtsSynthesizeRequest("Hallo.", "de"), CancellationToken.None);

        result.ShouldBeOfType<FileContentResult>().ContentType.ShouldBe(TtsProviderConstants.MpegMediaType);
    }

    [Test]
    public async Task Synthesize_Local_Busy_Returns503WithRetryAfter()
    {
        _local.SynthesizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<byte[]>(_ => throw new TtsProviderBusyException(TimeSpan.FromSeconds(5)));

        var result = await _controller.Synthesize(
            new TtsSynthesizeRequest("Hallo.", "de", TtsProviderConstants.Local), CancellationToken.None);

        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        _controller.Response.Headers.RetryAfter.ToString().ShouldBe("5");
        await _edge.DidNotReceiveWithAnyArgs().SynthesizeAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task Synthesize_Local_BusyWithoutDelay_Returns503WithoutRetryAfter()
    {
        _local.SynthesizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<byte[]>(_ => throw new TtsProviderBusyException(null));

        var result = await _controller.Synthesize(
            new TtsSynthesizeRequest("Hallo.", "de", TtsProviderConstants.Local), CancellationToken.None);

        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        _controller.Response.Headers.ContainsKey("Retry-After").ShouldBeFalse();
    }
}
