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

    private SpyTurnRegistry _registry = null!;
    private ILLMStreamingOrchestrator _orchestrator = null!;
    private INavigationTargetMatcher _navMatcher = null!;
    private IUtteranceNormalizer _normalizer = null!;
    private Func<LLMStreamRequest, CancellationToken, IAsyncEnumerable<SseChunk>> _script = null!;
    private MemoryStream _body = null!;

    [SetUp]
    public void SetUp()
    {
        _registry = new SpyTurnRegistry(new ActiveTurnRegistry());
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
            _registry)
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
