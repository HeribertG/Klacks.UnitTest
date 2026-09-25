// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the turn stop request of the chat controller: the cancel endpoint answers 202 only to the
/// owner of a running turn and 404 in every other case (foreign, unknown, finished, no user), never 403 or
/// 401; and the streaming endpoint registers its turn before the orchestrator runs and removes it on every
/// way out - normal end, orchestrator failure, client disconnect - while a fast-path, empty-utterance or
/// empty-user request never registers at all.
/// </summary>

using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Application.Interfaces.Klacksy;
using Klacks.Api.Application.Klacksy.Models;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.Api.Presentation.Controllers.Assistant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;

namespace Klacks.UnitTest.Controllers.Assistant;

[TestFixture]
public class ChatControllerCancelTests
{
    private const string OwnerId = "11111111-1111-1111-1111-111111111111";
    private const string StrangerId = "22222222-2222-2222-2222-222222222222";
    private const string CancelRouteTemplate = "turns/{turnId:guid}/cancel";
    private const string DashboardRoute = "/workplace/dashboard";
    private const string DashboardTarget = "dashboard";
    private const string WriteLabel = "Create employee";

    private SpyTurnRegistry _registry = null!;
    private IInterruptedTurnFinalizer _finalizer = null!;
    private ILLMStreamingOrchestrator _orchestrator = null!;
    private INavigationTargetMatcher _navMatcher = null!;
    private IUtteranceNormalizer _normalizer = null!;
    private Func<LLMStreamRequest, CancellationToken, IAsyncEnumerable<SseChunk>> _script = null!;
    private MemoryStream _body = null!;

    [SetUp]
    public void SetUp()
    {
        _registry = new SpyTurnRegistry(new ActiveTurnRegistry());
        _finalizer = Substitute.For<IInterruptedTurnFinalizer>();
        _orchestrator = Substitute.For<ILLMStreamingOrchestrator>();
        _normalizer = Substitute.For<IUtteranceNormalizer>();
        _navMatcher = Substitute.For<INavigationTargetMatcher>();
        _body = new MemoryStream();

        _normalizer.Normalize(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new NormalizedUtterance("zeig mir alles", "zeig mir alles", false, false));
        _navMatcher.Match(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(NoNavigationMatch());

        _script = (_, _) => Chunks(SseChunk.Done());
        _orchestrator.ProcessStreamAsync(Arg.Any<LLMStreamRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => _script(call.Arg<LLMStreamRequest>(), call.Arg<CancellationToken>()));
    }

    [Test]
    public void CancelTurn_ByTheOwnerOfARunningTurn_Returns202WithAcceptedTrueAndCancelsTheToken()
    {
        var turnId = Guid.NewGuid();
        var token = _registry.Register(turnId, OwnerId);

        var result = ControllerFor(OwnerId).CancelTurn(turnId);

        var accepted = result.Result.ShouldBeOfType<AcceptedResult>();
        accepted.StatusCode.ShouldBe(StatusCodes.Status202Accepted);
        accepted.Value.ShouldBeOfType<CancelTurnResponse>().Accepted.ShouldBeTrue();
        token.IsCancellationRequested.ShouldBeTrue();
    }

    [Test]
    public void CancelTurn_ByAnotherUser_Returns404AndLeavesTheTurnRunning()
    {
        var turnId = Guid.NewGuid();
        var token = _registry.Register(turnId, OwnerId);

        var result = ControllerFor(StrangerId).CancelTurn(turnId);

        result.Result.ShouldBeOfType<NotFoundResult>();
        token.IsCancellationRequested.ShouldBeFalse();
    }

    [Test]
    public void CancelTurn_ForAnUnknownTurn_Returns404()
    {
        ControllerFor(OwnerId).CancelTurn(Guid.NewGuid()).Result.ShouldBeOfType<NotFoundResult>();
    }

    [Test]
    public void CancelTurn_ForAFinishedTurn_Returns404()
    {
        var turnId = Guid.NewGuid();
        _registry.Register(turnId, OwnerId);
        _registry.Complete(turnId);

        ControllerFor(OwnerId).CancelTurn(turnId).Result.ShouldBeOfType<NotFoundResult>();
    }

    [Test]
    public void CancelTurn_Twice_Returns202BothTimes()
    {
        var turnId = Guid.NewGuid();
        _registry.Register(turnId, OwnerId);
        var controller = ControllerFor(OwnerId);

        controller.CancelTurn(turnId).Result.ShouldBeOfType<AcceptedResult>();
        controller.CancelTurn(turnId).Result.ShouldBeOfType<AcceptedResult>();
    }

    [Test]
    public void CancelTurn_WithoutANameIdentifierClaim_Returns404NotAnAuthFailure()
    {
        var turnId = Guid.NewGuid();
        var token = _registry.Register(turnId, OwnerId);
        var controller = ControllerFor(null);

        var result = controller.CancelTurn(turnId);

        result.Result.ShouldBeOfType<NotFoundResult>();
        token.IsCancellationRequested.ShouldBeFalse();
    }

    [Test]
    public void CancelTurn_IsRoutedByGuidAndExemptFromTheChatRateLimit()
    {
        var method = typeof(ChatController).GetMethod(nameof(ChatController.CancelTurn))!;

        method.GetCustomAttribute<HttpPostAttribute>()!.Template.ShouldBe(CancelRouteTemplate);
        method.GetCustomAttribute<DisableRateLimitingAttribute>().ShouldNotBeNull();
    }

    [Test]
    public void CancelTurn_InheritsTheClassAuthorizationInsteadOfDeclaringItsOwn()
    {
        var method = typeof(ChatController).GetMethod(nameof(ChatController.CancelTurn))!;

        method.GetCustomAttributes<AuthorizeAttribute>(inherit: false).ShouldBeEmpty();
        typeof(ChatController).GetCustomAttribute<AuthorizeAttribute>()!.AuthenticationSchemes
            .ShouldNotBeNullOrEmpty();
    }

    [Test]
    public async Task Stream_RegistersTheTurnBeforeTheOrchestratorRunsAndHandsItsTokenOver()
    {
        LLMStreamRequest? seen = null;
        var registeredWhenTheOrchestratorStarted = 0;
        _script = (request, _) =>
        {
            seen = request;
            registeredWhenTheOrchestratorStarted = _registry.Registered.Count;
            return Chunks(SseChunk.Done());
        };

        await StreamAsync(OwnerId);

        registeredWhenTheOrchestratorStarted.ShouldBe(1);
        _registry.Registered.Single().ShouldBe((seen!.TurnId, OwnerId));
        seen.StopToken.CanBeCanceled.ShouldBeTrue();
    }

    [Test]
    public async Task Stream_AStopRequestedWhileStreamingCancelsThePassedTokenButNotTheRequestToken()
    {
        var stopSeen = false;
        var requestTokenSeen = true;
        _script = (request, requestToken) => RunAsync(request, requestToken);

        async IAsyncEnumerable<SseChunk> RunAsync(
            LLMStreamRequest request, [EnumeratorCancellation] CancellationToken requestToken)
        {
            await Task.Yield();
            ControllerFor(OwnerId).CancelTurn(request.TurnId).Result.ShouldBeOfType<AcceptedResult>();
            stopSeen = request.StopToken.IsCancellationRequested;
            requestTokenSeen = requestToken.IsCancellationRequested;
            yield return SseChunk.Done();
        }

        await StreamAsync(OwnerId);

        stopSeen.ShouldBeTrue();
        requestTokenSeen.ShouldBeFalse();
    }

    [Test]
    public async Task Stream_RemovesTheTurnAfterANormalEnd()
    {
        await StreamAsync(OwnerId);

        _registry.Completed.Count.ShouldBe(1);
        _registry.Inner.ActiveCount.ShouldBe(0);
    }

    [Test]
    public async Task Stream_RemovesTheTurnWhenTheOrchestratorThrows()
    {
        _script = (_, _) => Throwing(new InvalidOperationException("boom"));

        await StreamAsync(OwnerId);

        _registry.Inner.ActiveCount.ShouldBe(0);
        _registry.Completed.Count.ShouldBe(1);
        ReadBody().ShouldContain("event: error");
    }

    [Test]
    public async Task Stream_RemovesTheTurnWhenTheClientDisconnects()
    {
        using var disconnect = new CancellationTokenSource();
        _script = (_, _) =>
        {
            disconnect.Cancel();
            return Throwing(new OperationCanceledException(disconnect.Token));
        };

        await StreamAsync(OwnerId, disconnect.Token);

        _registry.Inner.ActiveCount.ShouldBe(0);
        _registry.Completed.Count.ShouldBe(1);
    }

    [Test]
    public async Task Stream_AStopCancellationThatEscapesTheTurn_IsNoErrorEventAndEndsCleanly()
    {
        _script = (request, _) =>
        {
            ControllerFor(OwnerId).CancelTurn(request.TurnId).Result.ShouldBeOfType<AcceptedResult>();
            return Throwing(new OperationCanceledException(request.StopToken));
        };

        await StreamAsync(OwnerId);

        ReadBody().ShouldNotContain("event: error");
        _registry.Inner.ActiveCount.ShouldBe(0);
        _registry.Completed.Count.ShouldBe(1);
    }

    [Test]
    public async Task Stream_ACancellationThatIsNeitherAStopNorADisconnect_IsStillAnErrorEvent()
    {
        _script = (_, _) => Throwing(new OperationCanceledException());

        await StreamAsync(OwnerId);

        ReadBody().ShouldContain("event: error");
        _registry.Inner.ActiveCount.ShouldBe(0);
    }

    [Test]
    public async Task Stream_AfterANormalEnd_TheFinalizerIsCalledWithTheTurnAndNoError()
    {
        LLMStreamRequest? seen = null;
        _script = (request, _) =>
        {
            seen = request;
            return Chunks(SseChunk.Done());
        };

        await StreamAsync(OwnerId);

        await _finalizer.Received(1).FinalizeAsync(OwnerId, seen!.TurnId, false);
    }

    [Test]
    public async Task Stream_WhenTheOrchestratorThrows_TheFinalizerHearsOfTheFailureSoItIsNotLabelledInterrupted()
    {
        _script = (_, _) => Throwing(new InvalidOperationException("boom"));

        await StreamAsync(OwnerId);

        await _finalizer.Received(1).FinalizeAsync(OwnerId, Arg.Any<Guid>(), true);
    }

    [Test]
    public async Task Stream_ACancellationThatIsNeitherAStopNorADisconnect_IsAFailureForTheFinalizer()
    {
        _script = (_, _) => Throwing(new OperationCanceledException());

        await StreamAsync(OwnerId);

        await _finalizer.Received(1).FinalizeAsync(OwnerId, Arg.Any<Guid>(), true);
    }

    [Test]
    public async Task Stream_WhenTheClientDisconnects_TheFinalizerIsCalledWithoutAFailure()
    {
        using var disconnect = new CancellationTokenSource();
        _script = (_, _) =>
        {
            disconnect.Cancel();
            return Throwing(new OperationCanceledException(disconnect.Token));
        };

        await StreamAsync(OwnerId, disconnect.Token);

        await _finalizer.Received(1).FinalizeAsync(OwnerId, Arg.Any<Guid>(), false);
    }

    [Test]
    public async Task Stream_AStopCancellationThatEscapesTheTurn_IsFinalizedAsInterruptedNotAsAFailure()
    {
        _script = (request, _) =>
        {
            ControllerFor(OwnerId).CancelTurn(request.TurnId).Result.ShouldBeOfType<AcceptedResult>();
            return Throwing(new OperationCanceledException(request.StopToken));
        };

        await StreamAsync(OwnerId);

        await _finalizer.Received(1).FinalizeAsync(OwnerId, Arg.Any<Guid>(), false);
    }

    [Test]
    public async Task Stream_AStopCancellationThatEscapesTheTurn_TheClientStillHearsTurnStoppedThenDone()
    {
        LLMStreamRequest? seen = null;
        FinalizerClaimsTheStop(new StoppedTurnSummary(new[] { WriteLabel }, 1));
        _script = (request, _) =>
        {
            seen = request;
            return StopEscapes(request);
        };

        await StreamAsync(OwnerId);

        var body = ReadBody();
        EventCount(body, "turn_stopped").ShouldBe(1);
        body.ShouldContain(seen!.TurnId.ToString());
        body.ShouldContain(WriteLabel);
        body.IndexOf("event: turn_stopped", StringComparison.Ordinal)
            .ShouldBeLessThan(body.IndexOf("event: done", StringComparison.Ordinal));
        LastEventName(body).ShouldBe("done");
        body.ShouldNotContain("event: error");
        body.ShouldNotContain("event: metadata");
        _registry.Inner.ActiveCount.ShouldBe(0);
    }

    [Test]
    public async Task Stream_AStopCancellationThatEscapesTheTurn_ButWasPersistedByTheStopTail_AddsNothing()
    {
        FinalizerClaimsNothing();
        _script = (request, _) => StopEscapes(request);

        await StreamAsync(OwnerId);

        EventCount(ReadBody(), "turn_stopped").ShouldBe(0);
    }

    [Test]
    public async Task Stream_WhenTheStopTailAlreadySentTurnStopped_ItIsNeverSentASecondTime()
    {
        FinalizerClaimsTheStop(new StoppedTurnSummary(new[] { WriteLabel }, 1));
        _script = (request, _) => TailThenEscape(request);

        await StreamAsync(OwnerId);

        EventCount(ReadBody(), "turn_stopped").ShouldBe(1);
        EventCount(ReadBody(), "done").ShouldBe(1);
    }

    [Test]
    public async Task Stream_AStopThatArrivesAfterTheDoneChunk_IsAcceptedButAddsNoEvent()
    {
        FinalizerClaimsNothing();
        _script = (request, _) => DoneThenStop(request);

        await StreamAsync(OwnerId);

        var body = ReadBody();
        EventCount(body, "turn_stopped").ShouldBe(0);
        EventCount(body, "done").ShouldBe(1);
        LastEventName(body).ShouldBe("done");
    }

    [Test]
    public async Task Stream_AClientDisconnectIsNeverAnsweredWithTurnStopped()
    {
        using var disconnect = new CancellationTokenSource();
        FinalizerClaimsTheStop(new StoppedTurnSummary(new[] { WriteLabel }, 1));
        _script = (request, _) =>
        {
            disconnect.Cancel();
            return Throwing(new OperationCanceledException(disconnect.Token));
        };

        await StreamAsync(OwnerId, disconnect.Token);

        EventCount(ReadBody(), "turn_stopped").ShouldBe(0);
    }

    [Test]
    public async Task Stream_AFailedTurnIsNeverAnsweredWithTurnStoppedEvenIfTheFinalizerReturnedASummary()
    {
        FinalizerClaimsTheStop(new StoppedTurnSummary(new[] { WriteLabel }, 1));
        _script = (_, _) => Throwing(new InvalidOperationException("boom"));

        await StreamAsync(OwnerId);

        EventCount(ReadBody(), "turn_stopped").ShouldBe(0);
        ReadBody().ShouldContain("event: error");
    }

    [Test]
    public async Task Stream_WhenTheClientIsGoneBeforeTurnStoppedIsWritten_TheStreamEndsQuietlyAndTheTurnIsRemoved()
    {
        FinalizerClaimsTheStop(new StoppedTurnSummary(new[] { WriteLabel }, 1));
        _script = (request, _) => StopEscapes(request);

        var controller = ControllerFor(OwnerId);
        controller.ControllerContext.HttpContext.Response.Body = new ThrowsOnStopConfirmationStream(_body);

        await Should.NotThrowAsync(() => controller.ProcessMessageStream(
            new LLMRequest { Message = "Zeig mir alles", ConversationId = null }, CancellationToken.None));

        _registry.Inner.ActiveCount.ShouldBe(0);
        _registry.Completed.Count.ShouldBe(1);
    }

    [Test]
    public async Task Stream_TheFinalizerRunsWhileTheTurnIsStillRegisteredAndBeforeItIsRemoved()
    {
        var completedWhenFinalizing = -1;
        var registeredWhenFinalizing = -1;
        _finalizer.FinalizeAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<bool>())
            .Returns(_ =>
            {
                completedWhenFinalizing = _registry.Completed.Count;
                registeredWhenFinalizing = _registry.Inner.ActiveCount;
                return Task.FromResult<StoppedTurnSummary?>(null);
            });

        await StreamAsync(OwnerId);

        completedWhenFinalizing.ShouldBe(0);
        registeredWhenFinalizing.ShouldBe(1);
        _registry.Inner.ActiveCount.ShouldBe(0);
    }

    [Test]
    public async Task Stream_EvenIfTheFinalizerFailsUnexpectedly_TheTurnIsRemovedFromTheRegistry()
    {
        _finalizer.FinalizeAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<bool>())
            .Returns<Task<StoppedTurnSummary?>>(_ => throw new InvalidOperationException("finalizer broke"));

        await Should.ThrowAsync<InvalidOperationException>(() => StreamAsync(OwnerId));

        _registry.Inner.ActiveCount.ShouldBe(0);
        _registry.Completed.Count.ShouldBe(1);
    }

    [Test]
    public async Task Stream_OnTheFastPathOrAnEmptyUtterance_NoTurnIsFinalized()
    {
        _normalizer.Normalize(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new NormalizedUtterance(string.Empty, string.Empty, false, true));

        await StreamAsync(OwnerId);

        await _finalizer.DidNotReceiveWithAnyArgs().FinalizeAsync(default!, default, default);
    }

    [Test]
    public async Task Stream_WithoutAUserId_StillStreamsButRegistersNothing()
    {
        LLMStreamRequest? seen = null;
        _script = (request, _) =>
        {
            seen = request;
            return Chunks(SseChunk.Done());
        };

        await StreamAsync(userId: null);

        seen.ShouldNotBeNull();
        seen.StopToken.CanBeCanceled.ShouldBeFalse();
        _registry.Registered.ShouldBeEmpty();
        ReadBody().ShouldContain("event: done");
    }

    [Test]
    public async Task Stream_OnTheNavigationFastPath_RegistersNothing()
    {
        _navMatcher.Match(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new NavigationMatchResult
            {
                TargetId = DashboardTarget,
                Route = DashboardRoute,
                Score = 1.0,
                Tier = NavigationMatchTier.Exact,
                Candidates = new[] { new NavigationCandidate(DashboardTarget, DashboardRoute, 1.0) }
            });

        await StreamAsync(OwnerId);

        _registry.Registered.ShouldBeEmpty();
        _orchestrator.DidNotReceiveWithAnyArgs().ProcessStreamAsync(default!, default);
    }

    [Test]
    public async Task Stream_OnAnEmptyUtterance_RegistersNothing()
    {
        _normalizer.Normalize(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new NormalizedUtterance(string.Empty, string.Empty, false, true));

        await StreamAsync(OwnerId);

        _registry.Registered.ShouldBeEmpty();
    }

    private void FinalizerClaimsTheStop(StoppedTurnSummary summary) =>
        _finalizer.FinalizeAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<bool>())
            .Returns(Task.FromResult<StoppedTurnSummary?>(summary));

    private void FinalizerClaimsNothing() =>
        _finalizer.FinalizeAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<bool>())
            .Returns(Task.FromResult<StoppedTurnSummary?>(null));

    private IAsyncEnumerable<SseChunk> StopEscapes(LLMStreamRequest request)
    {
        ControllerFor(OwnerId).CancelTurn(request.TurnId).Result.ShouldBeOfType<AcceptedResult>();
        return Throwing(new OperationCanceledException(request.StopToken));
    }

    private async IAsyncEnumerable<SseChunk> DoneThenStop(LLMStreamRequest request)
    {
        await Task.Yield();
        yield return SseChunk.Done();
        ControllerFor(OwnerId).CancelTurn(request.TurnId).Result.ShouldBeOfType<AcceptedResult>();
    }

    private async IAsyncEnumerable<SseChunk> TailThenEscape(LLMStreamRequest request)
    {
        await Task.Yield();
        ControllerFor(OwnerId).CancelTurn(request.TurnId).Result.ShouldBeOfType<AcceptedResult>();
        yield return SseChunk.TurnStopped(request.TurnId, new List<string> { WriteLabel }, 1);
        yield return SseChunk.Done();
        throw new OperationCanceledException(request.StopToken);
    }

    private static string LastEventName(string body)
    {
        var lastBlock = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Last();
        return lastBlock.Split('\n')[0].Substring("event: ".Length);
    }

    private static int EventCount(string body, string eventName) =>
        body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Count(block => block.StartsWith("event: " + eventName + "\n", StringComparison.Ordinal));

    private async Task StreamAsync(string? userId, CancellationToken requestAborted = default)
    {
        var controller = ControllerFor(userId);
        controller.ControllerContext.HttpContext.Response.Body = _body;

        await controller.ProcessMessageStream(
            new LLMRequest { Message = "Zeig mir alles", ConversationId = null }, requestAborted);
    }

    private string ReadBody()
    {
        _body.Position = 0;
        return new StreamReader(_body).ReadToEnd();
    }

    private ChatController ControllerFor(string? userId)
    {
        var principal = userId == null
            ? new ClaimsPrincipal(new ClaimsIdentity("Test"))
            : new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test"));

        return new ChatController(
            Substitute.For<ILogger<ChatController>>(),
            Substitute.For<IMediator>(),
            Substitute.For<IAgentSkillRepository>(),
            Substitute.For<IAgentRepository>(),
            _orchestrator,
            Substitute.For<ISkillCacheService>(),
            _normalizer,
            _navMatcher,
            Substitute.For<INavigationTargetCacheService>(),
            Substitute.For<INavigationFeedbackLogger>(),
            Substitute.For<INavigationMissDetector>(),
            Substitute.For<ILLMRepository>(),
            Substitute.For<IUserActivityTracker>(),
            Substitute.For<INavigationEntityRouteGuard>(),
            _registry,
            _finalizer)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };
    }

    private static NavigationMatchResult NoNavigationMatch() => new()
    {
        TargetId = null,
        Route = null,
        Score = 0.0,
        Tier = NavigationMatchTier.None,
        Candidates = Array.Empty<NavigationCandidate>()
    };

    private static async IAsyncEnumerable<SseChunk> Chunks(params SseChunk[] chunks)
    {
        await Task.Yield();
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }
    }

    private static async IAsyncEnumerable<SseChunk> Throwing(Exception exception)
    {
        await Task.Yield();
        if (exception != null)
        {
            throw exception;
        }

        yield break;
    }

    private sealed class ThrowsOnStopConfirmationStream : Stream
    {
        private const string StopConfirmationEvent = "event: turn_stopped";
        private readonly Stream _inner;

        public ThrowsOnStopConfirmationStream(Stream inner) => _inner = inner;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => WriteCore(buffer.AsSpan(offset, count));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCore(buffer.Span);
            return ValueTask.CompletedTask;
        }

        private void WriteCore(ReadOnlySpan<byte> buffer)
        {
            if (System.Text.Encoding.UTF8.GetString(buffer).Contains(StopConfirmationEvent, StringComparison.Ordinal))
            {
                throw new IOException("The client is gone.");
            }

            _inner.Write(buffer);
        }
    }

    private sealed class SpyTurnRegistry : IActiveTurnRegistry
    {
        public SpyTurnRegistry(ActiveTurnRegistry inner) => Inner = inner;

        public ActiveTurnRegistry Inner { get; }

        public List<(Guid TurnId, string UserId)> Registered { get; } = new();

        public List<Guid> Completed { get; } = new();

        public CancellationToken Register(Guid turnId, string userId)
        {
            Registered.Add((turnId, userId));
            return Inner.Register(turnId, userId);
        }

        public StopRequestOutcome RequestStop(Guid turnId, string userId) => Inner.RequestStop(turnId, userId);

        public bool IsStopRequested(Guid turnId) => Inner.IsStopRequested(turnId);

        public void Complete(Guid turnId)
        {
            Completed.Add(turnId);
            Inner.Complete(turnId);
        }
    }
}
