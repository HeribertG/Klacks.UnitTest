// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the ratchet stops on the description sharpening. Two of them together decide whether the
/// loop can be left running: a correction is spent exactly once, so the same handful of complaints cannot
/// justify a fresh narrowing on every run, and a skill whose description the loop already narrowed by
/// itself is left alone until a person has seen it - because the corrections a narrowing produces are
/// precisely what would justify the next one. The third behaviour pinned here is the golden case: the
/// skill the user actually meant is frozen before the narrowing, so once it works no later round may move
/// that utterance off its target again.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation;

using Klacks.Api.Application.Services.Assistant.Evaluation;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.UnitTest.Application.Services.Assistant.Learning;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SkillDescriptionOptimizerTests
{
    private const string WronglyChosen = "list_clients";
    private const string IntendedTarget = "revenue_per_client";
    private const string Excerpt = "Zeige mir die Umsatzstatistik pro Kunde";
    private const string Suggestion =
        "{\"description\":\"Lists the contract data of one client.\",\"justification\":\"too broad\"}";

    private ISkillSelectionTrajectoryRepository _trajectories = null!;
    private IProposedSkillChangeRepository _proposals = null!;
    private IAgentSkillRepository _skills = null!;
    private IAgentRepository _agents = null!;
    private ISkillLearningCaseRepository _cases = null!;
    private ISkillLearningGoldenCaseRepository _goldenCases = null!;
    private IEvalRunRepository _evalRuns = null!;
    private IEvalRunItemRepository _evalRunItems = null!;
    private ITurnGoldsetLoader _goldsetLoader = null!;
    private ISkillLearningOptionsProvider _learningOptions = null!;
    private FakeLLMProvider _provider = null!;
    private SkillDescriptionOptimizer _optimizer = null!;
    private Agent _agent = null!;
    private AgentSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _agent = new Agent { Id = Guid.NewGuid() };
        _agents = Substitute.For<IAgentRepository>();
        _agents.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(_agent);

        _skill = new AgentSkill
        {
            Id = Guid.NewGuid(),
            AgentId = _agent.Id,
            Name = WronglyChosen,
            Description = "Lists everything about clients."
        };

        _skills = Substitute.For<IAgentSkillRepository>();
        _skills.GetByNameAsync(_agent.Id, WronglyChosen, Arg.Any<CancellationToken>()).Returns(_skill);

        _proposals = Substitute.For<IProposedSkillChangeRepository>();
        _proposals.HasOpenProposalForSkillAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        _trajectories = Substitute.For<ISkillSelectionTrajectoryRepository>();
        _cases = Substitute.For<ISkillLearningCaseRepository>();
        _goldenCases = Substitute.For<ISkillLearningGoldenCaseRepository>();
        _goldenCases.ExistsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        _evalRuns = Substitute.For<IEvalRunRepository>();
        _evalRuns.GetLatestFullRunAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((EvalRun?)null);
        _evalRunItems = Substitute.For<IEvalRunItemRepository>();
        _goldsetLoader = Substitute.For<ITurnGoldsetLoader>();

        _learningOptions = Substitute.For<ISkillLearningOptionsProvider>();
        _learningOptions.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new SkillLearningOptions(
                SkillLearningDefaults.MinOccurrences,
                SkillLearningDefaults.MinDistinctUsers,
                SkillLearningDefaults.PruneDays,
                SkillLearningDefaults.RetentionDays));

        _provider = new FakeLLMProvider();

        var modelResolver = Substitute.For<ICheapestModelResolver>();
        modelResolver.ResolveAsync(Arg.Any<CancellationToken>())
            .Returns(((LLMModel?)new LLMModel { ModelId = "fake", ApiModelId = "fake-1" }, (ILLMProvider?)_provider));

        _optimizer = new SkillDescriptionOptimizer(
            _trajectories, _proposals, _skills, _agents, _cases, _goldenCases,
            _evalRuns, _evalRunItems, _goldsetLoader, modelResolver, _learningOptions,
            Substitute.For<ILogger<SkillDescriptionOptimizer>>());
    }

    private SkillSelectionTrajectory GivenWrongSkillCorrection(
        string? intendedTarget = IntendedTarget, string answer = Suggestion)
    {
        var trajectory = new SkillSelectionTrajectory
        {
            Id = Guid.NewGuid(),
            AgentId = _agent.Id,
            Locale = "de",
            IntentExcerpt = Excerpt,
            LlmChosenSkill = WronglyChosen,
            WasCorrected = true,
            CorrectionType = CorrectionTypes.WrongSkill
        };

        _trajectories.GetUncorrectedWrongSkillAsync(_agent.Id, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([trajectory]);

        _cases.FindExpectedSkillByTrajectoryAsync(trajectory.Id, Arg.Any<CancellationToken>())
            .Returns(intendedTarget);

        _provider.Answering(answer);
        return trajectory;
    }

    // Without the watermark the same complaint justifies a fresh narrowing on every single run, and each
    // narrowing produces the complaints that justify the next one.
    [Test]
    public async Task TheCorrectionsAProposalWasBuiltFrom_AreSpent()
    {
        var trajectory = GivenWrongSkillCorrection();

        (await _optimizer.GenerateProposalsAsync(30)).Generated.ShouldBe(1);

        await _trajectories.Received(1).MarkSharpenedAsync(
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == trajectory.Id),
            Arg.Any<DateTime>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ACorrectionThatProducedNoSuggestion_IsNotSpent()
    {
        GivenWrongSkillCorrection(answer: "this is not json");

        (await _optimizer.GenerateProposalsAsync(30)).Generated.ShouldBe(0);

        await _trajectories.DidNotReceive().MarkSharpenedAsync(
            Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    // A description the loop narrowed by itself stays as it is until a person has seen it.
    [Test]
    public async Task ASkillTheLoopAlreadyNarrowed_GetsNoSecondProposal()
    {
        GivenWrongSkillCorrection();
        _proposals.HasOpenProposalForSkillAsync(
                _skill.Id, ProposedChangeFields.Description, Arg.Any<CancellationToken>())
            .Returns(true);

        (await _optimizer.GenerateProposalsAsync(30)).Generated.ShouldBe(0);

        await _proposals.DidNotReceive().AddAsync(
            Arg.Any<ProposedSkillChange>(), Arg.Any<CancellationToken>());
    }

    // A skill already carrying an open proposal is skipped before any model call, so it must not count as an
    // attempt: an attempt is what the optimizer actually asked the model for.
    [Test]
    public async Task ASkillWithAnOpenProposal_IsSkippedWithoutCountingAsAnAttempt()
    {
        GivenWrongSkillCorrection();
        _proposals.HasOpenProposalForSkillAsync(
                _skill.Id, ProposedChangeFields.Description, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _optimizer.GenerateProposalsAsync(30);

        result.Attempts.ShouldBe(0);
        result.Failures.ShouldBe(0);
    }

    // Every correction group that reaches the model is an attempt; a clean suggestion is not a failure.
    [Test]
    public async Task ASuccessfulCorrection_CountsOneAttemptAndNoFailure()
    {
        GivenWrongSkillCorrection();

        var result = await _optimizer.GenerateProposalsAsync(30);

        result.Attempts.ShouldBe(1);
        result.Failures.ShouldBe(0);
    }

    // An answer with no JSON object at all - a reasoning model that spent its budget without ever writing
    // the answer, for example - is a failed attempt, not a skipped one.
    [Test]
    public async Task AnUnparsableAnswer_CountsAsAFailedAttempt()
    {
        GivenWrongSkillCorrection(answer: "this is not json");

        var result = await _optimizer.GenerateProposalsAsync(30);

        result.Attempts.ShouldBe(1);
        result.Failures.ShouldBe(1);
    }

    // The model correctly decided nothing needs to change - that is a working attempt, not a failure the
    // weekly pipeline should alarm on.
    [Test]
    public async Task AModelThatKeepsTheCurrentDescription_CountsAsAnAttemptButNotAFailure()
    {
        GivenWrongSkillCorrection(answer:
            "{\"description\":\"Lists everything about clients.\",\"justification\":\"already tight\"}");

        var result = await _optimizer.GenerateProposalsAsync(30);

        result.Generated.ShouldBe(0);
        result.Attempts.ShouldBe(1);
        result.Failures.ShouldBe(0);
    }

    // Live finding 2026-09-27: DeepSeek reasoning models can spend the whole completion budget on
    // reasoning_content and answer with empty content and no tool call - LLMProviderResponse reports that as
    // ReasoningWithoutContent. The optimizer must count it as a failed attempt, not silently drop it.
    [Test]
    public async Task AReasoningOnlyAnswer_CountsAsAFailedAttempt()
    {
        GivenWrongSkillCorrection();
        _provider.ClearAnswers().AnsweringWith(new LLMProviderResponse
        {
            Success = true,
            Content = string.Empty,
            ReasoningWithoutContent = true
        });

        var result = await _optimizer.GenerateProposalsAsync(30);

        result.Generated.ShouldBe(0);
        result.Attempts.ShouldBe(1);
        result.Failures.ShouldBe(1);
    }

    // Verified 2026-09-27: 256 tokens let a DeepSeek reasoning model exhaust the budget on reasoning_content
    // before writing any JSON. The request must carry a budget generous enough for that plus a full answer.
    [Test]
    public async Task TheSuggestionRequest_CarriesAMaxTokenBudgetThatSurvivesAReasoningModel()
    {
        GivenWrongSkillCorrection();

        await _optimizer.GenerateProposalsAsync(30);

        _provider.Requests.ShouldHaveSingleItem().MaxTokens.ShouldBeGreaterThanOrEqualTo(2048);
    }

    // The extraction between the first '{' and the last '}' already skips a code fence wrapped around the
    // JSON object - some models answer that way even when told not to.
    [Test]
    public async Task AnAnswerWrappedInAMarkdownCodeFence_IsStillParsed()
    {
        GivenWrongSkillCorrection(answer: "```json\n" + Suggestion + "\n```");

        var result = await _optimizer.GenerateProposalsAsync(30);

        result.Generated.ShouldBe(1);
    }

    // The frozen case names where the wish belonged, never the skill being narrowed: an expectation
    // pointing at the narrowed skill is exactly what the narrowing is meant to break, so freezing it
    // would block every sharpening for good.
    [Test]
    public async Task TheSkillTheUserActuallyMeant_IsFrozenAsAGoldenCase()
    {
        GivenWrongSkillCorrection();

        await _optimizer.GenerateProposalsAsync(30);

        await _goldenCases.Received(1).AddAsync(
            Arg.Is<SkillLearningGoldenCase>(c =>
                c.Query == Excerpt && c.ExpectedSourceId == IntendedTarget && c.Locale == "de"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ACorrectionThatNamedNoTarget_FreezesNothing()
    {
        GivenWrongSkillCorrection(intendedTarget: null);

        (await _optimizer.GenerateProposalsAsync(30)).Generated.ShouldBe(1);

        await _goldenCases.DidNotReceive().AddAsync(
            Arg.Any<SkillLearningGoldenCase>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ACorrectionPointingBackAtTheNarrowedSkill_FreezesNothing()
    {
        GivenWrongSkillCorrection(intendedTarget: WronglyChosen);

        await _optimizer.GenerateProposalsAsync(30);

        await _goldenCases.DidNotReceive().AddAsync(
            Arg.Any<SkillLearningGoldenCase>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AGoldenCaseThatAlreadyExists_IsNotFrozenTwice()
    {
        GivenWrongSkillCorrection();
        _goldenCases.ExistsAsync(Excerpt, IntendedTarget, Arg.Any<CancellationToken>()).Returns(true);

        await _optimizer.GenerateProposalsAsync(30);

        await _goldenCases.DidNotReceive().AddAsync(
            Arg.Any<SkillLearningGoldenCase>(), Arg.Any<CancellationToken>());
    }

    // The freeze has to happen before the proposal exists, so the gate of the very same pass replays it.
    [Test]
    public async Task TheGoldenCase_IsFrozenBeforeTheProposalIsWritten()
    {
        GivenWrongSkillCorrection();
        var order = new List<string>();

        _goldenCases.AddAsync(Arg.Any<SkillLearningGoldenCase>(), Arg.Any<CancellationToken>())
            .Returns(_ => { order.Add("golden"); return Task.CompletedTask; });
        _proposals.AddAsync(Arg.Any<ProposedSkillChange>(), Arg.Any<CancellationToken>())
            .Returns(_ => { order.Add("proposal"); return Task.CompletedTask; });

        await _optimizer.GenerateProposalsAsync(30);

        order.ShouldBe(["golden", "proposal"]);
    }
}
