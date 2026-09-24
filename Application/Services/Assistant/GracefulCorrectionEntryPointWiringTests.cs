// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Both chat entry points delegate their correction/toolset preparation to the single shared
/// ICorrectionTurnPreparer (see CorrectionTurnPreparerTests for the pipeline itself), so a divergence
/// here can only be a wiring bug: calling it with the wrong request fields, or mapping its result onto
/// LLMContext differently. That is all this fixture checks.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Models.Assistant;
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

    private ICorrectionTurnPreparer _correctionTurnPreparer = null!;
    private ISkillCacheService _skillCache = null!;
    private ILLMService _llmService = null!;
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
}
