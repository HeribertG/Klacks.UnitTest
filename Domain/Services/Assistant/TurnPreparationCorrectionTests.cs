// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The clarification half of the correction completion: when the re-routed turn asks instead of acting,
/// what the question is allowed to name, and in which language it may be asked. The ambiguity rule is the
/// subject of five of these tests, because it is the one decision of this feature that is a judgement
/// call rather than a lookup. The planning half (composite, exclusion, gates, note) is covered by
/// TurnPreparationCorrectionPlanningTests and deliberately not repeated here.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class TurnPreparationCorrectionTests
{
    private const string Correction = "Nein, ich meinte alle Mitarbeitenden in die Gruppe.";
    private const string PreviousMessage = "Trag alle Mitarbeitenden in die Gruppe Zürich ein.";
    private const string WrongSkill = "find_customer_candidates";
    private const string WrongSkillLabel = "Searches for matching customers";
    private const string CandidateA = "fill_group_by_criteria";
    private const string CandidateB = "search_employees";
    private const string CandidateALabel = "Fills the group from a rule";
    private const string CandidateBLabel = "Searches for matching employees";
    private const string SecondSentence = ". A second sentence that is never quoted.";
    private const string SpanishSentence =
        "Entendido — no {previousAction}. ¿Te refieres a {optionA} o a {optionB}?";
    private const string SpanishOpening = "Entendido";
    private const string Spanish = "es";
    private const string German = "de";
    private const string UserId = "22222222-2222-2222-2222-222222222222";

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
    public void ResetConfiguredTexts() => GracefulCorrectionTexts.Reset();

    private static AssistantLastAction Anchor() => new()
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
                SkillName = WrongSkill,
                SkillDisplayLabel = WrongSkillLabel,
                ArgumentsJson = "{\"searchString\":\"Zürich\"}",
                IsReadOnly = true,
                Success = true
            }
        ]
    };

    private static GracefulCorrectionInput Input(string language = German) =>
        new(new Agent { Id = Guid.NewGuid(), Name = "Klacksy" },
            new List<string>(), Correction, "conv-1", UserId, language, Anchor(), false);

    private static LLMFunction Function(string name, ToolsetSkillSource source, double? score = null) => new()
    {
        Name = name,
        Description = (name == CandidateA ? CandidateALabel : CandidateBLabel) + SecondSentence,
        ToolsetSource = source,
        RetrievalScore = score
    };

    private async Task<GracefulCorrectionPlan> PlanAsync(string language = German) =>
        (await _service.PlanCorrectionAsync(Input(language)))!;

    [Test]
    public async Task TwoScoredCandidatesWithinTheTolerance_AskWithBothOptions()
    {
        var plan = await PlanAsync();

        var outcome = _service.CompleteCorrection(
            plan,
            [
                Function(CandidateA, ToolsetSkillSource.Keyword, 0.81),
                Function(CandidateB, ToolsetSkillSource.Keyword, 0.79)
            ],
            German);

        outcome.ClarificationReply.ShouldNotBeNullOrWhiteSpace();
        outcome.ClarificationSkillNames.ShouldBe(new[] { CandidateA, CandidateB });
    }

    // Rule 1 applies to a question as well: it names the misunderstanding before it offers the options,
    // and it never leaks an internal snake_case skill name while doing so.
    [Test]
    public async Task TheQuestion_NamesTheMisunderstandingByItsLabel_AndNeverASkillName()
    {
        var plan = await PlanAsync();

        var outcome = _service.CompleteCorrection(
            plan,
            [Function(CandidateA, ToolsetSkillSource.Keyword), Function(CandidateB, ToolsetSkillSource.Keyword)],
            German);

        outcome.ClarificationReply.ShouldContain(WrongSkillLabel);
        outcome.ClarificationReply.ShouldContain(CandidateALabel);
        outcome.ClarificationReply.ShouldContain(CandidateBLabel);
        outcome.ClarificationReply.ShouldNotContain(WrongSkill);
        outcome.ClarificationReply.ShouldNotContain(CandidateA);
        outcome.ClarificationReply.ShouldNotContain(CandidateB);
        outcome.ClarificationReply.ShouldNotContain(SecondSentence);
    }

    [Test]
    public async Task TwoScoredCandidatesOutsideTheTolerance_ActOnTheBetterOne()
    {
        var plan = await PlanAsync();

        var outcome = _service.CompleteCorrection(
            plan,
            [
                Function(CandidateA, ToolsetSkillSource.Keyword, 0.90),
                Function(CandidateB, ToolsetSkillSource.Keyword, 0.40)
            ],
            German);

        outcome.ClarificationReply.ShouldBeNull();
        outcome.ClarificationSkillNames.ShouldBeEmpty();
    }

    // A keyword guarantee is a yes/no, not a degree, so two of them are simply tied and nothing ranks
    // them - which is exactly the case design rule 2 wants a question for.
    [Test]
    public async Task TwoUnscoredCandidates_Ask()
    {
        var plan = await PlanAsync();

        var outcome = _service.CompleteCorrection(
            plan,
            [Function(CandidateA, ToolsetSkillSource.Keyword), Function(CandidateB, ToolsetSkillSource.RecipeStep)],
            German);

        outcome.ClarificationReply.ShouldNotBeNullOrWhiteSpace();
    }

    // A null score is NOT read as zero: retrieval judged the scored one relevant, while the other is only
    // a literal keyword hit.
    [Test]
    public async Task OneScoredOneUnscored_ActsOnTheScoredOne()
    {
        var plan = await PlanAsync();

        var outcome = _service.CompleteCorrection(
            plan,
            [Function(CandidateA, ToolsetSkillSource.Keyword, 0.7), Function(CandidateB, ToolsetSkillSource.Keyword)],
            German);

        outcome.ClarificationReply.ShouldBeNull();
    }

    // A question offers exactly two options, so a single candidate is never a question - and the runner-up
    // must never be indexed for.
    [Test]
    public async Task ASingleCandidate_AsksNothingAndDoesNotReachForARunnerUp()
    {
        var plan = await PlanAsync();

        var outcome = _service.CompleteCorrection(
            plan, [Function(CandidateA, ToolsetSkillSource.Keyword)], German);

        outcome.ClarificationReply.ShouldBeNull();
        outcome.ClarificationSkillNames.ShouldBeEmpty();
    }

    [Test]
    public async Task ACandidateWithoutADescription_AsksNothing()
    {
        var plan = await PlanAsync();
        var nameless = Function(CandidateA, ToolsetSkillSource.Keyword);
        nameless.Description = string.Empty;

        var outcome = _service.CompleteCorrection(
            plan, [nameless, Function(CandidateB, ToolsetSkillSource.Keyword)], German);

        outcome.ClarificationReply.ShouldBeNull();
    }

    // Two CRUD skills can share a first sentence, and "do you mean X or X?" is a question the user cannot
    // answer - the same defect as an option that cannot be named at all.
    [Test]
    public async Task TwoCandidatesWithTheSameLabel_AskNothing()
    {
        var plan = await PlanAsync();
        var twin = Function(CandidateB, ToolsetSkillSource.Keyword);
        twin.Description = CandidateALabel + SecondSentence;

        var outcome = _service.CompleteCorrection(
            plan, [Function(CandidateA, ToolsetSkillSource.Keyword), twin], German);

        outcome.ClarificationReply.ShouldBeNull();
    }

    [Test]
    public async Task InAnInstalledPluginLanguage_AsksInThatLanguage()
    {
        GracefulCorrectionTexts.Configure(Spanish, new Dictionary<string, string>
        {
            [GracefulCorrectionTexts.ClarificationQuestion] = SpanishSentence
        });
        var plan = await PlanAsync(Spanish);

        var outcome = _service.CompleteCorrection(
            plan,
            [Function(CandidateA, ToolsetSkillSource.Keyword), Function(CandidateB, ToolsetSkillSource.Keyword)],
            Spanish);

        outcome.ClarificationReply.ShouldStartWith(SpanishOpening);
        outcome.ClarificationReply.ShouldContain(WrongSkillLabel);
    }

    // The one-language rule (spec §1 rule 4): an installed language whose pack lost the sentence asks
    // nothing at all rather than asking in English.
    [Test]
    public async Task InAnInstalledLanguageWithoutTheSentence_AsksNothingRatherThanAskingInEnglish()
    {
        GracefulCorrectionTexts.Configure(Spanish, new Dictionary<string, string> { ["some.other.key"] = "x" });
        var plan = await PlanAsync(Spanish);

        var outcome = _service.CompleteCorrection(
            plan,
            [Function(CandidateA, ToolsetSkillSource.Keyword), Function(CandidateB, ToolsetSkillSource.Keyword)],
            Spanish);

        outcome.ClarificationReply.ShouldBeNull();
        outcome.ContextNote.ShouldNotBeNullOrWhiteSpace();
    }
}
