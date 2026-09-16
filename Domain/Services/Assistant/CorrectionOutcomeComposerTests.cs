// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The pure half of the correction completion: which candidates count, when the turn asks instead of
/// acting, what the question may name, and in which language it may be asked at all. The ambiguity rule
/// is the subject of five of these tests, because it is the one decision of this feature that is a
/// judgement call rather than a lookup; the language rule is the subject of four more, because the
/// question is the one sentence of this feature no model renders. Since 2026-09-16 the three nouns the
/// question puts into its frame are AUTHORED labels per skill and language (AgentSkill.Labels), not
/// English skill descriptions, so the interim English-only gate is gone: the turn asks in the user's
/// language or, when nobody authored a label for it, not at all. The note's own wording is exercised
/// through the service in TurnPreparationCorrectionPlanningTests, and the per-language catalogue in
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
    private const string WrongSkillGerman = "Passende Kundschaft suchen";
    private const string WrongSkillFrench = "Rechercher des clients correspondants";
    private const string WrongSkillSpanish = "Buscar clientes coincidentes";
    private const string WrongSkillTraditional = "搜尋相符客戶";
    private const string CandidateA = "fill_group_by_criteria";
    private const string CandidateB = "search_employees";
    private const string CandidateALabel = "Fills the group from a rule";
    private const string CandidateBLabel = "Searches for matching employees";
    private const string CandidateAGerman = "Gruppe nach Regel füllen";
    private const string CandidateBGerman = "Mitarbeitende suchen";
    private const string CandidateAFrench = "Remplir le groupe selon une règle";
    private const string CandidateBFrench = "Rechercher des collaborateurs";
    private const string CandidateASpanish = "Rellenar el grupo por regla";
    private const string CandidateBSpanish = "Buscar empleados";
    private const string CandidateATraditional = "依規則填滿群組";
    private const string CandidateBTraditional = "搜尋員工";
    private const string UndoSkillName = "remove_shift_from_group";
    private const string UndoneSkill = "add_shift_to_group";
    private const string UndoneSkillLabel = "Assigns a shift to a group";
    private const string English = "en";
    private const string German = "de";
    private const string French = "fr";
    private const string Spanish = "es";
    private const string Polish = "pl";
    private const string TraditionalChinese = "zh-TW";
    private const string SpanishSentence =
        "Entendido — no {previousAction}. ¿Te refieres a {optionA} o a {optionB}?";
    private const string TraditionalSentence =
        "明白了——不是{previousAction}。您指的是{optionA}還是{optionB}？";
    private const string PolishSentence =
        "Rozumiem — nie {previousAction}. Chodzi o {optionA} czy o {optionB}?";

    /// <summary>
    /// The fixed head of the undo offer, up to its first placeholder: what the note carries when - and
    /// only when - the offer is made. Derived from the template rather than copied, so a reworded offer
    /// cannot leave these tests asserting on a sentence that no longer exists.
    /// </summary>
    private static readonly string UndoOfferPrefix =
        GracefulCorrectionNotes.UndoOfferTemplate[..GracefulCorrectionNotes.UndoOfferTemplate.IndexOf('{')];

    [TearDown]
    public void ResetConfiguredTexts() => GracefulCorrectionTexts.Reset();

    /// <summary>
    /// The authored labels of the corrected skill, as the previous turn stored them alongside the label
    /// it resolved for itself. The QUESTION resolves its previous-action noun from these in the
    /// CORRECTION turn's language, which is what keeps a German noun out of a French sentence when the
    /// user switches language inside the two-minute window.
    /// </summary>
    private static readonly Dictionary<string, string> WrongSkillLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = WrongSkillLabel,
        ["de"] = WrongSkillGerman,
        ["fr"] = WrongSkillFrench,
        ["es"] = WrongSkillSpanish,
        ["zh-TW"] = WrongSkillTraditional
    };

    private static GracefulCorrectionPlan Plan(
        string? previousLabel = WrongSkillLabel,
        IReadOnlyDictionary<string, string>? previousLabels = null) => new(
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
                    SkillLabels = previousLabels ?? (previousLabel == null ? null : WrongSkillLabels),
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
        Description = (name == CandidateA ? CandidateALabel : CandidateBLabel)
                      + ". A second sentence that must never reach the user.",
        ToolsetSource = source,
        RetrievalScore = score,
        Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = name == CandidateA ? CandidateALabel : CandidateBLabel,
            ["de"] = name == CandidateA ? CandidateAGerman : CandidateBGerman,
            ["fr"] = name == CandidateA ? CandidateAFrench : CandidateBFrench,
            ["es"] = name == CandidateA ? CandidateASpanish : CandidateBSpanish,
            ["zh-TW"] = name == CandidateA ? CandidateATraditional : CandidateBTraditional
        }
    };

    private static GracefulCorrectionOutcome Compose(
        IReadOnlyList<LLMFunction> functions,
        string? language = English,
        string? previousLabel = WrongSkillLabel,
        SkillUndoInvocation? undo = null,
        AssistantLastActionCall? undoneCall = null,
        IReadOnlyDictionary<string, string>? previousLabels = null) =>
        CorrectionOutcomeComposer.Compose(
            Plan(previousLabel, previousLabels), functions, language, undo, undoneCall);

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
            CorrectionOutcomeComposer.AnswerLanguage(English)));
        outcome.ContextNote.ShouldNotContain(UndoSkillName);
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
        outcome.ContextNote.ShouldNotContain(UndoOfferPrefix);
    }

    // Zero candidates is the same situation once removed: the note already tells the model to ask for the
    // missing detail, so an undo offer next to it would put a second yes/no into one answer. The user's
    // "ja" would then be ambiguous, and the token it redeems carries a gate-bypassing write.
    [Test]
    public void AnUndoNextToAMissingCandidate_IsDropped_SoTheTurnAsksOnlyOneQuestion()
    {
        var outcome = Compose([], undo: Undo(), undoneCall: UndoneCall());

        outcome.ContextNote.ShouldContain(GracefulCorrectionNotes.NoCandidateSuffix);
        outcome.Undo.ShouldBeNull();
        outcome.UndoneSkillLabel.ShouldBeNull();
        outcome.ContextNote.ShouldNotContain(UndoOfferPrefix);
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
    // and it never leaks an internal snake_case skill name or a raw description while doing so.
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
        outcome.ClarificationReply.ShouldNotContain("second sentence");
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

    // An option that cannot be named in the turn's language is not offered. A skill without an authored
    // label is the plugin case the 21 packs will close; until then the turn proceeds without a question
    // rather than naming one option in a foreign language.
    [Test]
    public void ACandidateWithoutALabelInTheTurnsLanguage_AsksNothing()
    {
        var unlabelled = Function(CandidateA, ToolsetSkillSource.Keyword);
        unlabelled.Labels = null;

        var outcome = Compose([unlabelled, Function(CandidateB, ToolsetSkillSource.Keyword)], German);

        outcome.ClarificationReply.ShouldBeNull();
    }

    // Two CRUD skills can be given the same short label by two different authors, and "do you mean X or
    // X?" is a question the user cannot answer - the same defect as an option that cannot be named at
    // all. The seed guard makes this state unshippable; the composer still refuses it at runtime, because
    // a language pack is authored outside that guard's reach.
    [Test]
    public void TwoCandidatesWithTheSameLabel_AskNothing()
    {
        var twin = Function(CandidateB, ToolsetSkillSource.Keyword);
        twin.Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["de"] = CandidateAGerman
        };

        var outcome = Compose([Function(CandidateA, ToolsetSkillSource.Keyword), twin], German);

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

    // Fail closed, not open: a turn that carries no language at all resolves no label, so there is
    // nothing the question could name.
    [Test]
    public void WithoutALanguage_AsksNothing()
    {
        var outcome = Compose(
            [Function(CandidateA, ToolsetSkillSource.Keyword), Function(CandidateB, ToolsetSkillSource.Keyword)],
            language: null);

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

    // The core of the owner's rule 4: a German turn is asked in German, with German nouns inside the
    // German frame. This is the case the interim gate used to suppress entirely.
    [Test]
    public void AGermanTurn_IsAskedInGermanWithGermanLabels()
    {
        var outcome = Compose(
            [Function(CandidateA, ToolsetSkillSource.Keyword), Function(CandidateB, ToolsetSkillSource.Keyword)],
            German);

        outcome.ClarificationReply.ShouldNotBeNullOrWhiteSpace();
        outcome.ClarificationReply.ShouldStartWith("Verstanden");
        outcome.ClarificationReply.ShouldContain(CandidateAGerman);
        outcome.ClarificationReply.ShouldContain(CandidateBGerman);
        outcome.ClarificationReply.ShouldNotContain(CandidateALabel);
        outcome.ClarificationSkillNames.ShouldBe(new[] { CandidateA, CandidateB });
    }

    // An installed plugin language uses its own frame AND its own nouns. Neither half falls back.
    [Test]
    public void AnInstalledPluginLanguage_IsAskedInThatLanguage()
    {
        GracefulCorrectionTexts.Configure(Spanish, new Dictionary<string, string>
        {
            [GracefulCorrectionTexts.ClarificationQuestion] = SpanishSentence
        });

        var outcome = Compose(
            [Function(CandidateA, ToolsetSkillSource.Keyword), Function(CandidateB, ToolsetSkillSource.Keyword)],
            Spanish);

        outcome.ClarificationReply.ShouldStartWith("Entendido");
        outcome.ClarificationReply.ShouldContain(CandidateASpanish);
        outcome.ClarificationReply.ShouldContain(CandidateBSpanish);
    }

    // A regional pack tag reads its OWN labels, not those of the other script sharing its base language.
    [Test]
    public void ARegionalPackTag_ReadsItsOwnLabels()
    {
        GracefulCorrectionTexts.Configure(TraditionalChinese, new Dictionary<string, string>
        {
            [GracefulCorrectionTexts.ClarificationQuestion] = TraditionalSentence
        });

        var outcome = Compose(
            [Function(CandidateA, ToolsetSkillSource.Keyword), Function(CandidateB, ToolsetSkillSource.Keyword)],
            TraditionalChinese);

        outcome.ClarificationReply.ShouldContain(CandidateATraditional);
        outcome.ClarificationReply.ShouldContain(CandidateBTraditional);
    }

    // The known, owner-accepted gap until the 21 pack label files exist: a language whose frame resolves
    // but whose nouns nobody authored asks NOTHING. Never an English noun in a Polish sentence.
    [Test]
    public void AnInstalledLanguageWithoutAuthoredLabels_AsksNothing()
    {
        GracefulCorrectionTexts.Configure(Polish, new Dictionary<string, string>
        {
            [GracefulCorrectionTexts.ClarificationQuestion] = PolishSentence
        });

        var outcome = Compose(
            [Function(CandidateA, ToolsetSkillSource.Keyword), Function(CandidateB, ToolsetSkillSource.Keyword)],
            Polish);

        outcome.ClarificationReply.ShouldBeNull();
        outcome.ContextNote.ShouldNotBeNullOrWhiteSpace();
    }

    // The cap is the only thing left standing between a pack-authored label and the question, because no
    // seed guard reaches a language pack. An over-long label is cut and the cut end is trimmed.
    [Test]
    public void AnOverlongAuthoredLabel_IsCappedAndTrimmed()
    {
        var overlong = Function(CandidateA, ToolsetSkillSource.Keyword);
        var authored = new string('a', GracefulCorrectionDefaults.OptionLabelMaxLength - 1) + "   bb";
        overlong.Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["de"] = authored };

        var outcome = Compose([overlong, Function(CandidateB, ToolsetSkillSource.Keyword)], German);

        outcome.ClarificationReply.ShouldContain(new string('a', GracefulCorrectionDefaults.OptionLabelMaxLength - 1));
        outcome.ClarificationReply.ShouldNotContain(authored);
    }

    /// <summary>
    /// Rule 4 across a language switch inside the two-minute window. The previous turn ran in German and
    /// stored the German noun it had resolved for itself; the correction arrives in French. All three
    /// nouns of the French frame - the misunderstanding and both options - are resolved HERE, from the
    /// authored labels the record carries, in the language of THIS turn. The stored German label is not
    /// dead weight: the note is model-facing and keeps quoting what the assistant actually told the user.
    /// </summary>
    [Test]
    public void AGermanActionCorrectedInFrench_NamesThePreviousActionInFrench()
    {
        var outcome = Compose(
            [Function(CandidateA, ToolsetSkillSource.Keyword), Function(CandidateB, ToolsetSkillSource.Keyword)],
            French,
            previousLabel: WrongSkillGerman);

        outcome.ClarificationReply.ShouldStartWith("Compris");
        outcome.ClarificationReply.ShouldContain(WrongSkillFrench);
        outcome.ClarificationReply.ShouldNotContain(WrongSkillGerman);
        outcome.ClarificationReply.ShouldContain(CandidateAFrench);
        outcome.ClarificationReply.ShouldContain(CandidateBFrench);
        outcome.ContextNote.ShouldContain(WrongSkillGerman);
    }

    // The same-language case is unchanged: the resolution simply lands on the entry the previous turn
    // already resolved, so the question names exactly what the note names.
    [Test]
    public void AGermanActionCorrectedInGerman_NamesThePreviousActionInGerman()
    {
        var outcome = Compose(
            [Function(CandidateA, ToolsetSkillSource.Keyword), Function(CandidateB, ToolsetSkillSource.Keyword)],
            German,
            previousLabel: WrongSkillGerman);

        outcome.ClarificationReply.ShouldStartWith("Verstanden");
        outcome.ClarificationReply.ShouldContain(WrongSkillGerman);
        outcome.ContextNote.ShouldContain(WrongSkillGerman);
    }

    // Fail closed on the previous-action slot too: both options can be named in the correction's
    // language, but the misunderstanding cannot, and rule 1 obliges the question to name it.
    [Test]
    public void APreviousActionWithoutALabelInTheCorrectionsLanguage_AsksNothing()
    {
        GracefulCorrectionTexts.Configure(Spanish, new Dictionary<string, string>
        {
            [GracefulCorrectionTexts.ClarificationQuestion] = SpanishSentence
        });

        var outcome = Compose(
            [Function(CandidateA, ToolsetSkillSource.Keyword), Function(CandidateB, ToolsetSkillSource.Keyword)],
            Spanish,
            previousLabel: WrongSkillGerman,
            previousLabels: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["de"] = WrongSkillGerman
            });

        outcome.ClarificationReply.ShouldBeNull();
        outcome.ContextNote.ShouldNotBeNullOrWhiteSpace();
    }

    // A record written before the previous turn could capture any authored label at all. The note still
    // quotes its stand-in, the question is not asked.
    [Test]
    public void WithoutAnyAuthoredLabelsOnTheRecord_AsksNothing()
    {
        var outcome = Compose(
            [Function(CandidateA, ToolsetSkillSource.Keyword), Function(CandidateB, ToolsetSkillSource.Keyword)],
            German,
            previousLabel: WrongSkillGerman,
            previousLabels: new Dictionary<string, string>());

        outcome.ClarificationReply.ShouldBeNull();
        outcome.ContextNote.ShouldContain(WrongSkillGerman);
    }
}
