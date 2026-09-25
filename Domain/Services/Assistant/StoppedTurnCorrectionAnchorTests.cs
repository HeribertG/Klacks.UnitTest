// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The reason a stopped turn is persisted at all: a correction typed while the wrong answer was still being
/// written ("No, I meant ...") must find the action that already ran. The anchor is recorded by the real
/// TurnPreparationService from what a stopped turn hands it - the answer with the interruption marker and only
/// the calls the server ran - and the graceful-correction gate G0 then has to accept it. A turn that ran
/// nothing leaves no anchor, and a recipe that waits supersedes it, exactly as for a finished turn.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class StoppedTurnCorrectionAnchorTests
{
    private const string CorrectionMessage = "Nein, ich meinte den Vertrag von Anna Meier, nicht die Adresse";
    private const string WriteSkill = "create_employee";
    private const string ConversationKey = "conv-anchor";

    private static readonly Guid UserId = Guid.NewGuid();

    private IAssistantLastActionStore _store = null!;
    private AssistantLastAction? _saved;
    private TurnPreparationService _preparation = null!;
    private LLMContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _saved = null;
        _store = Substitute.For<IAssistantLastActionStore>();
        _store.When(store => store.Save(Arg.Any<AssistantLastAction>()))
            .Do(call => _saved = call.Arg<AssistantLastAction>());

        _preparation = new TurnPreparationService(
            Substitute.For<IPendingConfirmationStore>(),
            null!,
            Substitute.For<IRecipeRunRecorder>(),
            null!,
            _store,
            Substitute.For<IDeterministicRouteProbe>(),
            Substitute.For<ISkillInverseResolver>(),
            Substitute.For<ILogger<TurnPreparationService>>());

        _context = new LLMContext
        {
            Message = "Lege den Mitarbeiter Anna Meier an",
            UserId = UserId.ToString(),
            Language = "de",
            AvailableFunctions = [new LLMFunction { Name = WriteSkill }]
        };
    }

    [Test]
    public void TheAnchorOfAStoppedTurnThatRanAWrite_LetsACorrectionPassGateG0()
    {
        var stoppedAnswer = StoppedTurnSummary.StoredAnswer("Ich lege den Mitarbeiter");
        var ran = new LLMFunctionCall { FunctionName = WriteSkill, Success = true, Result = "Employee created." };

        _preparation.RecordLastAction(_context, ConversationKey, stoppedAnswer, StoppedTurnSummary.ExecutedCalls([ran]), false);

        _saved.ShouldNotBeNull();
        _saved!.Calls.Select(call => call.SkillName).ShouldBe([WriteSkill]);
        _saved.AssistantAnswerExcerpt.ShouldEndWith(TurnInterruptionDefaults.InterruptedMarker);
        GracefulCorrectionDetector.Evaluate(CorrectionMessage, _saved, recipeIsActive: false, correctionRoutesAlone: false, DateTime.UtcNow)
            .ShouldNotBe(GracefulCorrectionGate.Anchor);
        GracefulCorrectionDetector.Evaluate(CorrectionMessage, _saved, recipeIsActive: false, correctionRoutesAlone: false, DateTime.UtcNow)
            .ShouldBe(GracefulCorrectionGate.Passed);
    }

    [Test]
    public void AStoppedTurnThatRanNothing_LeavesNoAnchorAndSupersedesTheOldOne()
    {
        var skipped = new LLMFunctionCall { FunctionName = WriteSkill, SkippedByStop = true, Success = false };

        _preparation.RecordLastAction(
            _context, ConversationKey, StoppedTurnSummary.StoredAnswer(string.Empty),
            StoppedTurnSummary.ExecutedCalls([skipped]), false);

        _saved.ShouldBeNull();
        _store.Received(1).MarkSuperseded(UserId, ConversationKey);
    }

    [Test]
    public void AStoppedTurnWhoseRecipeWaits_SupersedesInsteadOfAnchoring()
    {
        var ran = new LLMFunctionCall { FunctionName = WriteSkill, Success = true, Result = "Done." };

        _preparation.RecordLastAction(
            _context, ConversationKey, StoppedTurnSummary.StoredAnswer("Which group?"),
            StoppedTurnSummary.ExecutedCalls([ran]), recipePaused: true);

        _saved.ShouldBeNull();
        _store.Received(1).MarkSuperseded(UserId, ConversationKey);
    }
}
