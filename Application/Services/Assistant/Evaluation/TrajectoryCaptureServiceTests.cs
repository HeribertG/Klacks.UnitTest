// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for TrajectoryCaptureService — verifies that HadMutationIntent is derived from
/// the user message via MutationIntentDetector, so tool-call-free turns can later be split into
/// "legitimate info question" vs. "should have acted" for measuring skill-routing quality; and
/// that a same-user follow-up containing a negation/complaint marks the previous trajectory as
/// implicitly corrected only within the short reactive time window.
/// </summary>

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
    private TrajectoryCaptureService _service = null!;
    private Guid _agentId;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ISkillSelectionTrajectoryRepository>();
        _service = new TrajectoryCaptureService(_repository, Substitute.For<ILogger<TrajectoryCaptureService>>());
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
    }

    [Test]
    public async Task MessageWithoutNegation_NeverLooksUpPreviousTrajectory()
    {
        var context = new LLMContext { Message = "Zeig mir bitte die offenen Schichten", UserId = "user-1" };

        await _service.CaptureAsync(_agentId, context, "Hier sind die offenen Schichten.", []);

        await _repository.DidNotReceiveWithAnyArgs().FindMostRecentByAgentAndUserAsync(default, default!);
    }
}
