// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for ChatController.ProcessMessageStream that the cancel and fast-path tests leave open: the
/// 400 for an empty message, the SSE response headers, the event sequence of an empty utterance and of a normal turn,
/// the fields the stream request hands to the orchestrator and the text of the error event on an unexpected failure.
/// </summary>

using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Interfaces.Klacksy;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Application.Klacksy.Models;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.Api.Presentation.Controllers.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Security.Claims;

namespace Klacks.UnitTest.Controllers.Assistant;

[TestFixture]
public class ChatControllerStreamCharacterizationTests
{
    private const string OwnerId = "11111111-1111-1111-1111-111111111111";

    private ILLMStreamingOrchestrator _orchestrator = null!;
    private INavigationTargetMatcher _navMatcher = null!;
    private IUtteranceNormalizer _normalizer = null!;
    private IInterruptedTurnFinalizer _finalizer = null!;
    private Func<LLMStreamRequest, IAsyncEnumerable<SseChunk>> _script = null!;
    private MemoryStream _body = null!;

    [SetUp]
    public void SetUp()
    {
        _orchestrator = Substitute.For<ILLMStreamingOrchestrator>();
        _normalizer = Substitute.For<IUtteranceNormalizer>();
        _navMatcher = Substitute.For<INavigationTargetMatcher>();
        _finalizer = Substitute.For<IInterruptedTurnFinalizer>();
        _body = new MemoryStream();

        _normalizer.Normalize(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new NormalizedUtterance("zeig mir alles", "zeig mir alles", false, false));
        _navMatcher.Match(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new NavigationMatchResult
            {
                TargetId = null,
                Route = null,
                Score = 0.0,
                Tier = NavigationMatchTier.None,
                Candidates = Array.Empty<NavigationCandidate>()
            });

        _script = _ => Chunks(SseChunk.Done());
        _orchestrator.ProcessStreamAsync(Arg.Any<LLMStreamRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => _script(call.Arg<LLMStreamRequest>()));
    }

    [Test]
    public async Task EmptyMessage_Answers400_AndWritesNothing()
    {
        var controller = CreateController();

        await controller.ProcessMessageStream(new LLMRequest { Message = "  " }, CancellationToken.None);

        controller.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        ReadBody().ShouldBeEmpty();
        _normalizer.DidNotReceiveWithAnyArgs().Normalize(default!, default!);
    }

    [Test]
    public async Task Stream_SetsTheServerSentEventHeaders()
    {
        var controller = CreateController();

        await controller.ProcessMessageStream(new LLMRequest { Message = "Zeig mir alles" }, CancellationToken.None);

        controller.Response.ContentType.ShouldBe("text/event-stream");
        controller.Response.Headers["Cache-Control"].ToString().ShouldBe("no-cache");
        controller.Response.Headers["Connection"].ToString().ShouldBe("keep-alive");
        controller.Response.Headers["X-Accel-Buffering"].ToString().ShouldBe("no");
    }

    [Test]
    public async Task EmptyUtterance_WritesStatusThenGreetingThenDone_WithoutTheOrchestrator()
    {
        _normalizer.Normalize(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new NormalizedUtterance("hey klacksy", string.Empty, true, true));

        await CreateController().ProcessMessageStream(new LLMRequest { Message = "hey klacksy" }, CancellationToken.None);

        var body = ReadBody();
        EventNames(body).ShouldBe([SseEventNames.Status, SseEventNames.Content, SseEventNames.Done]);
        body.ShouldContain(NavigationResponseKeys.EmptyUtteranceGreeting);
        _orchestrator.DidNotReceiveWithAnyArgs().ProcessStreamAsync(default!, default);
    }

    [Test]
    public async Task NormalTurn_WritesTheStatusEventFirst_ThenEveryOrchestratorChunkInOrder()
    {
        _script = _ => Chunks(SseChunk.Content("Hallo"), SseChunk.Content(" Welt"), SseChunk.Done());

        await CreateController().ProcessMessageStream(new LLMRequest { Message = "Zeig mir alles" }, CancellationToken.None);

        var body = ReadBody();
        EventNames(body).ShouldBe(
            [SseEventNames.Status, SseEventNames.Content, SseEventNames.Content, SseEventNames.Done]);
        body.IndexOf("Hallo", StringComparison.Ordinal).ShouldBeLessThan(body.IndexOf(" Welt", StringComparison.Ordinal));
    }

    [Test]
    public async Task NormalTurn_HandsTheRequestFieldsToTheOrchestrator()
    {
        LLMStreamRequest? seen = null;
        _script = request =>
        {
            seen = request;
            return Chunks(SseChunk.Done());
        };

        await CreateController().ProcessMessageStream(
            new LLMRequest
            {
                Message = "Zeig mir alles",
                ConversationId = "conv-1",
                ModelId = "model-1",
                Language = "de",
                IsVoiceMode = true
            },
            CancellationToken.None);

        seen.ShouldNotBeNull();
        seen.Message.ShouldBe("Zeig mir alles");
        seen.UserId.ShouldBe(OwnerId);
        seen.ConversationId.ShouldBe("conv-1");
        seen.ModelId.ShouldBe("model-1");
        seen.Language.ShouldBe("de");
        seen.IsVoiceMode.ShouldBeTrue();
        seen.TurnId.ShouldNotBe(Guid.Empty);
        seen.TurnStartTimestamp.ShouldNotBeNull();
    }

    [Test]
    public async Task UnexpectedFailure_WritesTheGenericErrorText_AfterTheChunksAlreadySent()
    {
        _script = _ => ContentThenThrow();

        await CreateController().ProcessMessageStream(new LLMRequest { Message = "Zeig mir alles" }, CancellationToken.None);

        var body = ReadBody();
        EventNames(body).ShouldBe([SseEventNames.Status, SseEventNames.Content, SseEventNames.Error]);
        body.ShouldContain(AssistantStreamErrorMessages.UnexpectedFailure);
        body.ShouldNotContain("secret detail");
    }

    private static List<string> EventNames(string body) =>
        body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block => block.Split('\n')[0].Substring("event: ".Length))
            .ToList();

    private string ReadBody()
    {
        _body.Position = 0;
        return new StreamReader(_body).ReadToEnd();
    }

    private ChatController CreateController()
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, OwnerId) }, "Test"));
        var httpContext = new DefaultHttpContext { User = principal };
        httpContext.Response.Body = _body;

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
            new ActiveTurnRegistry(),
            _finalizer)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    private static async IAsyncEnumerable<SseChunk> Chunks(params SseChunk[] chunks)
    {
        await Task.Yield();
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }
    }

    private static async IAsyncEnumerable<SseChunk> ContentThenThrow(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return SseChunk.Content("partial");
        throw new InvalidOperationException("secret detail");
    }
}
