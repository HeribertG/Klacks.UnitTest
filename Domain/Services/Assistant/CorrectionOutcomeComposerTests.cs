// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The pure half of the correction completion: which candidates count, when the turn asks instead of
/// acting, what the question may name, and in which language it may be asked at all. The ambiguity rule
/// is the subject of five of these tests, because it is the one decision of this feature that is a
/// judgement call rather than a lookup; the interim English-only gate is the subject of three more,
/// because it is the one place where rule 4 is currently satisfied by refusing rather than by
/// translating. The note's own wording is exercised through the service in
/// TurnPreparationCorrectionPlanningTests, and the per-language catalogue in
/// GracefulCorrectionTextGuardTests - neither is repeated here.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class CorrectionOutcomeComposerTests
{
    private const string Correction = "No, I meant every employee in the group.";
    private const string PreviousMessage = "Put every employee into the Zurich group.";
    private const string WrongSkill = "find_customer_candidates";
    private const string WrongSkillLabel = "Searches for matching customers";
    private const string CandidateA = "fill_group_by_criteria";
    private const string CandidateB = "search_employees";
    private const string CandidateALabel = "Fills the group from a rule";
    private const string CandidateBLabel = "Searches for matching employees";
    private const string SecondSentence = ". A second sentence that is never quoted.";
    private const string UndoSkillName = "remove_shift_from_group";
    private const string UndoneSkill = "add_shift_to_group";
    private const string UndoneSkillLabel = "Assigns a shift to a group";
    private const string English = "en";
    private const string RegionalEnglish = "en-GB";
    private const string German = "de";
    private const string Spanish = "es";
    private const string SpanishSentence =
        "Entendido — no {previousAction}. ¿Te refieres a {optionA} o a {optionB}?";

    [TearDown]
    public void ResetConfiguredTexts() => GracefulCorrectionTexts.Reset();

    private static GracefulCorrectionPlan Plan(string? previousLabel = WrongSkillLabel) => new(
        new AssistantLastAction
        {
            UserId = Guid.NewGuid(),
            ConversationId = "conv-1",
            UserMessage = PreviousMessage,
            AssistantAnswerExcerpt = "I searched for customers.",
            CreateTimeUtc = DateTime.UtcNow,
            Calls =
            [
                new AssistantLastActionCall
                {
                    SkillName = WrongSkill,
                    SkillDisplayLabel = previousLabel,
                    ArgumentsJson = "{\"searchString\":\"Zurich\"}",
                    IsReadOnly = true,
                    Success = true
                }
            ]
        },
        Correction,
        RecipeCorrectionComposer.Compose(PreviousMessage, Correction),
        [WrongSkill]);

    private static LLMFunction Function(string name, ToolsetSkillSource source, double? score = null) => new()
    {
        Name = name,
        Description = (name == CandidateA ? CandidateALabel : CandidateBLabel) + SecondSentence,
        ToolsetSource = source,
        RetrievalScore = score
    };

    private static GracefulCorrectionOutcome Compose(
        IReadOnlyList<LLMFunction> functions,
        string? language = English,
        string? previousLabel = WrongSkillLabel,
        SkillUndoInvocation? undo = null,
        AssistantLastActionCall? undoneCall = null) =>
        CorrectionOutcomeComposer.Compose(Plan(previousLabel), functions, language, undo, undoneCall);

    // The undo is resolved outside and handed in; what the composer decides is whether the offer is made
    // and how it is worded. Rule 3: one sentence, yes/no, in the language of the turn, and never a second
    // question next to a clarification.
    private static SkillUndoInvocation Undo() =>
        new(UndoSkillName, new Dictionary<string, object> { ["shiftId"] = "shift-1" });

    private static AssistantLastActionCall UndoneCall() => new()
    {
        SkillName = UndoneSkill,
        SkillDisplayLabel = UndoneSkillLabel,
        Success = true
    };

    [Test]
    public void AnUndoWithoutAClarification_IsOfferedInTheNoteAndCarriedOut()
    {
        var outcome = Compose(
            [Function(CandidateA, ToolsetSkillSource.Keyword, 0.90), Function(CandidateB, ToolsetSkillSource.Keyword, 0.40)],
            undo: Undo(),
            undoneCall: UndoneCall());

        outcome.Undo.ShouldNotBeNull();
        outcome.Undo!.SkillName.ShouldBe(UndoSkillName);
        outcome.UndoneSkillLabel.ShouldBe(UndoneSkillLabel);
        outcome.ContextNote.ShouldContain(string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            GracefulCorrectionNotes.UndoOfferTemplate,
            UndoneSkillLabel,
            UndoSkillName,
            CorrectionOutcomeComposer.AnswerLanguage(English)));
    }

    [Test]
    public void AnUndoNextToAClarification_IsDropped_SoTheTurnAsksOnlyOneQuestion()
    {
        var outcome = Compose(
            [Function(CandidateA, ToolsetSkillSource.Keyword), Function(CandidateB, ToolsetSkillSource.Keyword)],
            undo: Undo(),
            undoneCall: UndoneCall());

        outcome.ClarificationReply.ShouldNotBeNullOrWhiteSpace();
        outcome.Undo.ShouldBeNull();
        outcome.UndoneSkillLabel.ShouldBeNull();
        outcome.ContextNote.ShouldNotContain(UndoSkillName);
    }

    [Test]
    public void WithoutAnUndo_TheNoteCarriesNoOffer()
    {
        var outcome = Compose(
            [Function(CandidateA, ToolsetSkillSource.Keyword, 0.90), Function(CandidateB, ToolsetSkillSource.Keyword, 0.40)]);

        outcome.Undo.ShouldBeNull();
        outcome.UndoneSkillLabel.ShouldBeNull();
        outcome.ContextNote.ShouldNotContain(UndoSkillName);
    }

    [Test]
    public void TwoScoredCandidatesWithinTheTolerance_AskWithBothOptions()
    {
        var outcome = Compose(
        [
            Function(CandidateA, ToolsetSkillSource.Keyword, 0.81),
            Function(CandidateB, ToolsetSkillSource.Keyword, 0.79)
        ]);

        outcome.ClarificationReply.ShouldNotBeNullOrWhiteSpace();
        outcome.ClarificationSkillNames.ShouldBe(new[] { CandidateA, CandidateB });
    }

    // Rule 1 applies to a question as well: it names the misunderstanding before it offers the options,
    // and it never leaks an internal snake_case skill name while doing so.
    [Test]
    public void TheQuestion_NamesTheMisunderstandingByItsLabel_AndNeverASkillName()
    {
        var outcome = Compose(
            [Function(CandidateA, ToolsetSkillSource.Keyword), Function(CandidateB, ToolsetSkillSource.Keyword)]);

        outcome.ClarificationReply.ShouldContain(WrongSkillLabel);
        outcome.ClarificationReply.ShouldContain(CandidateALabel);
        outcome.ClarificationReply.ShouldContain(CandidateBLabel);
        outcome.ClarificationReply.ShouldNotContain(WrongSkill);
        outcome.ClarificationReply.ShouldNotContain(CandidateA);
        outcome.ClarificationReply.ShouldNotContain(CandidateB);
        outcome.ClarificationReply.ShouldNotContain(SecondSentence);
    }

    [Test]
    public void TwoScoredCandidatesOutsideTheTolerance_ProceedWithoutAQuestion()
    {
        var outcome = Compose(
        [
            Function(CandidateA, ToolsetSkillSource.Keyword, 0.90),
            Function(CandidateB, ToolsetSkillSource.Keyword, 0.40)
        ]);

        outcome.ClarificationReply.ShouldBeNull();
        outcome.ClarificationSkillNames.ShouldBeEmpty();
    }

    // A keyword guarantee is a yes/no, not a degree, so two of them are simply tied and nothing ranks
    // them - which is exactly the case design rule 2 wants a question for.
    [Test]
    public void TwoUnscoredCandidates_Ask()
    {
        var outcome = Compose(
            [Function(CandidateA, ToolsetSkillSource.Keyword), Function(CandidateB, ToolsetSkillSource.RecipeStep)]);

        outcome.ClarificationReply.ShouldNotBeNullOrWhiteSpace();
    }

    // A null score is NOT read as zero: retrieval judged the scored one relevant, while the other is only
    // a literal keyword hit.
    [Test]
    public void OneScoredOneUnscored_ProceedWithoutAQuestion()
    {
        var outcome = Compose(
            [Function(CandidateA, ToolsetSkillSource.Keyword, 0.7), Function(CandidateB, ToolsetSkillSource.Keyword)]);

        outcome.ClarificationReply.ShouldBeNull();
    }

    // The scored candidate leads regardless of the order it arrives in, so the pair the rule judges is
    // the same pair either way.
    [Test]
    public void AnUnscoredCandidateNeverOutranksAScoredOne()
    {
        var outcome = Compose(
            [Function(CandidateB, ToolsetSkillSource.Keyword), Function(CandidateA, ToolsetSkillSource.Keyword, 0.7)]);

        outcome.ClarificationReply.ShouldBeNull();
    }

    // A question offers exactly two options, so a single candidate is never a question - and the
    // runner-up must never be indexed for.
    [Test]
    public void ASingleCandidate_AsksNothingAndDoesNotReachForARunnerUp()
    {
        var outcome = Compose([Function(CandidateA, ToolsetSkillSource.Keyword)]);

        outcome.ClarificationReply.ShouldBeNull();
        outcome.ClarificationSkillNames.ShouldBeEmpty();
    }

    [Test]
    public void ACandidateWithoutADescription_AsksNothing()
    {
        var nameless = Function(CandidateA, ToolsetSkillSource.Keyword);
        nameless.Description = string.Empty;

        var outcome = Compose([nameless, Function(CandidateB, ToolsetSkillSource.Keyword)]);

        outcome.ClarificationReply.ShouldBeNull();
    }

    // Two CRUD skills can share a first sentence, and "do you mean X or X?" is a question the user cannot
    // answer - the same defect as an option that cannot be named at all.
    [Test]
    public void TwoCandidatesWithTheSameLabel_AskNothing()
    {
        var twin = Function(CandidateB, ToolsetSkillSource.Keyword);
        twin.Description = CandidateALabel + SecondSentence;

        var outcome = Compose([Function(CandidateA, ToolsetSkillSource.Keyword), twin]);

        outcome.ClarificationReply.ShouldBeNull();
    }

    // Rule 1 obliges the question to name the misunderstanding. Without a captured display label there is
    // nothing to name, and the note's English stand-in is model-facing only.
    [Test]
    public void WithoutADisplayLabelForThePreviousAction_AsksNothing()
    {
        var outcome = Compose(
            [Function(CandidateA, ToolsetSkillSource.Keyword), Function(CandidateB, ToolsetSkillSource.Keyword)],
            previousLabel: null);

        outcome.ClarificationReply.ShouldBeNull();
        outcome.ClarificationSkillNames.ShouldBeEmpty();
        outcome.ContextNote.ShouldContain(GracefulCorrectionNotes.UnnamedPreviousActionLabel);
    }

    // The interim gate: the option labels are English skill descriptions, so a German question would be a
    // German frame around English nouns - rule 4 broken in substance. Until the labels are localized the
    // turn proceeds without a question instead.
    [Test]
    public void OutsideEnglish_AsksNothingWhileTheLabelsAreEnglish()
    {
        var outcome = Compose(
            [Function(CandidateA, ToolsetSkillSource.Keyword), Function(CandidateB, ToolsetSkillSource.Keyword)],
            German);

        outcome.ClarificationReply.ShouldBeNull();
        outcome.ClarificationSkillNames.ShouldBeEmpty();
        outcome.ContextNote.ShouldNotBeNullOrWhiteSpace();
    }

    // Fail closed, not open: a turn that carries no language at all cannot be shown to be English.
    [Test]
    public void WithoutALanguage_AsksNothing()
    {
        var outcome = Compose(
            [Function(CandidateA, ToolsetSkillSource.Keyword), Function(CandidateB, ToolsetSkillSource.Keyword)],
            language: null);

        outcome.ClarificationReply.ShouldBeNull();
    }

    // The gate reads the base language, so a regional English installation is still English.
    [Test]
    public void RegionalEnglish_StillAsks()
    {
        var outcome = Compose(
            [Function(CandidateA, ToolsetSkillSource.Keyword), Function(CandidateB, ToolsetSkillSource.Keyword)],
            RegionalEnglish);

        outcome.ClarificationReply.ShouldNotBeNullOrWhiteSpace();
    }

    // The pack mechanism stays in place for the owner's final decision, but the interim gate sits in
    // front of it: an installed plugin language asks nothing today even though its sentence resolves.
    [Test]
    public void AnInstalledPluginLanguage_AsksNothingUnderTheInterimGate()
    {
        GracefulCorrectionTexts.Configure(Spanish, new Dictionary<string, string>
        {
            [GracefulCorrectionTexts.ClarificationQuestion] = SpanishSentence
        });

        var outcome = Compose(
            [Function(CandidateA, ToolsetSkillSource.Keyword), Function(CandidateB, ToolsetSkillSource.Keyword)],
            Spanish);

        outcome.ClarificationReply.ShouldBeNull();
    }

    [Test]
    public void DeterministicCandidates_AreOnlyTheGuaranteedOnes_WithoutTheConfirmSkill()
    {
        var functions = new List<LLMFunction>
        {
            Function(CandidateA, ToolsetSkillSource.Keyword),
            Function("list_contracts", ToolsetSkillSource.Retrieved, 0.9),
            Function("navigate_to", ToolsetSkillSource.AlwaysOn),
            Function("show_group", ToolsetSkillSource.Expansion),
            Function(AutonomyDefaults.ConfirmPendingActionSkillName, ToolsetSkillSource.Keyword)
        };

        CorrectionOutcomeComposer.DeterministicCandidates(functions)
            .Select(f => f.Name)
            .ShouldBe(new[] { CandidateA });
    }

    // An abbreviation is not a sentence end: the label is cut at a terminator that whitespace or the end
    // of the text follows AND that a whole word precedes, so neither "e.g." nor "z.B." truncates the
    // label after a single letter the way a plain IndexOf('.') did.
    [TestCase("Adds e.g. contracts to a group. A second sentence.", "Adds e.g. contracts to a group")]
    [TestCase("Erstellt z.B. Auftraege. Zweiter Satz.", "Erstellt z.B. Auftraege")]
    [TestCase("Fills the group from a rule", "Fills the group from a rule")]
    public void AnAbbreviationInTheDescription_DoesNotCutTheLabelShort(string description, string expected)
    {
        var abbreviated = Function(CandidateA, ToolsetSkillSource.Keyword);
        abbreviated.Description = description;

        var outcome = Compose([abbreviated, Function(CandidateB, ToolsetSkillSource.Keyword)]);

        outcome.ClarificationReply.ShouldContain(expected);
    }
}
