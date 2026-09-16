// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The six gates of the graceful-correction path, each with a positive and a negative case. The gates
/// are AND-ed in order, so every test isolates exactly one of them by keeping the other five satisfied.
/// The detector reports WHICH gate rejected, because a correction that silently does not happen is the
/// hardest failure of this feature to diagnose from a log.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class GracefulCorrectionDetectorTests
{
    private const string Correction = "Nein, ich meinte alle Mitarbeitenden in die Gruppe.";

    private static AssistantLastAction Anchor(DateTime? createdAt = null, bool superseded = false) => new()
    {
        UserId = Guid.NewGuid(),
        ConversationId = "conv-1",
        UserMessage = "Trag alle Mitarbeitenden in die Gruppe Zürich ein.",
        AssistantAnswerExcerpt = "Ich habe nach Kunden gesucht.",
        CreateTimeUtc = createdAt ?? DateTime.UtcNow,
        SupersededAtUtc = superseded ? DateTime.UtcNow : null,
        Calls =
        [
            new AssistantLastActionCall
            {
                SkillName = "find_customer_candidates",
                SkillDisplayLabel = "Finds matching customers",
                ArgumentsJson = "{\"searchString\":\"Zürich\"}",
                IsReadOnly = true,
                Success = true
            }
        ]
    };

    private static GracefulCorrectionGate Evaluate(
        string message,
        AssistantLastAction? anchor,
        bool recipeIsActive = false,
        bool routesAlone = false) =>
        GracefulCorrectionDetector.Evaluate(message, anchor, recipeIsActive, routesAlone, DateTime.UtcNow);

    [Test]
    public void AllGatesSatisfied_IsACorrection()
    {
        Evaluate(Correction, Anchor()).ShouldBe(GracefulCorrectionGate.Passed);
    }

    [Test]
    public void G0_NoAnchor_IsRejectedByTheAnchorGate()
    {
        Evaluate(Correction, null).ShouldBe(GracefulCorrectionGate.Anchor);
    }

    [Test]
    public void G0_AnchorOlderThanTheWindow_IsRejectedByTheAnchorGate()
    {
        Evaluate(Correction, Anchor(DateTime.UtcNow.AddMinutes(-5))).ShouldBe(GracefulCorrectionGate.Anchor);
    }

    [Test]
    public void G0_SupersededAnchor_IsRejectedByTheAnchorGate()
    {
        Evaluate(Correction, Anchor(superseded: true)).ShouldBe(GracefulCorrectionGate.Anchor);
    }

    [Test]
    public void G1_ActiveRecipe_IsRejectedByTheRecipeGate()
    {
        Evaluate(Correction, Anchor(), recipeIsActive: true).ShouldBe(GracefulCorrectionGate.ActiveRecipe);
    }

    [Test]
    public void G2_NoCorrectionSignal_IsRejectedBySignal()
    {
        Evaluate("Und jetzt bitte die Gruppe Bern.", Anchor()).ShouldBe(GracefulCorrectionGate.Signal);
    }

    [Test]
    public void G3_BareNegation_StaysADecline()
    {
        Evaluate("Nein.", Anchor()).ShouldBe(GracefulCorrectionGate.BareNegation);
    }

    [Test]
    public void G4_IndependentQuestion_StaysAQuestion()
    {
        Evaluate("Nein, wie finde ich heraus, welche Gruppen ein Mitarbeiter hat?", Anchor())
            .ShouldBe(GracefulCorrectionGate.TopicSwitch);
    }

    [Test]
    public void G4_CorrectionWithoutAQuestion_PassesTheTopicSwitchGate()
    {
        Evaluate(Correction, Anchor()).ShouldBe(GracefulCorrectionGate.Passed);
    }

    [Test]
    public void G5_CorrectionThatRoutesOnItsOwn_IsANewRequest()
    {
        Evaluate(Correction, Anchor(), routesAlone: true).ShouldBe(GracefulCorrectionGate.RoutesAlone);
    }

    [Test]
    public void ExcludedSkillNames_AreTheAnchorsCalls()
    {
        GracefulCorrectionDetector.ExcludedSkillNames(Anchor()).ShouldBe(new[] { "find_customer_candidates" });
    }

    [Test]
    public void ExcludedSkillNames_NeverContainTheConfirmSkill()
    {
        var anchor = Anchor();
        anchor.Calls =
        [
            new AssistantLastActionCall { SkillName = AutonomyDefaults.ConfirmPendingActionSkillName, Success = true },
            new AssistantLastActionCall { SkillName = "add_shift_to_group", Success = true }
        ];

        GracefulCorrectionDetector.ExcludedSkillNames(anchor).ShouldBe(new[] { "add_shift_to_group" });
    }
}
