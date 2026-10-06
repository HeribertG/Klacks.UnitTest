// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Schedules.HolisticHarmonizer;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.HolisticHarmonizer;

[TestFixture]
public class VisionCapabilityProbeTests
{
    private static readonly LLMModel Model = new() { ModelId = "vision-model", ApiModelId = "vision-model-api" };

    private ILLMProvider _provider = null!;
    private Queue<string> _tokens = null!;

    [SetUp]
    public void SetUp()
    {
        _provider = Substitute.For<ILLMProvider>();
        _tokens = new Queue<string>(["KXN", "EFH", "PTZ"]);
    }

    [Test]
    public async Task RunAsync_FirstReadCorrect_PassesWithOneCall()
    {
        Answers("{\"token\":\"KXN\"}");

        var result = await CreateProbe().RunAsync(Model, _provider, CancellationToken.None);

        result.IsHealthy.ShouldBeTrue();
        result.AnsweredButFailedImageCheck.ShouldBeFalse();
        await _provider.Received(1).ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_OneMisreadThenCorrectRead_Passes()
    {
        Answers("{\"token\":\"KXH\"}", "{\"token\":\"EFH\"}");

        var result = await CreateProbe().RunAsync(Model, _provider, CancellationToken.None);

        result.IsHealthy.ShouldBeTrue();
        result.AnsweredButFailedImageCheck.ShouldBeFalse();
    }

    [Test]
    public async Task RunAsync_AllReadsFail_ReportsNotVisionCapable()
    {
        Answers("{\"token\":\"\"}", "{\"token\":\"\"}");

        var result = await CreateProbe().RunAsync(Model, _provider, CancellationToken.None);

        result.IsHealthy.ShouldBeFalse();
        result.AnsweredButFailedImageCheck.ShouldBeTrue();
        await _provider.Received(VisionCapabilityProbe.ReadAttempts)
            .ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_EachReadUsesAFreshToken()
    {
        var renderedTokens = new List<string>();
        Answers("{\"token\":\"\"}", "{\"token\":\"\"}");

        await CreateProbe(renderedTokens).RunAsync(Model, _provider, CancellationToken.None);

        renderedTokens.ShouldBe(["KXN", "EFH"]);
    }

    [Test]
    public async Task RunAsync_MisreadThenProviderError_IsInconclusiveAndNotCachedAsNoVision()
    {
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                Ok("{\"token\":\"KXH\"}"),
                new LLMProviderResponse { Success = false, Error = "invalid api key" });

        var result = await CreateProbe().RunAsync(Model, _provider, CancellationToken.None);

        result.IsHealthy.ShouldBeFalse();
        result.AnsweredButFailedImageCheck.ShouldBeFalse();
    }

    [Test]
    public async Task RunAsync_TransientErrorThenCorrectRead_RetriesSameTokenAndPasses()
    {
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new LLMProviderResponse { Success = false, Error = "529 Overloaded" },
                Ok("{\"token\":\"KXN\"}"));

        var result = await CreateProbe().RunAsync(Model, _provider, CancellationToken.None);

        result.IsHealthy.ShouldBeTrue();
    }

    [Test]
    public async Task RunAsync_NonTransientProviderError_IsNotRetried()
    {
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = false, Error = "invalid api key" });

        var result = await CreateProbe().RunAsync(Model, _provider, CancellationToken.None);

        result.IsHealthy.ShouldBeFalse();
        result.AnsweredButFailedImageCheck.ShouldBeFalse();
        await _provider.Received(1).ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_ReadTimesOut_IsInconclusive()
    {
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());

        var result = await CreateProbe().RunAsync(Model, _provider, CancellationToken.None);

        result.IsHealthy.ShouldBeFalse();
        result.AnsweredButFailedImageCheck.ShouldBeFalse();
        result.Error!.ShouldContain("timed out");
    }

    [Test]
    public async Task RunAsync_MisreadThenSecondReadHangsPastDeadline_IsInconclusive()
    {
        var calls = 0;
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    return Ok("{\"token\":\"KXH\"}");
                }

                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return Ok("{\"token\":\"EFH\"}");
            });
        var probe = new VisionCapabilityProbe(
            NullLogger.Instance, () => _tokens.Dequeue(), _ => [1], TimeSpan.FromMilliseconds(300), TimeSpan.Zero);

        var result = await probe.RunAsync(Model, _provider, CancellationToken.None);

        result.IsHealthy.ShouldBeFalse();
        result.AnsweredButFailedImageCheck.ShouldBeFalse();
        result.Error!.ShouldContain("timed out");
    }

    [Test]
    public async Task RunAsync_ProviderSwallowsDeadlineCancellation_ReportsTimeout()
    {
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                }
                catch (OperationCanceledException)
                {
                }

                return new LLMProviderResponse { Success = false, Error = "Internal error processing request: A task was canceled." };
            });
        var probe = new VisionCapabilityProbe(
            NullLogger.Instance, () => _tokens.Dequeue(), _ => [1], TimeSpan.FromMilliseconds(200), TimeSpan.Zero);

        var result = await probe.RunAsync(Model, _provider, CancellationToken.None);

        result.AnsweredButFailedImageCheck.ShouldBeFalse();
        result.Error!.ShouldContain("timed out");
    }

    [Test]
    public async Task RunAsync_CallerCancels_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => CreateProbe().RunAsync(Model, _provider, cts.Token));
    }

    [Test]
    public async Task RunAsync_SendsRenderedImageToApiModel()
    {
        Answers("{\"token\":\"KXN\"}");

        await CreateProbe().RunAsync(Model, _provider, CancellationToken.None);

        await _provider.Received(1).ProcessAsync(
            Arg.Is<LLMProviderRequest>(r => r.ImagePng != null && r.ImagePng.Length > 0 && r.ModelId == "vision-model-api"),
            Arg.Any<CancellationToken>());
    }

    private VisionCapabilityProbe CreateProbe(List<string>? renderedTokens = null) =>
        new(
            NullLogger.Instance,
            () => _tokens.Dequeue(),
            token =>
            {
                renderedTokens?.Add(token);
                return [1, 2, 3];
            },
            TimeSpan.FromSeconds(5),
            TimeSpan.Zero);

    private void Answers(params string[] contents)
    {
        var responses = contents.Select(Ok).ToArray();
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(responses[0], responses[1..]);
    }

    private static LLMProviderResponse Ok(string content) => new() { Success = true, Content = content };
}
