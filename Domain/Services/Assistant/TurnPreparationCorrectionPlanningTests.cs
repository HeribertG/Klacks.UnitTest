// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Part (c) of the turn preparation: the correction planning and the volatile note. Covers the two
/// gates the service owns itself rather than the detector - the real pending-recipe flag (G1) and the
/// deterministic route probe (G5), including the fail-closed policy for a broken probe - plus the note's
/// label fallback, which is the one place an internal skill name could leak in front of a user.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class TurnPreparationCorrectionPlanningTests
{
    private const string Correction = "Nein, ich meinte alle Mitarbeitenden in die Gruppe.";
    private const string PreviousMessage = "Trag alle Mitarbeitenden in die Gruppe Zürich ein.";
    private const string PreviousSkillName = "find_customer_candidates";
    private const string PreviousSkillLabel = "Finds matching customers";
    private const string CandidateSkillName = "add_clients_to_group";
    private const string UserId = "11111111-1111-1111-1111-111111111111";

    private IDeterministicRouteProbe _routeProbe = null!;
    private TurnPreparationService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _routeProbe = Substitute.For<IDeterministicRouteProbe>();
        _routeProbe.GuaranteedSkillNamesAsync(
                Arg.Any<Agent?>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        _service = new TurnPreparationService(
            Substitute.For<IPendingConfirmationStore>(),
            recipeEngine: null!,
            recipeRunRecorder: Substitute.For<IRecipeRunRecorder>(),
            slotExtractor: null!,
            lastActionStore: Substitute.For<IAssistantLastActionStore>(),
            routeProbe: _routeProbe,
            logger: Substitute.For<ILogger<TurnPreparationService>>());
    }

    [TearDown]
    public void ResetDetectors()
    {
        DeclineDetector.Reset();
        ImplicitCorrectionDetector.Reset();
    }

    private static AssistantLastAction Anchor(string? displayLabel = PreviousSkillLabel) => new()
    {
        UserId = Guid.Parse(UserId),
        ConversationId = "conv-1",
        UserMessage = PreviousMessage,
        AssistantAnswerExcerpt = "Ich habe nach Kunden gesucht.",
        CreateTimeUtc = DateTime.UtcNow,
        Calls =
        [
            new AssistantLastActionCall
            {
                SkillName = PreviousSkillName,
                SkillDisplayLabel = displayLabel,
                ArgumentsJson = "{\"searchString\":\"Zürich\"}",
                IsReadOnly = true,
                Success = true
            }
        ]
    };

    private static GracefulCorrectionInput Input(
        AssistantLastAction? anchor, bool recipeIsActive = false, string message = Correction) =>
        new(null, new List<string>(), message, "conv-1", UserId, "de", anchor, recipeIsActive);

    private static LLMFunction Candidate(string name, ToolsetSkillSource source, double? score = null) => new()
    {
        Name = name,
        Description = "Adds clients to a group.",
        ToolsetSource = source,
        RetrievalScore = score
    };

    [Test]
    public async Task AllGatesSatisfied_PlansTheCompositeAndTheExclusion()
    {
        var plan = await _service.PlanCorrectionAsync(Input(Anchor()));

        plan.ShouldNotBeNull();
        plan!.CompositeMessage.ShouldBe(RecipeCorrectionComposer.Compose(PreviousMessage, Correction));
        plan.CompositeMessage.ShouldContain(PreviousMessage);
        plan.CompositeMessage.ShouldContain(Correction);
        plan.CorrectionMessage.ShouldBe(Correction);
        plan.ExcludedSkillNames.ShouldBe(new[] { PreviousSkillName });
    }

    // G1 is passed in from the pending-recipe store, not inferred from the anchor: a recipe that called
    // a tool and then paused on an ask leaves both, and the user's next message answers the recipe.
    [Test]
    public async Task ActiveRecipe_PlansNoCorrection()
    {
        var plan = await _service.PlanCorrectionAsync(Input(Anchor(), recipeIsActive: true));

        plan.ShouldBeNull();
    }

    [Test]
    public async Task CorrectionThatRoutesOnItsOwn_PlansNoCorrection()
    {
        _routeProbe.GuaranteedSkillNamesAsync(
                Arg.Any<Agent?>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { CandidateSkillName });

        var plan = await _service.PlanCorrectionAsync(Input(Anchor()));

        plan.ShouldBeNull();
    }

    // Fail-closed: an empty probe result means "does not route alone" and OPENS the correction path, so a
    // broken probe must be treated as the opposite verdict rather than as an empty result.
    [Test]
    public async Task BrokenProbe_PlansNoCorrection()
    {
        _routeProbe.GuaranteedSkillNamesAsync(
                Arg.Any<Agent?>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("embedding API key missing"));

        var plan = await _service.PlanCorrectionAsync(Input(Anchor()));

        plan.ShouldBeNull();
    }

    [Test]
    public async Task CancellationOfTheProbe_IsNotSwallowed()
    {
        _routeProbe.GuaranteedSkillNamesAsync(
                Arg.Any<Agent?>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await _service.PlanCorrectionAsync(Input(Anchor())));
    }

    [Test]
    public async Task NoAnchor_PlansNoCorrection()
    {
        var plan = await _service.PlanCorrectionAsync(Input(null));

        plan.ShouldBeNull();
    }

    [Test]
    public async Task TheNote_NamesThePreviousLabelTheCorrectionAndTheLanguage()
    {
        var plan = await _service.PlanCorrectionAsync(Input(Anchor()));

        var outcome = _service.CompleteCorrection(
            plan!, [Candidate(CandidateSkillName, ToolsetSkillSource.Keyword)], "de");

        outcome.ContextNote.ShouldContain(PreviousSkillLabel);
        outcome.ContextNote.ShouldContain(Correction);
        outcome.ContextNote.ShouldContain("'de'");
        outcome.ClarificationReply.ShouldBeNull();
        outcome.ClarificationSkillNames.ShouldBeEmpty();
    }

    // The internal snake_case name must never reach the user, and a turn without a captured label is
    // exactly the case in which it otherwise would.
    [Test]
    public async Task WithoutADisplayLabel_TheNoteUsesTheRedactionAndNeverTheSkillName()
    {
        var plan = await _service.PlanCorrectionAsync(Input(Anchor(displayLabel: null)));

        var outcome = _service.CompleteCorrection(
            plan!, [Candidate(CandidateSkillName, ToolsetSkillSource.Keyword)], "de");

        outcome.ContextNote.ShouldContain(MutationGuardConstants.RedactedInternalIdentifier);
        outcome.ContextNote.ShouldNotContain(PreviousSkillName);
    }

    [Test]
    public async Task WithoutALanguage_TheNoteFallsBackToTheDefaultTag()
    {
        var plan = await _service.PlanCorrectionAsync(Input(Anchor()));

        var outcome = _service.CompleteCorrection(plan!, [], null);

        outcome.ContextNote.ShouldContain($"'{LanguageConfig.DefaultLanguageFallback}'");
    }

    [Test]
    public async Task WithoutADeterministicCandidate_TheNoteSaysSo()
    {
        var plan = await _service.PlanCorrectionAsync(Input(Anchor()));

        var outcome = _service.CompleteCorrection(
            plan!, [Candidate(CandidateSkillName, ToolsetSkillSource.Retrieved, 0.9)], "de");

        outcome.ContextNote.ShouldContain(GracefulCorrectionNotes.NoCandidateSuffix);
    }

    [Test]
    public async Task WithADeterministicCandidate_TheNoteDoesNotSayThereIsNone()
    {
        var plan = await _service.PlanCorrectionAsync(Input(Anchor()));

        var outcome = _service.CompleteCorrection(
            plan!, [Candidate(CandidateSkillName, ToolsetSkillSource.RecipeStep)], "de");

        outcome.ContextNote.ShouldNotContain(GracefulCorrectionNotes.NoCandidateSuffix);
    }

    [Test]
    public void DeterministicCandidates_AreOnlyTheGuaranteedOnes_WithoutTheConfirmSkill()
    {
        var functions = new List<LLMFunction>
        {
            Candidate(CandidateSkillName, ToolsetSkillSource.Keyword),
            Candidate("list_contracts", ToolsetSkillSource.Retrieved, 0.9),
            Candidate("navigate_to", ToolsetSkillSource.AlwaysOn),
            Candidate("show_group", ToolsetSkillSource.Expansion),
            Candidate(AutonomyDefaults.ConfirmPendingActionSkillName, ToolsetSkillSource.Keyword)
        };

        TurnPreparationService.DeterministicCandidates(functions)
            .Select(f => f.Name)
            .ShouldBe(new[] { CandidateSkillName });
    }
}
