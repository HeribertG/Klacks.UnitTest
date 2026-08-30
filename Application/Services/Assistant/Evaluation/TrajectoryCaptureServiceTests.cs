// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for TrajectoryCaptureService — verifies that HadMutationIntent is derived from
/// the user message via MutationIntentDetector, so tool-call-free turns can later be split into
/// "legitimate info question" vs. "should have acted" for measuring skill-routing quality; and
/// that a same-user follow-up containing a negation/complaint marks the previous trajectory as
/// implicitly corrected only within the short reactive time window, and hands that preceding turn to
/// the learning collector under the cluster key of its own utterance.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant.Evaluation;
using Klacks.Api.Domain.Constants;
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
    private TrajectoryCaptureService _service = null!;
    private Guid _agentId;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ISkillSelectionTrajectoryRepository>();
        _caseCollector = Substitute.For<ISkillLearningCaseCollector>();
        _phrases = Substitute.For<ISkillPhraseRepository>();
        _usage = Substitute.For<ISkillUsageRepository>();
        _phrases
            .GetActiveBySourceAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _usage
            .GetByTurnIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _service = new TrajectoryCaptureService(
            _repository, _caseCollector, _phrases, _usage, Substitute.For<ILogger<TrajectoryCaptureService>>());
        _agentId = Guid.NewGuid();
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

    private static SkillUsageRecord Usage(Guid turnId, bool success) => new()
    {
        SkillName = "list_open_shifts",
        Category = SkillCategory.Query,
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        Success = success,
        TurnId = turnId
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
}
