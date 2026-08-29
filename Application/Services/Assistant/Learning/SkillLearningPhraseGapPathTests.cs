// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The phrase_gap arm end to end through the code that really decides it: the loop, the classifier and
/// the answer parser, with only the language model itself replaced. Everything else in the suite hands
/// SkillLearningLoop a substituted ILearnedArtifactGenerator, so a change in ReadClassification - which
/// is where a model's answer is validated against the offered skills - could not fail a single test.
/// The end-to-end fixture used to cover this against a real model; since the routing probe was raised to
/// the production cap on 2026-08-29 its phrase_gap wish is answered as "already routed" before any of
/// this runs, and that arm has proved nothing since.
/// All three reachable endings of the arm are here, including the one that says the arm is a dead end
/// without an explicit correction - that is not a defect of these tests but the shape of the loop, and
/// it is asserted so it cannot change unnoticed.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Learning;

using Klacks.Api.Application.Services.Assistant.Learning;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SkillLearningPhraseGapPathTests
{
    private const string Excerpt = "Zeige mir die Umsatzstatistik pro Kunde";
    private const string Target = "revenue_per_client";
    private const string OfferedSkill = "list_clients";

    private ISkillLearningClusterRepository _clusters = null!;
    private ISkillLearningCaseRepository _cases = null!;
    private ISkillRoutingOracle _oracle = null!;
    private IPhraseLearner _phraseLearner = null!;
    private ICapabilityLearner _capabilityLearner = null!;
    private FakeLLMProvider _provider = null!;
    private SkillLearningLoop _loop = null!;

    [SetUp]
    public void SetUp()
    {
        _clusters = Substitute.For<ISkillLearningClusterRepository>();
        _clusters.TryClaimForLearningAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _cases = Substitute.For<ISkillLearningCaseRepository>();
        _cases.ListByClusterAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        _oracle = Substitute.For<ISkillRoutingOracle>();
        _phraseLearner = Substitute.For<IPhraseLearner>();
        _capabilityLearner = Substitute.For<ICapabilityLearner>();

        var sharpener = Substitute.For<ISkillDescriptionSharpener>();
        sharpener.RunAsync(Arg.Any<CancellationToken>()).Returns((0, 0));

        _provider = new FakeLLMProvider();
        var resolver = Substitute.For<ICheapestModelResolver>();
        resolver.ResolveAsync(Arg.Any<CancellationToken>())
            .Returns((new LLMModel { ApiModelId = "fake-1" }, (ILLMProvider?)_provider));

        var generator = new LearnedArtifactGenerator(
            resolver, Substitute.For<ILogger<LearnedArtifactGenerator>>());

        _loop = new SkillLearningLoop(
            _clusters, _cases, generator, _oracle, _phraseLearner, _capabilityLearner, sharpener,
            Substitute.For<ILogger<SkillLearningLoop>>());
    }

    /// <summary>
    /// The only ending in which a phrase is actually learned. The model returns phrase_gap and names the
    /// skill; the name survives ReadClassification although it is NOT among the offered skills, because it
    /// is the one the user corrected to - that exception is the whole reachable path.
    /// </summary>
    [Test]
    public async Task APhraseGapVerdictForACorrectedSkill_LearnsThePhrase()
    {
        var cluster = GivenReadyCluster();
        GivenCorrection(cluster.Id, Target);
        GivenProbe(OfferedSkill);
        GivenAnswer(Target);
        var phraseId = Guid.NewGuid();
        _phraseLearner.LearnAsync(
                Arg.Any<SkillLearningClusterContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PhraseLearningOutcome.Success(phraseId, "umsatz pro kunde"));

        var summary = await _loop.RunAsync();

        summary.Learned.ShouldBe(1);
        await _phraseLearner.Received(1).LearnAsync(
            Arg.Any<SkillLearningClusterContext>(), Target, Arg.Any<CancellationToken>());
        await _clusters.Received(1).FinishLearningAsync(
            cluster.Id, SkillLearningClusterStatuses.LearnedPhrase, SkillLearningOutcomeKinds.Phrase,
            phraseId.ToString(), null, 0, Arg.Any<CancellationToken>());
        _provider.Requests.Count.ShouldBe(1, "The verdict has to come from the model, not from a substitute.");
    }

    /// <summary>
    /// The dead end, asserted rather than described. Without a correction the classifier may only name a
    /// skill from the offered list (ReadClassification), and LearnPhraseAsync dismisses precisely when the
    /// target is in that list - so the two conditions are the same list and no classifier-chosen target
    /// can ever be learned. Nothing in this file works around that; it is pinned so a future change to
    /// either side is visible.
    /// </summary>
    [Test]
    public async Task APhraseGapVerdictWithoutACorrection_IsDismissedBecauseTheTargetMustComeFromTheOfferedList()
    {
        var cluster = GivenReadyCluster();
        GivenProbe(OfferedSkill, Target);
        GivenAnswer(Target);

        var summary = await _loop.RunAsync();

        summary.Learned.ShouldBe(0);
        summary.AlreadyRouted.ShouldBe(1);
        await _phraseLearner.DidNotReceive().LearnAsync(
            Arg.Any<SkillLearningClusterContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _clusters.Received(1).FinishLearningAsync(
            cluster.Id,
            Arg.Is<string>(status => status == SkillLearningClusterStatuses.Dismissed),
            Arg.Is<string?>(kind => kind == null),
            Arg.Is<string?>(reference => reference == null),
            Arg.Is<string>(reason => reason.Contains("Already routed")),
            0,
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A verdict that keeps its class but loses its skill. ReadClassification drops an invented name and
    /// returns the phrase_gap classification with a null target, so the loop reaches LearnPhraseAsync with
    /// nothing to learn for. That branch had no test at all.
    /// </summary>
    [Test]
    public async Task APhraseGapVerdictNamingASkillNobodyOffered_CostsAnAttemptAndSaysWhy()
    {
        var cluster = GivenReadyCluster();
        GivenProbe(OfferedSkill);
        GivenAnswer("a_skill_the_model_invented");

        var summary = await _loop.RunAsync();

        summary.Learned.ShouldBe(0);
        summary.Failed.ShouldBe(1);
        await _phraseLearner.DidNotReceive().LearnAsync(
            Arg.Any<SkillLearningClusterContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _clusters.Received(1).FinishLearningAsync(
            cluster.Id,
            Arg.Is<string>(status => status == SkillLearningClusterStatuses.Ready),
            Arg.Is<string?>(kind => kind == null),
            Arg.Is<string?>(reference => reference == null),
            Arg.Is<string>(reason => reason.Contains("named no existing skill")),
            1,
            Arg.Any<CancellationToken>());
    }

    private SkillLearningCluster GivenReadyCluster()
    {
        var cluster = new SkillLearningCluster
        {
            Id = Guid.NewGuid(),
            IntentExcerpt = Excerpt,
            Locale = "de",
            Status = SkillLearningClusterStatuses.Ready,
            AttemptCount = 0
        };

        _clusters.ListByStatusAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([cluster]);

        return cluster;
    }

    private void GivenCorrection(Guid clusterId, string expectedSkill) =>
        _cases.ListByClusterAsync(clusterId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new SkillLearningCase { ClusterId = clusterId, ExpectedSkill = expectedSkill }]);

    // Never found: a wish whose target is already offered is dismissed before the model is asked at all,
    // and then none of the classification code under test here would run.
    private void GivenProbe(params string[] offered) =>
        _oracle.ProbeAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SkillRoutingProbe(false, offered));

    private void GivenAnswer(string skill) =>
        _provider.Answering(
            $$"""{"cases":[{"index":0,"kind":"phrase_gap","skill":"{{skill}}","reason":"The wording never reaches it."}]}""");
}
