// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Both chat entry points delegate their correction/toolset preparation to the single shared
/// ICorrectionTurnPreparer (see CorrectionTurnPreparerTests for the pipeline itself), so a divergence
/// here can only be a wiring bug: calling it with the wrong request fields, or mapping its result onto
/// LLMContext differently. The same holds for the explanation depth: both entry points must put exactly the profile
/// IExplainDepthResolver resolved for the calling user onto the context. The explain-offer seam is fed by a response
/// (and a Metadata chunk) built by the real LLMResponseBuilder, so the typed calls - not the anonymous wire projection -
/// must reach IGateExplainOfferService.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class GracefulCorrectionEntryPointWiringTests
{
    private const string UserId = "11111111-1111-1111-1111-111111111111";
    private const string ConversationId = "conv-1";
    private const string CorrectionMessage = "Nein, ich meinte alle Mitarbeitenden in die Gruppe.";
    private const string ContextNote = "CORRECTION - the previous turn searched for customers.";
    private const string ClarificationReply = "Did you mean adding clients to a group, or listing them?";
    private const string HeldToken = "held-token";
    private const string ModelAnswer = "Shall I accept the scenario?";

    private ICorrectionTurnPreparer _correctionTurnPreparer = null!;
    private ISkillCacheService _skillCache = null!;
    private ILLMService _llmService = null!;
    private IExplainDepthResolver _explainDepthResolver = null!;
    private IGateExplainOfferService _gateExplainOffers = null!;
    private ExplainDepthProfile _resolvedDepth = null!;
    private LLMContext? _capturedContext;

    [SetUp]
    public void SetUp()
    {
        _capturedContext = null;
        _correctionTurnPreparer = Substitute.For<ICorrectionTurnPreparer>();
        _correctionTurnPreparer.PrepareAsync(
                Arg.Any<Agent?>(), Arg.Any<List<string>>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new CorrectionTurnPreparation(new SkillToolsetResult(), null, UndoWasHeld: false));

        _skillCache = Substitute.For<ISkillCacheService>();
        _skillCache.GetDefaultAgentAsync(Arg.Any<CancellationToken>())
            .Returns(new Agent { Id = Guid.NewGuid(), Name = "Klacksy" });

        _resolvedDepth = new ExplainDepthProfile(ExplainExperienceLevel.Advanced, IsExplicitOverride: true);
        _explainDepthResolver = Substitute.For<IExplainDepthResolver>();
        _explainDepthResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_resolvedDepth);

        _gateExplainOffers = Substitute.For<IGateExplainOfferService>();
        _gateExplainOffers.AugmentRepliesAsync(
                Arg.Any<LLMContext>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<LLMFunctionCall>?>(),
                Arg.Any<SuggestedRepliesConfig?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<SuggestedRepliesConfig?>(3));

        _llmService = Substitute.For<ILLMService>();
        _llmService.ProcessAsync(Arg.Do<LLMContext>(c => _capturedContext = c))
            .Returns(new LLMResponse());
        _llmService.ProcessStreamAsync(Arg.Do<LLMContext>(c => _capturedContext = c), Arg.Any<CancellationToken>())
            .Returns(_ => EmptyStream());
    }

    private static async IAsyncEnumerable<SseChunk> EmptyStream()
    {
        await Task.Yield();
        yield break;
    }

    private void GivenAFullCorrectionPreparation()
    {
        var toolset = new SkillToolsetResult
        {
            Functions = [],
            HasDomainSkillContext = true,
            AssemblyMs = 5
        };
        var correction = new GracefulCorrectionOutcome(ContextNote, ClarificationReply, ["candidate_a", "candidate_b"]);

        _correctionTurnPreparer.PrepareAsync(
                Arg.Any<Agent?>(), Arg.Any<List<string>>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new CorrectionTurnPreparation(toolset, correction, UndoWasHeld: true));
    }

    private ProcessLLMMessageCommandHandler CreateHandler()
    {
        var providerOrchestrator = new LLMProviderOrchestrator(
            Substitute.For<ILogger<LLMProviderOrchestrator>>(),
            Substitute.For<ILLMProviderFactory>(),
            Substitute.For<ILLMRepository>());

        return new ProcessLLMMessageCommandHandler(
            _llmService, Substitute.For<IAgentRepository>(), _skillCache, _correctionTurnPreparer,
            Substitute.For<IPlanningScopeEnricher>(),
            Substitute.For<IEntityCandidateGrounder>(),
            providerOrchestrator,
            Substitute.For<IContextBudgetPolicy>(),
            _explainDepthResolver,
            _gateExplainOffers,
            Substitute.For<ILogger<ProcessLLMMessageCommandHandler>>());
    }

    private LLMStreamingOrchestrator CreateOrchestrator()
    {
        var providerOrchestrator = new LLMProviderOrchestrator(
            Substitute.For<ILogger<LLMProviderOrchestrator>>(),
            Substitute.For<ILLMProviderFactory>(),
            Substitute.For<ILLMRepository>());

        return new LLMStreamingOrchestrator(
            _llmService, _skillCache, _correctionTurnPreparer,
            Substitute.For<IPlanningScopeEnricher>(),
            Substitute.For<IEntityCandidateGrounder>(),
            providerOrchestrator,
            Substitute.For<IContextBudgetPolicy>(),
            _explainDepthResolver,
            _gateExplainOffers,
            Substitute.For<ILogger<LLMStreamingOrchestrator>>());
    }

    private static ProcessLLMMessageCommand Command() => new()
    {
        Message = CorrectionMessage,
        UserId = UserId,
        ConversationId = ConversationId,
        UserRights = new List<string>()
    };

    private static LLMStreamRequest StreamRequest() => new()
    {
        Message = CorrectionMessage,
        UserId = UserId,
        ConversationId = ConversationId,
        UserRights = new List<string>()
    };

    private async Task Drain(IAsyncEnumerable<SseChunk> source)
    {
        await foreach (var chunk in source)
        {
            _ = chunk;
        }
    }

    private void AssertContextCarriesThePreparation()
    {
        _capturedContext.ShouldNotBeNull();
        _capturedContext!.CorrectionNote.ShouldBe(ContextNote);
        _capturedContext.CorrectionClarificationReply.ShouldBe(ClarificationReply);
        _capturedContext.GracefulCorrectionApplied.ShouldBeTrue();
        _capturedContext.CorrectionUndoOffered.ShouldBeTrue();
    }

    [Test]
    public async Task NonStreaming_ThreadsThePreparationResultOntoTheContext()
    {
        GivenAFullCorrectionPreparation();

        await CreateHandler().Handle(Command(), CancellationToken.None);

        AssertContextCarriesThePreparation();
    }

    [Test]
    public async Task Streaming_ThreadsThePreparationResultOntoTheContext()
    {
        GivenAFullCorrectionPreparation();

        await Drain(CreateOrchestrator().ProcessStreamAsync(StreamRequest()));

        AssertContextCarriesThePreparation();
    }

    [Test]
    public async Task NonStreaming_WithoutACorrection_LeavesTheCorrectionFieldsEmpty()
    {
        await CreateHandler().Handle(Command(), CancellationToken.None);

        _capturedContext.ShouldNotBeNull();
        _capturedContext!.CorrectionNote.ShouldBeNull();
        _capturedContext.GracefulCorrectionApplied.ShouldBeFalse();
        _capturedContext.CorrectionUndoOffered.ShouldBeFalse();
    }

    [Test]
    public async Task Streaming_WithoutACorrection_LeavesTheCorrectionFieldsEmpty()
    {
        await Drain(CreateOrchestrator().ProcessStreamAsync(StreamRequest()));

        _capturedContext.ShouldNotBeNull();
        _capturedContext!.CorrectionNote.ShouldBeNull();
        _capturedContext.GracefulCorrectionApplied.ShouldBeFalse();
        _capturedContext.CorrectionUndoOffered.ShouldBeFalse();
    }

    [Test]
    public async Task NonStreaming_CallsThePreparerWithTheRequestFields()
    {
        await CreateHandler().Handle(Command(), CancellationToken.None);

        await _correctionTurnPreparer.Received(1).PrepareAsync(
            Arg.Any<Agent?>(), Arg.Any<List<string>>(), CorrectionMessage, ConversationId, UserId,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Streaming_CallsThePreparerWithTheRequestFields()
    {
        await Drain(CreateOrchestrator().ProcessStreamAsync(StreamRequest()));

        await _correctionTurnPreparer.Received(1).PrepareAsync(
            Arg.Any<Agent?>(), Arg.Any<List<string>>(), CorrectionMessage, ConversationId, UserId,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NonStreaming_PutsTheResolvedExplainDepthOntoTheContext()
    {
        await CreateHandler().Handle(Command(), CancellationToken.None);

        _capturedContext.ShouldNotBeNull();
        _capturedContext!.ExplainDepth.ShouldBeSameAs(_resolvedDepth);
        await _explainDepthResolver.Received(1).ResolveAsync(UserId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Streaming_PutsTheResolvedExplainDepthOntoTheContext()
    {
        await Drain(CreateOrchestrator().ProcessStreamAsync(StreamRequest()));

        _capturedContext.ShouldNotBeNull();
        _capturedContext!.ExplainDepth.ShouldBeSameAs(_resolvedDepth);
        await _explainDepthResolver.Received(1).ResolveAsync(UserId, Arg.Any<CancellationToken>());
    }

    private static List<LLMFunctionCall> HeldAcceptScenario() =>
    [
        new LLMFunctionCall
        {
            FunctionName = ScenarioAcceptDefaults.SkillName,
            Success = false,
            RequiresConfirmation = true,
            IssuedConfirmationToken = HeldToken
        }
    ];

    private static LLMResponse BuiltByTheRealBuilder(List<LLMFunctionCall> calls) =>
        new LLMResponseBuilder().BuildSuccessResponse(
            new LLMProviderResponse { Content = ModelAnswer, Usage = new Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage(), Success = true },
            ConversationId, ModelAnswer, calls);

    private static bool IsTheHeldAcceptScenarioCall(IReadOnlyList<LLMFunctionCall>? calls) =>
        calls is { Count: 1 }
        && calls[0].FunctionName == ScenarioAcceptDefaults.SkillName
        && calls[0].RequiresConfirmation
        && calls[0].IssuedConfirmationToken == HeldToken;

    [Test]
    public async Task NonStreaming_SettlesTheOfferBeforeTheModel_AndSwapsInTheAugmentedReplies()
    {
        var augmented = new SuggestedRepliesConfig { Prompt = "augmented" };
        var built = BuiltByTheRealBuilder(HeldAcceptScenario());
        _llmService.ProcessAsync(Arg.Do<LLMContext>(c => _capturedContext = c))
            .Returns(built);
        _gateExplainOffers.AugmentRepliesAsync(
                Arg.Any<LLMContext>(), ConversationId,
                Arg.Is<IReadOnlyList<LLMFunctionCall>?>(calls => IsTheHeldAcceptScenarioCall(calls)),
                Arg.Any<SuggestedRepliesConfig?>(), Arg.Any<CancellationToken>())
            .Returns(augmented);

        var response = await CreateHandler().Handle(Command(), CancellationToken.None);

        response.SuggestedReplies.ShouldBeSameAs(augmented);
        await _gateExplainOffers.Received(1).PrepareTurnAsync(_capturedContext!, Arg.Any<CancellationToken>());
        Received.InOrder(() =>
        {
            _gateExplainOffers.PrepareTurnAsync(Arg.Any<LLMContext>(), Arg.Any<CancellationToken>());
            _llmService.ProcessAsync(Arg.Any<LLMContext>(), Arg.Any<CancellationToken>());
            _gateExplainOffers.AugmentRepliesAsync(
                Arg.Any<LLMContext>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<LLMFunctionCall>?>(),
                Arg.Any<SuggestedRepliesConfig?>(), Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task Streaming_SettlesTheOfferBeforeTheModel_AndAugmentsOnlyTheMetadataChunk()
    {
        var augmented = new SuggestedRepliesConfig { Prompt = "augmented" };
        var content = SseChunk.Content("text");
        var metadata = SseChunk.Metadata(BuiltByTheRealBuilder(HeldAcceptScenario()));
        _llmService.ProcessStreamAsync(Arg.Do<LLMContext>(c => _capturedContext = c), Arg.Any<CancellationToken>())
            .Returns(_ => Chunks(SseChunk.StreamStart("streamed-conv"), content, metadata, SseChunk.Done()));
        _gateExplainOffers.AugmentRepliesAsync(
                Arg.Any<LLMContext>(), "streamed-conv",
                Arg.Is<IReadOnlyList<LLMFunctionCall>?>(calls => IsTheHeldAcceptScenarioCall(calls)),
                Arg.Any<SuggestedRepliesConfig?>(), Arg.Any<CancellationToken>())
            .Returns(augmented);

        await Drain(CreateOrchestrator().ProcessStreamAsync(StreamRequest()));

        metadata.SuggestedReplies.ShouldBeSameAs(augmented);
        content.SuggestedReplies.ShouldBeNull();
        await _gateExplainOffers.Received(1).PrepareTurnAsync(_capturedContext!, Arg.Any<CancellationToken>());
        await _gateExplainOffers.Received(1).AugmentRepliesAsync(
            Arg.Any<LLMContext>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<LLMFunctionCall>?>(),
            Arg.Any<SuggestedRepliesConfig?>(), Arg.Any<CancellationToken>());
    }

    private static async IAsyncEnumerable<SseChunk> Chunks(params SseChunk[] chunks)
    {
        await Task.Yield();
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }
    }
}
