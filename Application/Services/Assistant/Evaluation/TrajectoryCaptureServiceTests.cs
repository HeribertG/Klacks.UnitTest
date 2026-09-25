// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for TrajectoryCaptureService — verifies that HadMutationIntent is derived from
/// the user message via MutationIntentDetector, so tool-call-free turns can later be split into
/// "legitimate info question" vs. "should have acted" for measuring skill-routing quality; and
/// that a same-user follow-up containing a negation/complaint marks the previous trajectory as
/// implicitly corrected only within the short reactive time window, and hands that preceding turn to
/// the learning collector under the cluster key of its own utterance.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant.Evaluation;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation;

[TestFixture]
public class TrajectoryCaptureServiceTests
{
    private ISkillSelectionTrajectoryRepository _repository = null!;
    private ISkillLearningCaseCollector _caseCollector = null!;
    private ISkillPhraseRepository _phrases = null!;
    private ISkillUsageRepository _usage = null!;
    private ILLMRepository _llm = null!;
    private TrajectoryCaptureService _service = null!;
    private Guid _agentId;

    private const string PluginNegationToken = "nie";

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ISkillSelectionTrajectoryRepository>();
        _caseCollector = Substitute.For<ISkillLearningCaseCollector>();
        _phrases = Substitute.For<ISkillPhraseRepository>();
        _usage = Substitute.For<ISkillUsageRepository>();
        _llm = Substitute.For<ILLMRepository>();
        _phrases
            .GetActiveBySourceAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _usage
            .GetByTurnIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _service = new TrajectoryCaptureService(
            _repository, _caseCollector, _phrases, _usage, _llm, Substitute.For<ILogger<TrajectoryCaptureService>>());
        _agentId = Guid.NewGuid();
    }

    // Both detectors, not only the one this fixture configures: TrajectoryCaptureService consults
    // AffirmationDetector as well, so entries left behind here would decide a later fixture's outcome.
    [TearDown]
    public void ResetPluginEntries()
    {
        DeclineDetector.Reset();
        AffirmationDetector.Reset();
    }

    [Test]
    public async Task MutationMessageWithoutToolCall_IsFlaggedAsHadMutationIntent()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));

        var context = new LLMContext { Message = "Erstelle einen neuen Kunden namens Muster AG", UserId = "user-1" };

        await _service.CaptureAsync(_agentId, context, "Ich habe das erledigt.", []);

        captured.ShouldNotBeNull();
        captured!.WasExecuted.ShouldBeFalse();
        captured.HadMutationIntent.ShouldBeTrue();
    }

    // The only link between a turn and a composed capability, and therefore the denominator of that
    // capability's usefulness quote. It is read off the context because the recipe plan is a local of the
    // chat loop that would otherwise never leave it.
    [Test]
    public async Task TheActiveRecipe_IsRecordedOnTheTrajectory()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));

        var context = new LLMContext
        {
            Message = "Melde die offenen Dienste",
            UserId = "user-1",
            ActiveRecipeName = "learned-open-shift-report"
        };

        await _service.CaptureAsync(_agentId, context, "Erledigt.", []);

        captured!.RecipeName.ShouldBe("learned-open-shift-report");
    }

    [Test]
    public async Task WithoutAnActiveRecipe_TheTrajectoryRecordsNone()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));

        await _service.CaptureAsync(
            _agentId, new LLMContext { Message = "Zeig mir die Kunden", UserId = "user-1" }, "Bitte.", []);

        captured!.RecipeName.ShouldBeNull();
    }

    // Attribution for the fitness quote of a learned phrase, matched on the normalised text so that
    // casing and spacing in the utterance do not decide whether a phrase counts as used. Recorded now
    // rather than derived later, so a phrase learned next week cannot claim credit for today's turn.
    [Test]
    public async Task AnUtteranceContainingALearnedWording_IsAttributedToTheOwningSkill()
    {
        GivenLearnedPhrase("list_open_shifts", "offene dienste");

        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));

        await _service.CaptureAsync(
            _agentId,
            new LLMContext { Message = "Zeig mir die   OFFENE   Dienste von morgen", UserId = "user-1" },
            "Hier sind sie.",
            []);

        captured!.LearnedPhraseHit.ShouldBe("list_open_shifts");
    }

    [Test]
    public async Task AnUtteranceWithoutAnyLearnedWording_CarriesNoAttribution()
    {
        GivenLearnedPhrase("list_open_shifts", "offene dienste");

        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));

        await _service.CaptureAsync(
            _agentId,
            new LLMContext { Message = "Wie viele Kunden haben wir?", UserId = "user-1" },
            "Achtzehn.",
            []);

        captured!.LearnedPhraseHit.ShouldBeNull();
    }

    private void GivenLearnedPhrase(string ownerName, string phrase) =>
        _phrases
            .GetActiveBySourceAsync(
                SkillPhraseSources.Learned, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new SkillPhrase { OwnerName = ownerName, Phrase = phrase }]);

    // W1.1: the turn id travels from the chat context onto the trajectory, where it forms the join
    // key to llm_usages.id and skill_usage_records.turn_id.
    [Test]
    public async Task TheTurnId_IsRecordedOnTheTrajectory()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));
        var turnId = Guid.NewGuid();

        await _service.CaptureAsync(
            _agentId,
            new LLMContext { Message = "Zeig mir die Kunden", UserId = "user-1", TurnId = turnId },
            "Bitte.",
            []);

        captured!.TurnId.ShouldBe(turnId);
    }

    // W1.3: was_successful is derived at capture time from the turn's skill_usage_records rows —
    // all successes make the turn successful, one failure spoils it, no rows leave it unknown.
    [Test]
    public async Task AllUsageRowsSuccessful_MarksTheTurnSuccessful()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));
        var turnId = Guid.NewGuid();
        _usage.GetByTurnIdAsync(turnId, Arg.Any<CancellationToken>())
            .Returns([Usage(turnId, true), Usage(turnId, true)]);

        await _service.CaptureAsync(
            _agentId,
            new LLMContext { Message = "Öffne die Dienste", UserId = "user-1", TurnId = turnId },
            "Erledigt.",
            [new LLMFunctionCall { FunctionName = "list_open_shifts" }]);

        captured!.WasSuccessful.ShouldBe(true);
    }

    [Test]
    public async Task OneFailedUsageRow_MarksTheTurnFailed()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));
        var turnId = Guid.NewGuid();
        _usage.GetByTurnIdAsync(turnId, Arg.Any<CancellationToken>())
            .Returns([Usage(turnId, true), Usage(turnId, false)]);

        await _service.CaptureAsync(
            _agentId,
            new LLMContext { Message = "Öffne die Dienste", UserId = "user-1", TurnId = turnId },
            "Erledigt.",
            [new LLMFunctionCall { FunctionName = "list_open_shifts" }]);

        captured!.WasSuccessful.ShouldBe(false);
    }

    [Test]
    public async Task WithoutUsageRows_WasSuccessfulStaysUnknown()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));

        await _service.CaptureAsync(
            _agentId,
            new LLMContext { Message = "Zeig mir die Kunden", UserId = "user-1", TurnId = Guid.NewGuid() },
            "Bitte.",
            []);

        captured!.WasSuccessful.ShouldBeNull();
    }

    // W1.4: a dispatched UiAction is not a verdict yet, so a turn consisting only of pending UI
    // actions stays unknown instead of being booked as a false success.
    [Test]
    public async Task OnlyDispatchedUiActions_WasSuccessfulStaysUnknown()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));
        var turnId = Guid.NewGuid();
        _usage.GetByTurnIdAsync(turnId, Arg.Any<CancellationToken>())
            .Returns([Usage(turnId, true, UiActionStatus.Dispatched)]);

        await _service.CaptureAsync(
            _agentId,
            new LLMContext { Message = "Öffne die Einstellungen", UserId = "user-1", TurnId = turnId },
            "Wird ausgeführt.",
            [new LLMFunctionCall { FunctionName = "open_settings" }]);

        captured!.WasSuccessful.ShouldBeNull();
    }

    [Test]
    public async Task AStoppedTurn_IsRecordedWithItsPhaseAndTheExecutedCallsOnly()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));
        var context = new LLMContext { Message = "Lege den Mitarbeiter Anna Meier an", UserId = "user-1", TurnId = Guid.NewGuid() };

        await _service.CaptureAsync(
            _agentId, context, "Teilantwort [interrupted by user]",
            [new LLMFunctionCall { FunctionName = "create_employee" }],
            InterruptedTurnPhases.DuringTools);

        captured!.WasInterrupted.ShouldBeTrue();
        captured.InterruptedPhase.ShouldBe(InterruptedTurnPhases.DuringTools);
        captured.WasExecuted.ShouldBeTrue();
        captured.LlmChosenSkill.ShouldBe("create_employee");
    }

    [Test]
    public async Task ATurnThatRanToItsEnd_IsNotMarkedInterrupted()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));

        await _service.CaptureAsync(
            _agentId, new LLMContext { Message = "Zeig mir die Kunden", UserId = "user-1" }, "Bitte.", []);

        captured!.WasInterrupted.ShouldBeFalse();
        captured.InterruptedPhase.ShouldBeNull();
    }

    [Test]
    public async Task AStoppedTurn_HasNoVerdictAndNoRecipeGateEvenWhenItsRowsSucceeded()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));
        var turnId = Guid.NewGuid();
        _usage.GetByTurnIdAsync(turnId, Arg.Any<CancellationToken>()).Returns([Usage(turnId, true)]);
        var context = new LLMContext
        {
            Message = "Melde die offenen Dienste",
            UserId = "user-1",
            TurnId = turnId,
            ActiveRecipeName = "open-shift-report",
            RecipeAwaitingConfirmation = true
        };

        await _service.CaptureAsync(
            _agentId, context, "Teil.", [new LLMFunctionCall { FunctionName = "list_open_shifts" }],
            InterruptedTurnPhases.DuringText);

        captured!.WasSuccessful.ShouldBeNull();
        captured.RecipeOutcome.ShouldBeNull();
    }

    // What a stopped turn's message says about the answer BEFORE it is no evidence: the stopped turn never ran to
    // its end, so it must not mark the previous turn corrected or resolve its recipe gate.
    [Test]
    public async Task AStoppedTurn_ResolvesNothingAboutThePreviousTurn()
    {
        var previous = PreviousTurn(createdSecondsAgo: 30, skill: "list_clients");
        _repository.FindMostRecentByAgentAndUserAsync(_agentId, "user-1").Returns(previous);

        await _service.CaptureAsync(
            _agentId, new LLMContext { Message = "Nein, das war nicht richtig", UserId = "user-1" }, "Teil.", [],
            InterruptedTurnPhases.BeforeText);

        previous.WasCorrected.ShouldBeFalse();
        await _repository.DidNotReceive().FindMostRecentByAgentAndUserAsync(Arg.Any<Guid>(), Arg.Any<string>());
        await _repository.DidNotReceive().UpdateAsync(previous);
        await _caseCollector.DidNotReceiveWithAnyArgs().CollectImplicitCorrectionAsync(default!, default);
    }

    // The one exception (F1): the stop plus a correction the graceful-correction path really re-routed.
    [Test]
    public async Task ARoutedCorrectionAfterAStoppedTurn_MarksItGracefulReroutedAndCollectsTheCase()
    {
        var previous = PreviousTurn(createdSecondsAgo: 30, skill: "create_employee", interrupted: true);
        _repository.FindMostRecentByAgentAndUserAsync(_agentId, "user-1").Returns(previous);

        await _service.CaptureAsync(
            _agentId,
            new LLMContext { Message = "Nein, ich meinte den Vertrag", UserId = "user-1", GracefulCorrectionApplied = true },
            "Verstanden.", []);

        previous.WasCorrected.ShouldBeTrue();
        previous.CorrectionType.ShouldBe(CorrectionTypes.GracefulRerouted);
        await _repository.Received(1).UpdateAsync(previous);
        await _caseCollector.Received(1).CollectImplicitCorrectionAsync(
            Arg.Is<SkillLearningImplicitCorrection>(c => c.TrajectoryId == previous.Id), Arg.Any<CancellationToken>());
    }

    // The user already judged the stopped turn through the correction menu, which taught it: the routed
    // correction that follows must not book and collect the same turn a second time.
    [Test]
    public async Task ARoutedCorrectionAfterAStoppedTurnTheMenuAlreadyCorrected_MarksAndCollectsNothing()
    {
        var previous = PreviousTurn(createdSecondsAgo: 30, skill: "create_employee", interrupted: true);
        previous.WasCorrected = true;
        previous.CorrectionType = CorrectionTypes.WrongSkill;
        _repository.FindMostRecentByAgentAndUserAsync(_agentId, "user-1").Returns(previous);

        await _service.CaptureAsync(
            _agentId,
            new LLMContext { Message = "Nein, ich meinte den Vertrag", UserId = "user-1", GracefulCorrectionApplied = true },
            "Verstanden.", []);

        previous.CorrectionType.ShouldBe(CorrectionTypes.WrongSkill);
        await _repository.DidNotReceive().UpdateAsync(previous);
        await _caseCollector.DidNotReceiveWithAnyArgs().CollectImplicitCorrectionAsync(default!, default);
    }

    [Test]
    public async Task ACorrectionThatWasNotRoutedAfterAStoppedTurn_MarksNothing()
    {
        var previous = PreviousTurn(createdSecondsAgo: 30, skill: "create_employee", interrupted: true);
        _repository.FindMostRecentByAgentAndUserAsync(_agentId, "user-1").Returns(previous);

        await _service.CaptureAsync(
            _agentId, new LLMContext { Message = "Nein, das war nicht richtig", UserId = "user-1" }, "Ok.", []);

        previous.WasCorrected.ShouldBeFalse();
        await _repository.DidNotReceive().UpdateAsync(previous);
        await _caseCollector.DidNotReceiveWithAnyArgs().CollectImplicitCorrectionAsync(default!, default);
    }

    [Test]
    public async Task ARoutedCorrectionOutsideTheWindowAfterAStoppedTurn_MarksNothing()
    {
        var previous = PreviousTurn(createdSecondsAgo: 600, skill: "create_employee", interrupted: true);
        _repository.FindMostRecentByAgentAndUserAsync(_agentId, "user-1").Returns(previous);

        await _service.CaptureAsync(
            _agentId,
            new LLMContext { Message = "Nein, ich meinte den Vertrag", UserId = "user-1", GracefulCorrectionApplied = true },
            "Verstanden.", []);

        previous.WasCorrected.ShouldBeFalse();
    }

    [Test]
    public async Task AConfirmedRecipeGateAfterAStoppedTurn_DoesNotResolveTheStoppedTurn()
    {
        var previous = PreviousTurn(createdSecondsAgo: 30, skill: null, interrupted: true);
        previous.RecipeOutcome = RecipeOutcomes.Pending;
        previous.RecipeName = "open-shift-report";
        _repository.FindMostRecentByAgentAndUserAsync(_agentId, "user-1").Returns(previous);

        await _service.CaptureAsync(
            _agentId,
            new LLMContext
            {
                Message = "ja",
                UserId = "user-1",
                ActiveRecipeName = "open-shift-report",
                RecipeConfirmationAccepted = true
            },
            "Ok.", []);

        previous.RecipeOutcome.ShouldBe(RecipeOutcomes.Pending);
        await _repository.DidNotReceive().UpdateAsync(previous);
    }

    private SkillSelectionTrajectory PreviousTurn(int createdSecondsAgo, string? skill, bool interrupted = false) => new()
    {
        Id = Guid.NewGuid(),
        AgentId = _agentId,
        UserId = "user-1",
        Locale = "de",
        UserMessageHash = "abc123def4567890",
        LlmChosenSkill = skill,
        WasCorrected = false,
        WasInterrupted = interrupted,
        InterruptedPhase = interrupted ? InterruptedTurnPhases.DuringTools : null,
        CreateTime = DateTime.UtcNow.AddSeconds(-createdSecondsAgo)
    };

    [Test]
    public async Task OnlyCancelledUiActions_WasSuccessfulStaysUnknown()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));
        var turnId = Guid.NewGuid();
        _usage.GetByTurnIdAsync(turnId, Arg.Any<CancellationToken>())
            .Returns([Usage(turnId, false, UiActionStatus.Cancelled)]);

        await _service.CaptureAsync(
            _agentId,
            new LLMContext { Message = "Öffne die Einstellungen", UserId = "user-1", TurnId = turnId },
            "Wird ausgeführt.",
            [new LLMFunctionCall { FunctionName = "open_settings" }]);

        captured!.WasSuccessful.ShouldBeNull();
    }

    [Test]
    public async Task ACancelledSkillRowIsNoVerdict_TheOtherRowsDecideTheTurn()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));
        var turnId = Guid.NewGuid();
        var cancelled = Usage(turnId, false);
        cancelled.FailureKind = SkillFailureKind.Cancelled;
        _usage.GetByTurnIdAsync(turnId, Arg.Any<CancellationToken>()).Returns([Usage(turnId, true), cancelled]);

        await _service.CaptureAsync(
            _agentId,
            new LLMContext { Message = "Zeig mir die Kunden", UserId = "user-1", TurnId = turnId },
            "Hier.",
            [new LLMFunctionCall { FunctionName = "list_open_shifts" }]);

        captured!.WasSuccessful.ShouldBe(true);
    }

    [Test]
    public async Task DispatchedUiActionPlusCompletedSuccess_MarksTheTurnSuccessful()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));
        var turnId = Guid.NewGuid();
        _usage.GetByTurnIdAsync(turnId, Arg.Any<CancellationToken>())
            .Returns([Usage(turnId, true, UiActionStatus.Completed)]);

        await _service.CaptureAsync(
            _agentId,
            new LLMContext { Message = "Öffne die Einstellungen", UserId = "user-1", TurnId = turnId },
            "Erledigt.",
            [new LLMFunctionCall { FunctionName = "open_settings" }]);

        captured!.WasSuccessful.ShouldBe(true);
    }

    private static SkillUsageRecord Usage(Guid turnId, bool success, UiActionStatus? uiActionStatus = null) => new()
    {
        SkillName = "list_open_shifts",
        Category = SkillCategory.Query,
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        Success = success,
        TurnId = turnId,
        UiActionStatus = uiActionStatus
    };

    [Test]
    public async Task InfoQuestionWithoutToolCall_IsNotFlaggedAsHadMutationIntent()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));

        var context = new LLMContext { Message = "Wie erstelle ich einen neuen Kunden?", UserId = "user-1" };

        await _service.CaptureAsync(_agentId, context, "Das geht über die Kunden-Seite.", []);

        captured.ShouldNotBeNull();
        captured!.WasExecuted.ShouldBeFalse();
        captured.HadMutationIntent.ShouldBeFalse();
    }

    [Test]
    public async Task MutationMessageWithToolCall_IsExecutedAndFlagged()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));

        var context = new LLMContext { Message = "Lösche den Kunden Muster AG", UserId = "user-1" };
        var call = new LLMFunctionCall { FunctionName = "delete_client" };

        await _service.CaptureAsync(_agentId, context, "Erledigt.", [call]);

        captured.ShouldNotBeNull();
        captured!.WasExecuted.ShouldBeTrue();
        captured.HadMutationIntent.ShouldBeTrue();
        captured.LlmChosenSkill.ShouldBe("delete_client");
    }

    [Test]
    public async Task NegationFollowUp_WithinWindow_MarksPreviousTrajectoryAsImplicitlyCorrected()
    {
        var previous = new SkillSelectionTrajectory
        {
            Id = Guid.NewGuid(),
            AgentId = _agentId,
            UserId = "user-1",
            LlmChosenSkill = "list_clients",
            WasCorrected = false,
            CreateTime = DateTime.UtcNow.AddSeconds(-30),
        };
        _repository.FindMostRecentByAgentAndUserAsync(_agentId, "user-1").Returns(previous);

        var context = new LLMContext { Message = "Nein, das war nicht richtig", UserId = "user-1" };

        await _service.CaptureAsync(_agentId, context, "Entschuldigung, hier ist die Korrektur.", []);

        previous.WasCorrected.ShouldBeTrue();
        previous.CorrectionType.ShouldBe(CorrectionTypes.Implicit);
        await _repository.Received(1).UpdateAsync(previous);
    }

    // The correction belongs to the preceding utterance, so the case must land on that utterance's
    // cluster - the stored hash, never a hash of the negation that revealed it.
    [Test]
    public async Task NegationFollowUp_WithinWindow_CollectsAnImplicitCaseForThePrecedingUtterance()
    {
        var previous = new SkillSelectionTrajectory
        {
            Id = Guid.NewGuid(),
            AgentId = _agentId,
            UserId = "user-1",
            Locale = "de",
            UserMessageHash = "abc123def4567890",
            IntentExcerpt = "Zeige mir die Umsatzstatistik pro Kunde",
            KnowledgeIndexCandidatesJson = "[{\"name\":\"list_clients\"}]",
            LlmChosenSkill = "list_clients",
            WasCorrected = false,
            CreateTime = DateTime.UtcNow.AddSeconds(-30),
        };
        _repository.FindMostRecentByAgentAndUserAsync(_agentId, "user-1").Returns(previous);

        var context = new LLMContext { Message = "Nein, das war nicht richtig", UserId = "user-1" };

        await _service.CaptureAsync(_agentId, context, "Entschuldigung.", []);

        await _caseCollector.Received(1).CollectImplicitCorrectionAsync(
            Arg.Is<SkillLearningImplicitCorrection>(c =>
                c.AgentId == _agentId
                && c.ClusterKey == previous.UserMessageHash
                && c.IntentExcerpt == previous.IntentExcerpt
                && c.UserId == "user-1"
                && c.Locale == "de"
                && c.ChosenSkill == "list_clients"
                && c.ToolsetJson == previous.KnowledgeIndexCandidatesJson
                && c.TrajectoryId == previous.Id),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NegationFollowUp_OutsideWindow_DoesNotMarkPreviousTrajectory()
    {
        var previous = new SkillSelectionTrajectory
        {
            Id = Guid.NewGuid(),
            AgentId = _agentId,
            UserId = "user-1",
            WasCorrected = false,
            CreateTime = DateTime.UtcNow.AddMinutes(-5),
        };
        _repository.FindMostRecentByAgentAndUserAsync(_agentId, "user-1").Returns(previous);

        var context = new LLMContext { Message = "Nein, das war falsch", UserId = "user-1" };

        await _service.CaptureAsync(_agentId, context, "Ok.", []);

        previous.WasCorrected.ShouldBeFalse();
        await _repository.DidNotReceive().UpdateAsync(previous);
        await _caseCollector.DidNotReceiveWithAnyArgs().CollectImplicitCorrectionAsync(default!, default);
    }

    [Test]
    public async Task NegationFollowUp_PreviousAlreadyCorrected_IsNotUpdatedAgain()
    {
        var previous = new SkillSelectionTrajectory
        {
            Id = Guid.NewGuid(),
            AgentId = _agentId,
            UserId = "user-1",
            WasCorrected = true,
            CorrectionType = CorrectionTypes.WrongSkill,
            CreateTime = DateTime.UtcNow.AddSeconds(-10),
        };
        _repository.FindMostRecentByAgentAndUserAsync(_agentId, "user-1").Returns(previous);

        var context = new LLMContext { Message = "Nein, das stimmt nicht", UserId = "user-1" };

        await _service.CaptureAsync(_agentId, context, "Ok.", []);

        await _repository.DidNotReceive().UpdateAsync(previous);
        previous.CorrectionType.ShouldBe(CorrectionTypes.WrongSkill);
        await _caseCollector.DidNotReceiveWithAnyArgs().CollectImplicitCorrectionAsync(default!, default);
    }

    [Test]
    public async Task MessageWithoutNegation_NeverLooksUpPreviousTrajectory()
    {
        var context = new LLMContext { Message = "Zeig mir bitte die offenen Schichten", UserId = "user-1" };

        await _service.CaptureAsync(_agentId, context, "Hier sind die offenen Schichten.", []);

        await _repository.DidNotReceiveWithAnyArgs().FindMostRecentByAgentAndUserAsync(default, default!);
        await _caseCollector.DidNotReceiveWithAnyArgs().CollectImplicitCorrectionAsync(default!, default);
    }

    // W1.6: the candidates JSON records name, provenance, 1-based rank in the offered list and the
    // retrieval score where one exists, so "which source won" is a SQL query over the jsonb column.
    [Test]
    public async Task CandidatesJson_RecordsNameSourceRankAndScore()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));

        var context = new LLMContext
        {
            Message = "Zeig mir die offenen dienste",
            UserId = "user-1",
            AvailableFunctions =
            [
                new LLMFunction { Name = "navigate_to", ToolsetSource = ToolsetSkillSource.AlwaysOn },
                new LLMFunction
                {
                    Name = "list_open_shifts",
                    ToolsetSource = ToolsetSkillSource.Retrieved,
                    RetrievalScore = 0.87
                }
            ]
        };

        await _service.CaptureAsync(_agentId, context, "Erledigt.", []);

        using var document = JsonDocument.Parse(captured!.KnowledgeIndexCandidatesJson);
        var candidates = document.RootElement.EnumerateArray().ToList();
        candidates.Count.ShouldBe(2);

        candidates[0].GetProperty("name").GetString().ShouldBe("navigate_to");
        candidates[0].GetProperty("source").GetString().ShouldBe("AlwaysOn");
        candidates[0].GetProperty("rank").GetInt32().ShouldBe(1);
        candidates[0].GetProperty("score").ValueKind.ShouldBe(JsonValueKind.Null);

        candidates[1].GetProperty("name").GetString().ShouldBe("list_open_shifts");
        candidates[1].GetProperty("source").GetString().ShouldBe("Retrieved");
        candidates[1].GetProperty("rank").GetInt32().ShouldBe(2);
        candidates[1].GetProperty("score").GetDouble().ShouldBe(0.87);
    }

    [Test]
    public async Task WithoutAvailableFunctions_CandidatesJsonStaysAnEmptyArray()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));

        await _service.CaptureAsync(
            _agentId, new LLMContext { Message = "Zeig mir die Kunden", UserId = "user-1" }, "Bitte.", []);

        captured!.KnowledgeIndexCandidatesJson.ShouldBe("[]");
    }

    // W1.7: the trajectory latencies are copied from the turn's llm_usage row (written before the
    // background capture starts) instead of staying hard-coded zero.
    [Test]
    public async Task LlmUsageRow_FillsTrajectoryLatencies()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));
        var turnId = Guid.NewGuid();
        _llm.GetUsageByIdAsync(turnId, Arg.Any<CancellationToken>()).Returns(new Klacks.Api.Domain.Models.Assistant.LLMUsage
        {
            ResponseTimeMs = 1200,
            ToolsetAssemblyMs = 150,
            TtftMs = 800
        });

        await _service.CaptureAsync(
            _agentId,
            new LLMContext { Message = "Zeig mir die Kunden", UserId = "user-1", TurnId = turnId },
            "Bitte.",
            []);

        captured!.LatencyMsTotal.ShouldBe(1350);
        captured.LatencyMsKnowledge.ShouldBe(150);
        captured.LatencyMsLlm.ShouldBe(800);
    }

    // Without a TTFT the fallback is the full ResponseTimeMs, NOT ResponseTimeMs minus the assembly:
    // the assembly runs before the response stopwatch starts, so the two intervals are disjoint and
    // subtracting one from the other under-reported the model latency by the assembly duration.
    [Test]
    public async Task LlmUsageWithoutTtft_FallsBackToFullResponseTimeForLlmLatency()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));
        var turnId = Guid.NewGuid();
        _llm.GetUsageByIdAsync(turnId, Arg.Any<CancellationToken>()).Returns(new Klacks.Api.Domain.Models.Assistant.LLMUsage
        {
            ResponseTimeMs = 1200,
            ToolsetAssemblyMs = 150,
            TtftMs = null
        });

        await _service.CaptureAsync(
            _agentId,
            new LLMContext { Message = "Zeig mir die Kunden", UserId = "user-1", TurnId = turnId },
            "Bitte.",
            []);

        captured!.LatencyMsTotal.ShouldBe(1350);
        captured.LatencyMsKnowledge.ShouldBe(150);
        captured.LatencyMsLlm.ShouldBe(1200);
    }

    // With no response time to add, the total collapses to the assembly time - never below it, which
    // is what the old "total = ResponseTimeMs" rule produced (total 0 next to a knowledge part of 95).
    [Test]
    public async Task WithoutLlmUsageRow_KnowledgeLatencyFallsBackToContextValue()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));

        await _service.CaptureAsync(
            _agentId,
            new LLMContext { Message = "Zeig mir die Kunden", UserId = "user-1", ToolsetAssemblyMs = 95 },
            "Bitte.",
            []);

        captured!.LatencyMsKnowledge.ShouldBe(95);
        captured.LatencyMsTotal.ShouldBe(95);
        captured.LatencyMsLlm.ShouldBe(0);
    }

    [Test]
    public async Task AConfirmationPromptTurn_IsCapturedAsAPendingRecipeOutcome()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));

        var context = new LLMContext
        {
            Message = "Wie fange ich an?",
            UserId = "user-1",
            ActiveRecipeName = "setup-consultation",
            RecipeAwaitingConfirmation = true
        };

        await _service.CaptureAsync(_agentId, context, "Soll ich die Einrichtung starten?", []);

        captured!.RecipeOutcome.ShouldBe(RecipeOutcomes.Pending);
    }

    [Test]
    public async Task AnOrdinaryTurn_CarriesNoRecipeOutcome()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));

        await _service.CaptureAsync(
            _agentId, new LLMContext { Message = "Zeig mir die Kunden", UserId = "user-1" }, "Bitte.", []);

        captured!.RecipeOutcome.ShouldBeNull();
    }

    [Test]
    public async Task NegationFollowUp_AfterATurnThatCalledNothing_IsNoCorrection()
    {
        var previous = new SkillSelectionTrajectory
        {
            Id = Guid.NewGuid(),
            AgentId = _agentId,
            UserId = "user-1",
            LlmChosenSkill = null,
            WasCorrected = false,
            CreateTime = DateTime.UtcNow.AddSeconds(-30),
        };
        _repository.FindMostRecentByAgentAndUserAsync(_agentId, "user-1").Returns(previous);

        await _service.CaptureAsync(
            _agentId, new LLMContext { Message = "Nein, das war falsch", UserId = "user-1" }, "Ok.", []);

        previous.WasCorrected.ShouldBeFalse();
        await _repository.DidNotReceive().UpdateAsync(previous);
        await _caseCollector.DidNotReceiveWithAnyArgs().CollectImplicitCorrectionAsync(default!, default);
    }

    [Test]
    public async Task BareNegationAfterAConfirmationPrompt_IsRecordedAsARecipeDecline()
    {
        var previous = GivenPendingConfirmation();

        await _service.CaptureAsync(
            _agentId, new LLMContext { Message = "Nein", UserId = "user-1" }, "Alles klar.", []);

        previous.WasCorrected.ShouldBeFalse();
        previous.CorrectionType.ShouldBe(CorrectionTypes.None);
        previous.RecipeOutcome.ShouldBe(RecipeOutcomes.Declined);
        await _repository.Received(1).UpdateAsync(previous);
        await _caseCollector.Received(1).CollectRecipeDeclineAsync(
            Arg.Is<SkillLearningRecipeDecline>(d =>
                d.AgentId == _agentId
                && d.ClusterKey == previous.UserMessageHash
                && d.IntentExcerpt == previous.IntentExcerpt
                && d.UserId == "user-1"
                && d.Locale == "de"
                && d.RecipeName == "setup-consultation"
                && d.ToolsetJson == previous.KnowledgeIndexCandidatesJson
                && d.TrajectoryId == previous.Id),
            Arg.Any<CancellationToken>());
        await _caseCollector.DidNotReceiveWithAnyArgs().CollectImplicitCorrectionAsync(default!, default);
    }

    [Test]
    public async Task ResumingTheSameRecipe_ResolvesThePendingGateAsConfirmed()
    {
        var previous = GivenPendingConfirmation();

        await _service.CaptureAsync(
            _agentId,
            new LLMContext
            {
                Message = "Ja, bitte",
                UserId = "user-1",
                ActiveRecipeName = "setup-consultation"
            },
            "Ich starte die Einrichtung.",
            []);

        previous.RecipeOutcome.ShouldBe(RecipeOutcomes.Confirmed);
        await _repository.Received(1).UpdateAsync(previous);
        await _caseCollector.DidNotReceiveWithAnyArgs().CollectRecipeDeclineAsync(default!, default);
    }

    // A reply that redirects the conversation instead of refusing the recipe is its own outcome, not a
    // decline: the engine abandons the run either way, but this turn says nothing about whether the
    // trigger was right, so it must leave RecipeDeclineClusterPolicy's input untouched. It is not a
    // correction either - the user corrected the assistant's question, not its skill choice. Before the
    // redirected outcome existed the gate simply stayed pending for good.
    [Test]
    public async Task ANegationCorrectingCourseAfterAConfirmationPrompt_IsNeitherDeclineNorCorrection()
    {
        var previous = GivenPendingConfirmation();

        await _service.CaptureAsync(
            _agentId,
            new LLMContext
            {
                Message = "Nein, zeig mir stattdessen die Kunden",
                UserId = "user-1",
                RecipeConfirmationDeclined = true
            },
            "Hier sind die Kunden.",
            []);

        previous.RecipeOutcome.ShouldBe(RecipeOutcomes.Redirected);
        previous.WasCorrected.ShouldBeFalse();
        await _repository.Received(1).UpdateAsync(previous);
        await _caseCollector.DidNotReceiveWithAnyArgs().CollectRecipeDeclineAsync(default!, default);
        await _caseCollector.DidNotReceiveWithAnyArgs().CollectImplicitCorrectionAsync(default!, default);
    }

    // A bare refusal reaching the same branch keeps the outcome AND the learning signal it always had.
    // The engine's flag only decides that the gate is over, never which verdict it carries.
    [Test]
    public async Task AnAbandonedGateAnsweredByABareNegation_IsStillRecordedAsARecipeDecline()
    {
        var previous = GivenPendingConfirmation();

        await _service.CaptureAsync(
            _agentId,
            new LLMContext { Message = "Nein", UserId = "user-1", RecipeConfirmationDeclined = true },
            "Alles klar.",
            []);

        previous.RecipeOutcome.ShouldBe(RecipeOutcomes.Declined);
        await _repository.Received(1).UpdateAsync(previous);
        await _caseCollector.Received(1).CollectRecipeDeclineAsync(
            Arg.Is<SkillLearningRecipeDecline>(d =>
                d.AgentId == _agentId
                && d.ClusterKey == previous.UserMessageHash
                && d.RecipeName == "setup-consultation"
                && d.TrajectoryId == previous.Id),
            Arg.Any<CancellationToken>());
    }

    // Same reason the confirmed gate resolves before the window: the flag is the engine's own record, not
    // an attribution, and the pending recipe outlives the correction window by far.
    [Test]
    public async Task AnAbandonedGate_IsResolvedOutsideTheCorrectionWindow()
    {
        var previous = GivenPendingConfirmation();
        previous.CreateTime = DateTime.UtcNow.AddMinutes(-5);

        await _service.CaptureAsync(
            _agentId,
            new LLMContext
            {
                Message = "Nein, zeig mir stattdessen die Kunden",
                UserId = "user-1",
                RecipeConfirmationDeclined = true
            },
            "Hier sind die Kunden.",
            []);

        previous.RecipeOutcome.ShouldBe(RecipeOutcomes.Redirected);
        await _repository.Received(1).UpdateAsync(previous);
    }

    // The flag resolves a GATE, nothing else. A preceding turn that never asked a confirmation question
    // carries no pending outcome, and overwriting it would invent a gate that never existed.
    [Test]
    public async Task AnAbandonedGate_LeavesATurnWithoutAPendingOutcomeUntouched()
    {
        var previous = new SkillSelectionTrajectory
        {
            Id = Guid.NewGuid(),
            AgentId = _agentId,
            UserId = "user-1",
            LlmChosenSkill = "list_clients",
            WasCorrected = false,
            RecipeOutcome = null,
            CreateTime = DateTime.UtcNow.AddSeconds(-30),
        };
        _repository.FindMostRecentByAgentAndUserAsync(_agentId, "user-1").Returns(previous);

        await _service.CaptureAsync(
            _agentId,
            new LLMContext
            {
                Message = "Zeig mir stattdessen die Kunden",
                UserId = "user-1",
                RecipeConfirmationDeclined = true
            },
            "Hier sind die Kunden.",
            []);

        previous.RecipeOutcome.ShouldBeNull();
        previous.WasCorrected.ShouldBeFalse();
        await _repository.DidNotReceive().UpdateAsync(previous);
    }

    // The flag alone is the admission ticket: a redirecting reply carries neither a correction signal nor
    // a bare negation nor a resumed recipe, so without the flag in the gate condition the lookup would
    // never happen and the outcome would stay pending.
    [Test]
    public async Task AnAbandonedGateWithoutAnyOtherSignal_StillReachesTheLookup()
    {
        var previous = GivenPendingConfirmation();

        await _service.CaptureAsync(
            _agentId,
            new LLMContext
            {
                Message = "Zeig mir stattdessen die Kunden",
                UserId = "user-1",
                RecipeConfirmationDeclined = true
            },
            "Hier sind die Kunden.",
            []);

        previous.RecipeOutcome.ShouldBe(RecipeOutcomes.Redirected);
        await _repository.Received(1).UpdateAsync(previous);
        await _caseCollector.DidNotReceiveWithAnyArgs().CollectRecipeDeclineAsync(default!, default);
    }

    // "Nö" is a refusal the correction vocabulary does not know. Before the gate admitted bare
    // negations of its own, such a reply never reached the lookup and left the confirmation gate pending
    // for good.
    [TestCase("Nö")]
    [TestCase("Nee")]
    [TestCase("Nope")]
    [TestCase("Rien")]
    public async Task ABareNegationOutsideTheCorrectionVocabulary_IsRecordedAsARecipeDecline(string message)
    {
        var previous = GivenPendingConfirmation();

        await _service.CaptureAsync(
            _agentId, new LLMContext { Message = message, UserId = "user-1" }, "Alles klar.", []);

        previous.RecipeOutcome.ShouldBe(RecipeOutcomes.Declined);
        previous.WasCorrected.ShouldBeFalse();
        await _repository.Received(1).UpdateAsync(previous);
        await _caseCollector.Received(1).CollectRecipeDeclineAsync(
            Arg.Any<SkillLearningRecipeDecline>(), Arg.Any<CancellationToken>());
        await _caseCollector.DidNotReceiveWithAnyArgs().CollectImplicitCorrectionAsync(default!, default);
    }

    // Plugin languages answer the same question: a single-token negation from a language pack resolves the
    // gate exactly like a core-language one.
    [Test]
    public async Task ABarePluginLanguageNegation_IsRecordedAsARecipeDecline()
    {
        DeclineDetector.Configure([PluginNegationToken], []);
        var previous = GivenPendingConfirmation();

        await _service.CaptureAsync(
            _agentId, new LLMContext { Message = "Nie.", UserId = "user-1" }, "W porządku.", []);

        previous.RecipeOutcome.ShouldBe(RecipeOutcomes.Declined);
        previous.WasCorrected.ShouldBeFalse();
        await _caseCollector.Received(1).CollectRecipeDeclineAsync(
            Arg.Any<SkillLearningRecipeDecline>(), Arg.Any<CancellationToken>());
    }

    // A declined gate is finished business. By the time a correction arrives, the decline turn itself is
    // the most recent trajectory, so the correction lands there - the declined row is never reopened.
    [Test]
    public async Task ADeclinedGate_IsNotReopenedByALaterCorrection()
    {
        var declined = GivenPendingConfirmation();
        var written = new List<SkillSelectionTrajectory>();
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(written.Add));

        await _service.CaptureAsync(
            _agentId,
            new LLMContext { Message = "Nö", UserId = "user-1" },
            "Alles klar, hier sind die Kunden.",
            [new LLMFunctionCall { FunctionName = "list_clients" }]);

        written.Count.ShouldBe(1);
        var declineTurn = written[0];
        _repository.FindMostRecentByAgentAndUserAsync(_agentId, "user-1").Returns(declineTurn);

        await _service.CaptureAsync(
            _agentId,
            new LLMContext { Message = "Nein, das war falsch", UserId = "user-1" },
            "Entschuldigung.",
            []);

        declined.WasCorrected.ShouldBeFalse();
        declined.RecipeOutcome.ShouldBe(RecipeOutcomes.Declined);
        await _repository.DidNotReceive().UpdateAsync(
            Arg.Is<SkillSelectionTrajectory>(t => t.Id == declined.Id && t.WasCorrected));
        await _repository.Received(1).UpdateAsync(
            Arg.Is<SkillSelectionTrajectory>(t => t.Id == declineTurn.Id && t.WasCorrected));
    }

    // The same recipe running again is not by itself a confirmation: a rejection discards the pending
    // recipe and is matched afresh, which can re-trigger the very same recipe. Only an affirmation clears
    // the gate, which is the same evidence LLMService itself acts on.
    [Test]
    public async Task TheSameRecipeReTriggeredByARejection_LeavesThePendingGateUnresolved()
    {
        var previous = GivenPendingConfirmation();

        await _service.CaptureAsync(
            _agentId,
            new LLMContext
            {
                Message = "Nein, neue Gruppe anlegen",
                UserId = "user-1",
                ActiveRecipeName = "setup-consultation"
            },
            "Ich lege die Gruppe an.",
            []);

        previous.RecipeOutcome.ShouldBe(RecipeOutcomes.Pending);
        previous.WasCorrected.ShouldBeFalse();
        await _repository.DidNotReceive().UpdateAsync(previous);
        await _caseCollector.DidNotReceiveWithAnyArgs().CollectRecipeDeclineAsync(default!, default);
    }

    // The engine's own decision, not an inference from the message: TurnPreparationService sets the flag
    // where it clears the gate. ActiveRecipeName is left empty here on purpose, so nothing but the flag
    // can resolve the gate - that is what the old affirmation-plus-same-name inference needed.
    [Test]
    public async Task AnEngineConfirmedGate_IsResolvedAsConfirmed()
    {
        var previous = GivenPendingConfirmation();

        await _service.CaptureAsync(
            _agentId,
            new LLMContext { Message = "ok", UserId = "user-1", RecipeConfirmationAccepted = true },
            "Ich starte die Einrichtung.",
            []);

        previous.RecipeOutcome.ShouldBe(RecipeOutcomes.Confirmed);
        await _repository.Received(1).UpdateAsync(previous);
        await _caseCollector.DidNotReceiveWithAnyArgs().CollectRecipeDeclineAsync(default!, default);
    }

    // The correction window bounds a heuristic attribution; a gate the engine cleared is a fact, and the
    // pending recipe outlives that window by far (PendingRecipeTtlMinutes). A user who reads the
    // question and answers three minutes later resumes the recipe, so the gate must resolve too.
    [Test]
    public async Task AnEngineConfirmedGate_IsResolvedOutsideTheCorrectionWindow()
    {
        var previous = GivenPendingConfirmation();
        previous.CreateTime = DateTime.UtcNow.AddMinutes(-5);

        await _service.CaptureAsync(
            _agentId,
            new LLMContext { Message = "ok", UserId = "user-1", RecipeConfirmationAccepted = true },
            "Ich starte die Einrichtung.",
            []);

        previous.RecipeOutcome.ShouldBe(RecipeOutcomes.Confirmed);
        await _repository.Received(1).UpdateAsync(previous);
    }

    [Test]
    public async Task AnEngineConfirmedGate_LeavesATurnWithoutAPendingOutcomeUntouched()
    {
        var previous = new SkillSelectionTrajectory
        {
            Id = Guid.NewGuid(),
            AgentId = _agentId,
            UserId = "user-1",
            LlmChosenSkill = "list_clients",
            WasCorrected = false,
            RecipeOutcome = null,
            CreateTime = DateTime.UtcNow.AddSeconds(-30),
        };
        _repository.FindMostRecentByAgentAndUserAsync(_agentId, "user-1").Returns(previous);

        await _service.CaptureAsync(
            _agentId,
            new LLMContext { Message = "ok", UserId = "user-1", RecipeConfirmationAccepted = true },
            "Ich starte die Einrichtung.",
            []);

        previous.RecipeOutcome.ShouldBeNull();
        previous.WasCorrected.ShouldBeFalse();
        await _repository.DidNotReceive().UpdateAsync(previous);
    }

    private SkillSelectionTrajectory GivenPendingConfirmation()
    {
        var previous = new SkillSelectionTrajectory
        {
            Id = Guid.NewGuid(),
            AgentId = _agentId,
            UserId = "user-1",
            Locale = "de",
            UserMessageHash = "abc123def4567890",
            IntentExcerpt = "Wie fange ich an?",
            KnowledgeIndexCandidatesJson = "[{\"name\":\"navigate_to\"}]",
            LlmChosenSkill = null,
            RecipeName = "setup-consultation",
            RecipeOutcome = RecipeOutcomes.Pending,
            WasCorrected = false,
            CreateTime = DateTime.UtcNow.AddSeconds(-30),
        };
        _repository.FindMostRecentByAgentAndUserAsync(_agentId, "user-1").Returns(previous);
        return previous;
    }
}
