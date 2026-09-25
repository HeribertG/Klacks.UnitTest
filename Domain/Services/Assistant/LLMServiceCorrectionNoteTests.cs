// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The correction note has to survive the two hops between the entry point and the provider: the fold
/// into the turn's volatile system-prompt segment, and the per-iteration combination inside the chat
/// loop. Both are pinned here, because a note that is built correctly and then dropped on the way looks
/// exactly like a correction that never engaged.
/// </summary>

using System.Diagnostics;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using ProviderLLMMessage = Klacks.Api.Domain.Services.Assistant.Providers.LLMMessage;
using ProviderLLMUsage = Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceCorrectionNoteTests
{
    private const string ConversationId = "conv-correction-note";
    private const string CorrectionNote = "CORRECTION - your previous turn searched for customers.";
    private const string TemporalContext = "Today is Tuesday.";
    private const string SoulPrompt = "You are Klacksy.";
    private const string Answer = "Verstanden - nicht die Kunden, sondern die Mitarbeitenden.";

    private ITurnPreparationService _turnPreparation = null!;
    private LLMService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _turnPreparation = Substitute.For<ITurnPreparationService>();
        _turnPreparation.PrepareAsync(Arg.Any<TurnPreparationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TurnPreparation(null, false, null, null));

        _service = new LLMService(
            logger: Substitute.For<ILogger<LLMService>>(),
            providerOrchestrator: null!,
            conversationManager: null!,
            functionExecutor: null!,
            responseBuilder: null!,
            promptBuilder: null!,
            agentRepository: null!,
            contextAssemblyPipeline: null!,
            backgroundTaskService: null!,
            recipeEngine: null!,
            recipeRunRecorder: Substitute.For<IRecipeRunRecorder>(),
            suggestionEntityNameReader: null!,
            contextBudgetPolicy: null!,
            turnPreparation: _turnPreparation,
            turnCompletionRecorder: InertTurnCompletionRecorder.Create(),
            turnState: new TurnRunState());
    }

    private static LLMContext Context(string? correctionNote) => new()
    {
        Message = "Nein, ich meinte alle Mitarbeitenden.",
        UserId = Guid.NewGuid().ToString(),
        Language = "de",
        CorrectionNote = correctionNote,
        GracefulCorrectionApplied = correctionNote != null
    };

    private static MultiTurnContext BuildContext(
        LLMContext context, ILLMProvider provider, string volatilePrompt) => new(
        context,
        new LLMModel(),
        provider,
        SystemPrompt: "system prompt",
        TruncatedHistory: new List<ProviderLLMMessage>(),
        TotalUsage: new ProviderLLMUsage(),
        Conversation: new LLMConversation { ConversationId = ConversationId },
        Stopwatch: Stopwatch.StartNew(),
        VolatilePrompt: volatilePrompt);

    private static ILLMProvider ProviderAnswering(List<LLMProviderRequest> captured)
    {
        var provider = Substitute.For<ILLMProvider>();
        provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured.Add(call.ArgAt<LLMProviderRequest>(0));
                return new LLMProviderResponse { Success = true, Content = Answer };
            });
        return provider;
    }

    [Test]
    public void TheCorrectionNote_IsFoldedIntoTheVolatileSegment()
    {
        var volatilePrompt = LLMService.BuildVolatilePrompt(
            TemporalContext, Context(CorrectionNote), SoulPrompt);

        volatilePrompt.ShouldContain(CorrectionNote);
        volatilePrompt.ShouldContain(TemporalContext);
        volatilePrompt.ShouldContain(SoulPrompt);
    }

    [Test]
    public void WithoutACorrection_TheVolatileSegmentIsUnchanged()
    {
        var withNote = LLMService.BuildVolatilePrompt(TemporalContext, Context(null), SoulPrompt);

        withNote.ShouldNotContain(CorrectionNote);
        withNote.ShouldContain(TemporalContext);
        withNote.ShouldContain(SoulPrompt);
    }

    // The second hop: the loop combines the turn's volatile segment with its own per-iteration note, and
    // the correction note must survive that combination into the request that actually leaves the process.
    [Test]
    public async Task TheCorrectionNote_ReachesTheProvidersVolatileSystemPrompt()
    {
        var context = Context(CorrectionNote);
        var volatilePrompt = LLMService.BuildVolatilePrompt(TemporalContext, context, SoulPrompt);
        var captured = new List<LLMProviderRequest>();

        await _service.ExecuteMultiTurnLoopAsync(
            BuildContext(context, ProviderAnswering(captured), volatilePrompt));

        captured.ShouldNotBeEmpty();
        captured[0].VolatileSystemPrompt.ShouldContain(CorrectionNote);
    }

    [Test]
    public async Task WithoutACorrection_TheProvidersVolatileSystemPromptCarriesNoNote()
    {
        var context = Context(null);
        var volatilePrompt = LLMService.BuildVolatilePrompt(TemporalContext, context, SoulPrompt);
        var captured = new List<LLMProviderRequest>();

        await _service.ExecuteMultiTurnLoopAsync(
            BuildContext(context, ProviderAnswering(captured), volatilePrompt));

        captured.ShouldNotBeEmpty();
        captured[0].VolatileSystemPrompt.ShouldNotContain(CorrectionNote);
    }
}
