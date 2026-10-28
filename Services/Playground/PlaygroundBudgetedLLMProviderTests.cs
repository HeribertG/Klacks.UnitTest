// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for PlaygroundBudgetedLLMProvider: every ILLMProvider member (default interface members included) is
/// implemented and forwarded by the decorator itself, calls are reserved, refused and settled against the
/// budget, an aborted or failed call keeps its estimate, and the streaming usage callback is chained once per call
/// and restored afterwards so a transient retry never reconciles twice.
/// </summary>

using System.Net;
using System.Reflection;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Domain.Models.Playground;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Playground;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Base;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Playground;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using LLMMessage = Klacks.Api.Domain.Services.Assistant.Providers.LLMMessage;
using LLMUsage = Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundBudgetedLLMProviderTests
{
    private ILLMProvider _inner = null!;
    private IPlaygroundTokenBudget _budget = null!;
    private PlaygroundBudgetedLLMProvider _sut = null!;
    private PlaygroundBudgetReservation _reservation = null!;

    [SetUp]
    public void SetUp()
    {
        _inner = Substitute.For<ILLMProvider>();
        _budget = Substitute.For<IPlaygroundTokenBudget>();
        _reservation = new PlaygroundBudgetReservation("2026-10-28", null, null, 0);
        _budget.TryReserve(Arg.Any<PlaygroundVisitor?>(), Arg.Any<long>()).Returns(_reservation);
        _sut = new PlaygroundBudgetedLLMProvider(_inner, _budget);
    }

    [TearDown]
    public void TearDown()
    {
        PlaygroundVisitorContext.Clear();
    }

    private static LLMProviderRequest Request() => new()
    {
        Message = new string('m', 400),
        SystemPrompt = new string('s', 800),
        MaxTokens = 500,
    };

    private static async Task<List<string>> Drain(IAsyncEnumerable<string> stream)
    {
        var items = new List<string>();
        await foreach (var item in stream)
        {
            items.Add(item);
        }

        return items;
    }

    private static async IAsyncEnumerable<string> Tokens(params string[] tokens)
    {
        foreach (var token in tokens)
        {
            yield return token;
        }

        await Task.CompletedTask;
    }

    [Test]
    public void Decorator_ImplementsEveryInterfaceMemberItself_NoMemberFallsBackToAnInterfaceDefault()
    {
        var map = typeof(PlaygroundBudgetedLLMProvider).GetInterfaceMap(typeof(ILLMProvider));

        map.InterfaceMethods.Length.ShouldBe(map.TargetMethods.Length);
        for (var i = 0; i < map.InterfaceMethods.Length; i++)
        {
            var target = map.TargetMethods[i];
            target.DeclaringType.ShouldBe(
                typeof(PlaygroundBudgetedLLMProvider),
                $"{map.InterfaceMethods[i].Name} is not implemented by the decorator and would use the interface default");
        }
    }

    [Test]
    public void Decorator_CoversAllInterfaceMembers_IncludingTheDefaultOnes()
    {
        var interfaceMethods = typeof(ILLMProvider).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet();

        var expected = new[]
        {
            "get_ProviderId", "get_ProviderName", "get_IsEnabled", "get_SupportsStreaming", "get_SupportsToolChoice",
            "ResolveForcedToolChoiceSupport", "Configure", "ProcessAsync", "ValidateApiKeyAsync",
            "GetEffectiveInputTokenLimit", "ProcessStreamAsync", "GetAvailableModelsAsync", "TestModelAsync",
        };

        interfaceMethods.ShouldBe(expected, ignoreOrder: true,
            "ILLMProvider changed: forward the new member in PlaygroundBudgetedLLMProvider and list it here");
    }

    [Test]
    public void Properties_AreForwardedFromTheInnerProvider()
    {
        _inner.ProviderId.Returns("deepseek");
        _inner.ProviderName.Returns("DeepSeek");
        _inner.IsEnabled.Returns(true);
        _inner.SupportsStreaming.Returns(true);
        _inner.SupportsToolChoice.Returns(true);

        _sut.ProviderId.ShouldBe("deepseek");
        _sut.ProviderName.ShouldBe("DeepSeek");
        _sut.IsEnabled.ShouldBeTrue();
        _sut.SupportsStreaming.ShouldBeTrue();
        _sut.SupportsToolChoice.ShouldBeTrue();
    }

    [Test]
    public void DefaultInterfaceMembers_AreForwardedInsteadOfAnsweredWithTheInterfaceDefault()
    {
        var request = Request();
        var model = new LLMModel { ContextWindow = 1234 };
        _inner.ResolveForcedToolChoiceSupport(request).Returns(Klacks.Api.Domain.Enums.ForcedToolChoiceSupport.Native);
        _inner.GetEffectiveInputTokenLimit(model).Returns(777);

        _sut.ResolveForcedToolChoiceSupport(request).ShouldBe(Klacks.Api.Domain.Enums.ForcedToolChoiceSupport.Native);
        _sut.GetEffectiveInputTokenLimit(model).ShouldBe(777);
    }

    [Test]
    public async Task AsyncPassThroughMembers_AreForwarded()
    {
        var models = new List<LLMModelDiscovery>();
        var testResult = new LLMModelTestResult("m", "m", true, "ok", 1);
        _inner.ValidateApiKeyAsync("key").Returns(true);
        _inner.GetAvailableModelsAsync().Returns(models);
        _inner.TestModelAsync("m", "p").Returns(testResult);

        (await _sut.ValidateApiKeyAsync("key")).ShouldBeTrue();
        (await _sut.GetAvailableModelsAsync()).ShouldBeSameAs(models);
        (await _sut.TestModelAsync("m", "p")).ShouldBeSameAs(testResult);
    }

    [Test]
    public void Configure_IsForwarded()
    {
        var config = new LLMProvider();

        _sut.Configure(config);

        _inner.Received(1).Configure(config);
    }

    [Test]
    public async Task ProcessAsync_ReservesTheEstimateBeforeTheCall()
    {
        var request = Request();
        _inner.ProcessAsync(request, Arg.Any<CancellationToken>()).Returns(new LLMProviderResponse());

        await _sut.ProcessAsync(request);

        var expected = PlaygroundTokenEstimator.EstimateReservation(request);
        expected.ShouldBeGreaterThan(request.MaxTokens);
        _budget.Received(1).TryReserve(Arg.Any<PlaygroundVisitor?>(), expected);
    }

    [Test]
    public async Task ProcessAsync_WithReportedUsage_SettlesTheWeightedUsage()
    {
        var request = Request();
        var usage = new LLMUsage { InputTokens = 100, OutputTokens = 20, CacheCreationInputTokens = 5, CacheReadInputTokens = 40 };
        _inner.ProcessAsync(request, Arg.Any<CancellationToken>()).Returns(new LLMProviderResponse { Usage = usage });

        await _sut.ProcessAsync(request);

        _budget.Received(1).Settle(_reservation, 165);
    }

    [Test]
    public async Task ProcessAsync_WithoutUsage_KeepsTheEstimate()
    {
        var request = Request();
        _inner.ProcessAsync(request, Arg.Any<CancellationToken>()).Returns(new LLMProviderResponse());

        await _sut.ProcessAsync(request);

        _budget.DidNotReceive().Settle(Arg.Any<PlaygroundBudgetReservation>(), Arg.Any<long>());
    }

    [Test]
    public async Task ProcessAsync_WhenTheProviderThrows_KeepsTheEstimate()
    {
        var request = Request();
        _inner.ProcessAsync(request, Arg.Any<CancellationToken>())
            .Returns<Task<LLMProviderResponse>>(_ => throw new HttpRequestException("boom"));

        await Should.ThrowAsync<HttpRequestException>(async () => await _sut.ProcessAsync(request));

        _budget.DidNotReceive().Settle(Arg.Any<PlaygroundBudgetReservation>(), Arg.Any<long>());
    }

    [Test]
    public async Task ProcessAsync_WhenTheBudgetRefuses_FailsWithoutCallingTheProviderAndMarksTheVisitor()
    {
        _budget.TryReserve(Arg.Any<PlaygroundVisitor?>(), Arg.Any<long>()).Returns((PlaygroundBudgetReservation?)null);
        var visitor = new PlaygroundVisitor("203.0.113.1", "device");
        PlaygroundVisitorContext.Set(visitor);

        var response = await _sut.ProcessAsync(Request());

        response.Success.ShouldBeFalse();
        response.Error.ShouldBe(PlaygroundTokenLimitConstants.LimitReachedMessage);
        visitor.TokenLimitHit.ShouldBeTrue();
        await _inner.DidNotReceive().ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessAsync_ChargesTheAmbientVisitor()
    {
        var visitor = new PlaygroundVisitor("203.0.113.1", "device");
        PlaygroundVisitorContext.Set(visitor);
        var request = Request();
        _inner.ProcessAsync(request, Arg.Any<CancellationToken>()).Returns(new LLMProviderResponse());

        await _sut.ProcessAsync(request);

        _budget.Received(1).TryReserve(visitor, Arg.Any<long>());
    }

    [Test]
    public async Task ProcessAsync_WithoutAmbientVisitor_ChargesWithNullVisitor()
    {
        var request = Request();
        _inner.ProcessAsync(request, Arg.Any<CancellationToken>()).Returns(new LLMProviderResponse());

        await _sut.ProcessAsync(request);

        _budget.Received(1).TryReserve(null, Arg.Any<long>());
    }

    [Test]
    public async Task ProcessStreamAsync_WhenTheBudgetRefuses_ThrowsTheLimitExceptionAndMarksTheVisitor()
    {
        _budget.TryReserve(Arg.Any<PlaygroundVisitor?>(), Arg.Any<long>()).Returns((PlaygroundBudgetReservation?)null);
        var visitor = new PlaygroundVisitor("203.0.113.1", "device");
        PlaygroundVisitorContext.Set(visitor);

        await Should.ThrowAsync<PlaygroundTokenLimitExceededException>(async () => await Drain(_sut.ProcessStreamAsync(Request())));

        visitor.TokenLimitHit.ShouldBeTrue();
        _inner.DidNotReceive().ProcessStreamAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessStreamAsync_PassesTheTokensThrough()
    {
        var request = Request();
        _inner.ProcessStreamAsync(request, Arg.Any<CancellationToken>()).Returns(Tokens("a", "b", "c"));

        var tokens = await Drain(_sut.ProcessStreamAsync(request));

        tokens.ShouldBe(new[] { "a", "b", "c" });
    }

    [Test]
    public async Task ProcessStreamAsync_ReportedUsage_IsSettledAndStillReachesTheOriginalCallback()
    {
        var request = Request();
        LLMUsage? seenByOriginal = null;
        request.OnStreamUsage = usage => seenByOriginal = usage;
        var reported = new LLMUsage { InputTokens = 90, OutputTokens = 10 };
        _inner.ProcessStreamAsync(request, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            request.OnStreamUsage!.Invoke(reported);
            return Tokens("x");
        });

        await Drain(_sut.ProcessStreamAsync(request));

        _budget.Received(1).Settle(_reservation, 100);
        seenByOriginal.ShouldBeSameAs(reported);
    }

    [Test]
    public async Task ProcessStreamAsync_RestoresTheOriginalCallback_SoARetryDoesNotChainTheWrapperTwice()
    {
        var request = Request();
        Action<LLMUsage> original = _ => { };
        request.OnStreamUsage = original;
        _inner.ProcessStreamAsync(request, Arg.Any<CancellationToken>()).Returns(_ => Tokens("x"));

        await Drain(_sut.ProcessStreamAsync(request));
        request.OnStreamUsage.ShouldBeSameAs(original);

        await Drain(_sut.ProcessStreamAsync(request));
        request.OnStreamUsage.ShouldBeSameAs(original);
    }

    [Test]
    public async Task ProcessStreamAsync_RetryOfTheSameRequest_SettlesEachCallOnceOnItsOwnReservation()
    {
        var request = Request();
        var second = new PlaygroundBudgetReservation("2026-10-28", null, null, 0);
        _budget.TryReserve(Arg.Any<PlaygroundVisitor?>(), Arg.Any<long>()).Returns(_reservation, second);
        _inner.ProcessStreamAsync(request, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            request.OnStreamUsage?.Invoke(new LLMUsage { InputTokens = 10 });
            return Tokens("x");
        });

        await Drain(_sut.ProcessStreamAsync(request));
        await Drain(_sut.ProcessStreamAsync(request));

        _budget.Received(1).Settle(_reservation, 10);
        _budget.Received(1).Settle(second, 10);
    }

    [Test]
    public async Task ProcessStreamAsync_WithoutReportedUsage_KeepsTheEstimate()
    {
        var request = Request();
        _inner.ProcessStreamAsync(request, Arg.Any<CancellationToken>()).Returns(Tokens("x"));

        await Drain(_sut.ProcessStreamAsync(request));

        _budget.DidNotReceive().Settle(Arg.Any<PlaygroundBudgetReservation>(), Arg.Any<long>());
    }

    [Test]
    public async Task ProcessStreamAsync_AbortedByTheConsumer_KeepsTheEstimateAndRestoresTheCallback()
    {
        var request = Request();
        Action<LLMUsage> original = _ => { };
        request.OnStreamUsage = original;
        _inner.ProcessStreamAsync(request, Arg.Any<CancellationToken>()).Returns(Tokens("a", "b", "c"));

        await foreach (var _ in _sut.ProcessStreamAsync(request))
        {
            break;
        }

        _budget.DidNotReceive().Settle(Arg.Any<PlaygroundBudgetReservation>(), Arg.Any<long>());
        request.OnStreamUsage.ShouldBeSameAs(original);
    }

    [Test]
    public async Task Estimate_GrowsWithPromptHistoryAndRequestedOutput()
    {
        var small = Request();
        var large = Request();
        large.ConversationHistory.Add(new LLMMessage { Content = new string('h', 4000) });
        large.MaxTokens = 2000;
        _inner.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>()).Returns(new LLMProviderResponse());

        await _sut.ProcessAsync(small);
        await _sut.ProcessAsync(large);

        PlaygroundTokenEstimator.EstimateReservation(large).ShouldBeGreaterThan(PlaygroundTokenEstimator.EstimateReservation(small));
    }

    private static async IAsyncEnumerable<string> Failing(Exception failure, params string[] before)
    {
        foreach (var token in before)
        {
            yield return token;
        }

        await Task.CompletedTask;
        throw failure;
    }

    private static LLMProviderHttpException InsufficientBalance() =>
        new("DeepSeek API error: Insufficient Balance", HttpStatusCode.PaymentRequired, false, false);

    [Test]
    public async Task ProcessAsync_ErrorWithoutUsage_ReleasesTheReservation()
    {
        var request = Request();
        _inner.ProcessAsync(request, Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = false, Error = "Insufficient Balance" });

        await _sut.ProcessAsync(request);

        _budget.Received(1).Release(_reservation);
    }

    [Test]
    public async Task ProcessAsync_ErrorAfterCancellation_KeepsTheReservation()
    {
        var request = Request();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _inner.ProcessAsync(request, Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = false, Error = "canceled" });

        await _sut.ProcessAsync(request, cts.Token);

        _budget.DidNotReceive().Release(Arg.Any<PlaygroundBudgetReservation>());
    }

    [Test]
    public async Task ProcessAsync_ErrorWithUsage_SettlesInsteadOfReleasing()
    {
        var request = Request();
        _inner.ProcessAsync(request, Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = false, Usage = new LLMUsage { InputTokens = 50, OutputTokens = 5 } });

        await _sut.ProcessAsync(request);

        _budget.Received(1).Settle(_reservation, 55);
        _budget.DidNotReceive().Release(Arg.Any<PlaygroundBudgetReservation>());
    }

    [Test]
    public async Task ProcessAsync_Success_NeverReleases()
    {
        var request = Request();
        _inner.ProcessAsync(request, Arg.Any<CancellationToken>()).Returns(new LLMProviderResponse { Success = true });

        await _sut.ProcessAsync(request);

        _budget.DidNotReceive().Release(Arg.Any<PlaygroundBudgetReservation>());
    }

    [Test]
    public async Task ProcessStreamAsync_ProviderRefusalBeforeAnyToken_ReleasesTheReservationAndRethrows()
    {
        var request = Request();
        _inner.ProcessStreamAsync(request, Arg.Any<CancellationToken>()).Returns(Failing(InsufficientBalance()));

        await Should.ThrowAsync<LLMProviderHttpException>(async () => await Drain(_sut.ProcessStreamAsync(request)));

        _budget.Received(1).Release(_reservation);
    }

    [Test]
    public async Task ProcessStreamAsync_NetworkErrorBeforeAnyToken_ReleasesTheReservation()
    {
        var request = Request();
        _inner.ProcessStreamAsync(request, Arg.Any<CancellationToken>()).Returns(Failing(new HttpRequestException("connection refused")));

        await Should.ThrowAsync<HttpRequestException>(async () => await Drain(_sut.ProcessStreamAsync(request)));

        _budget.Received(1).Release(_reservation);
    }

    [Test]
    public async Task ProcessStreamAsync_ErrorAfterTheFirstToken_KeepsTheReservation()
    {
        var request = Request();
        _inner.ProcessStreamAsync(request, Arg.Any<CancellationToken>()).Returns(Failing(InsufficientBalance(), "partial"));

        await Should.ThrowAsync<LLMProviderHttpException>(async () => await Drain(_sut.ProcessStreamAsync(request)));

        _budget.DidNotReceive().Release(Arg.Any<PlaygroundBudgetReservation>());
    }

    [Test]
    public async Task ProcessStreamAsync_CancelledBeforeAnyToken_KeepsTheReservation()
    {
        var request = Request();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _inner.ProcessStreamAsync(request, Arg.Any<CancellationToken>()).Returns(Failing(new OperationCanceledException(cts.Token)));

        await Should.ThrowAsync<OperationCanceledException>(async () => await Drain(_sut.ProcessStreamAsync(request, cts.Token)));

        _budget.DidNotReceive().Release(Arg.Any<PlaygroundBudgetReservation>());
    }

    [Test]
    public async Task ProcessStreamAsync_RefusalWhileTheConsumerCancelled_KeepsTheReservation()
    {
        var request = Request();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _inner.ProcessStreamAsync(request, Arg.Any<CancellationToken>()).Returns(Failing(InsufficientBalance()));

        await Should.ThrowAsync<LLMProviderHttpException>(async () => await Drain(_sut.ProcessStreamAsync(request, cts.Token)));

        _budget.DidNotReceive().Release(Arg.Any<PlaygroundBudgetReservation>());
    }

    [Test]
    public async Task ProcessStreamAsync_ErrorAfterUsageWasReported_KeepsTheSettledReservation()
    {
        var request = Request();
        _inner.ProcessStreamAsync(request, Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            var seen = callInfo.Arg<LLMProviderRequest>();
            seen.OnStreamUsage!(new LLMUsage { InputTokens = 10, OutputTokens = 2 });
            return Failing(InsufficientBalance());
        });

        await Should.ThrowAsync<LLMProviderHttpException>(async () => await Drain(_sut.ProcessStreamAsync(request)));

        _budget.Received(1).Settle(_reservation, 12);
        _budget.DidNotReceive().Release(Arg.Any<PlaygroundBudgetReservation>());
    }

    [Test]
    public async Task ProcessStreamAsync_AnUnrelatedExceptionBeforeAnyToken_KeepsTheReservation()
    {
        var request = Request();
        _inner.ProcessStreamAsync(request, Arg.Any<CancellationToken>()).Returns(Failing(new FormatException("bad frame")));

        await Should.ThrowAsync<FormatException>(async () => await Drain(_sut.ProcessStreamAsync(request)));

        _budget.DidNotReceive().Release(Arg.Any<PlaygroundBudgetReservation>());
    }

    [Test]
    public async Task OversizedRequest_ReservesNoMoreThanThePerCallCeiling()
    {
        var request = Request();
        request.SystemPrompt = new string('x', 5_000_000);
        _inner.ProcessAsync(request, Arg.Any<CancellationToken>()).Returns(new LLMProviderResponse { Success = true });

        await _sut.ProcessAsync(request);

        _budget.Received(1).TryReserve(Arg.Any<PlaygroundVisitor?>(), PlaygroundTokenLimitConstants.MaxReservationTokens);
    }

    [Test]
    public void Estimate_OfANormalTurn_IsNotClippedByTheCeiling()
    {
        var request = Request();
        request.MaxTokens = LLMModelDefaults.DefaultRequestMaxTokens;

        PlaygroundTokenEstimator.EstimateReservation(request).ShouldBeLessThan(PlaygroundTokenLimitConstants.MaxReservationTokens);
    }
}
