// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for RecipeSkillMarginEvaluator — the shadow-mode recipe-vs-skill margin signal. They pin
/// down: a served skill winning yields a positive margin (no gate), a foreign skill winning yields a
/// negative margin (would gate), a served skill missing from the KNN neighbours is force-included via
/// GetByKeysAsync and still scored, and — the divergence test — a German compound word ("Abwesenheitsart")
/// that the legacy substring net cannot see (it requires a space) is nonetheless caught by the margin.
/// The embedding and reranker are mocked so the scores are fully controlled.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class RecipeSkillMarginEvaluatorTests
{
    private IEmbeddingProvider _embedding = null!;
    private IRerankerProvider _reranker = null!;
    private IKnowledgeIndexRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        _embedding = Substitute.For<IEmbeddingProvider>();
        _embedding.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f, 0.2f, 0.3f });

        _reranker = Substitute.For<IRerankerProvider>();
        _repository = Substitute.For<IKnowledgeIndexRepository>();
        _repository.GetByKeysAsync(
                Arg.Any<IReadOnlyList<(KnowledgeEntryKind Kind, string SourceId)>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<KnowledgeEntry>());
    }

    private RecipeSkillMarginEvaluator BuildEvaluator(bool enabled)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RecipeSkillMarginEvaluator.ShadowModeEnabledConfigKey] = enabled ? "true" : "false"
            })
            .Build();

        return new RecipeSkillMarginEvaluator(
            _embedding, _reranker, _repository, configuration,
            Substitute.For<ILogger<RecipeSkillMarginEvaluator>>());
    }

    private static KnowledgeEntry SkillEntry(string name) =>
        new() { Kind = KnowledgeEntryKind.Skill, SourceId = name, Text = name };

    // The reranker returns each candidate text's score from a lookup keyed on the text (= skill name),
    // so scoring stays deterministic regardless of the internal candidate ordering.
    private void ArrangeRerankerScores(Dictionary<string, double> scoreByText)
    {
        _reranker.ScoreAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var texts = ci.Arg<IReadOnlyList<string>>();
                return texts.Select(t => scoreByText[t]).ToArray();
            });
    }

    private void ArrangeNeighbours(params KnowledgeEntry[] neighbours)
    {
        _repository.FindNearestAsync(
                Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(neighbours);
    }

    private static RecipeSkillMarginRequest Request(
        string message, IReadOnlyCollection<string> servedSkills, bool oldDecision = false,
        IReadOnlyCollection<string>? competing = null, IReadOnlyCollection<string>? userRights = null) =>
        new(message, "some-recipe", "trigger", servedSkills, oldDecision, competing ?? [], userRights);

    [Test]
    public async Task ServedSkillWins_MarginPositive_WouldNotGate()
    {
        ArrangeNeighbours(SkillEntry("create_shift"), SkillEntry("search_shifts"));
        ArrangeRerankerScores(new Dictionary<string, double>
        {
            ["create_shift"] = 0.80,
            ["search_shifts"] = 0.30
        });

        var evaluator = BuildEvaluator(enabled: true);
        var result = await evaluator.EvaluateAndLogAsync(
            Request("create an order", servedSkills: new[] { "create_shift" }, userRights: new[] { Roles.Admin }),
            CancellationToken.None);

        result.ShouldNotBeNull();
        result!.BestServedSkill.ShouldBe("create_shift");
        result.BestForeignSkill.ShouldBe("search_shifts");
        result.Margin.ShouldNotBeNull();
        result.Margin!.Value.ShouldBeGreaterThan(0.0);
        result.WouldGateAtPlaceholderThreshold.ShouldBeFalse();
        result.ServedSkillsNotScored.ShouldBe(0);
    }

    [Test]
    public async Task ForeignSkillWins_MarginNegative_WouldGate()
    {
        ArrangeNeighbours(SkillEntry("create_shift"), SkillEntry("list_absence_types"));
        ArrangeRerankerScores(new Dictionary<string, double>
        {
            ["create_shift"] = 0.20,
            ["list_absence_types"] = 0.70
        });

        var evaluator = BuildEvaluator(enabled: true);
        var result = await evaluator.EvaluateAndLogAsync(
            Request("create a new absence type", servedSkills: new[] { "create_shift" }),
            CancellationToken.None);

        result.ShouldNotBeNull();
        result!.BestForeignSkill.ShouldBe("list_absence_types");
        result.Margin.ShouldNotBeNull();
        result.Margin!.Value.ShouldBeLessThan(0.0);
        result.WouldGateAtPlaceholderThreshold.ShouldBeTrue();
    }

    [Test]
    public async Task ServedSkillNotInKnn_IsForceIncluded_AndScored()
    {
        // KNN surfaces only a foreign skill; the served skill must still be fetched and scored.
        ArrangeNeighbours(SkillEntry("list_absence_types"));
        _repository.GetByKeysAsync(
                Arg.Is<IReadOnlyList<(KnowledgeEntryKind Kind, string SourceId)>>(keys =>
                    keys.Count == 1 && keys[0].Kind == KnowledgeEntryKind.Skill && keys[0].SourceId == "create_shift"),
                Arg.Any<CancellationToken>())
            .Returns(new[] { SkillEntry("create_shift") });
        ArrangeRerankerScores(new Dictionary<string, double>
        {
            ["list_absence_types"] = 0.40,
            ["create_shift"] = 0.60
        });

        var evaluator = BuildEvaluator(enabled: true);
        var result = await evaluator.EvaluateAndLogAsync(
            Request("create an order", servedSkills: new[] { "create_shift" }),
            CancellationToken.None);

        result.ShouldNotBeNull();
        result!.BestServedSkill.ShouldBe("create_shift");
        result.BestServedScore.ShouldBe(0.60);
        result.ServedSkillsNotScored.ShouldBe(0);
        result.Margin.ShouldNotBeNull();
        result.Margin!.Value.ShouldBeGreaterThan(0.0);
    }

    [Test]
    public async Task GermanCompoundWord_LegacyNetMisses_ButMarginCatches_SignalsDiverge()
    {
        const string message = "Neue Abwesenheitsart anlegen";

        // OLD signal: the legacy substring net requires a multi-word phrase (a space). A German compound
        // "Abwesenheitsart" is a single token, so the foreign skill's keyword never matches -> no competitor.
        var foreignSkill = new AgentSkill
        {
            Name = "list_absence_types",
            TriggerKeywords = """["abwesenheitsart"]"""
        };
        var matchedTrigger = new RecipeTrigger
        {
            AllOf = { new RecipeCondition { AnyWordStart = new List<string> { "anleg" } } }
        };

        var legacyCompeting = CompetingSkillIntentDetector.FindCompetingSkillNames(
            new[] { foreignSkill }, message, "de", matchedTrigger,
            matchedRecipeSynonyms: null, servedSkillNames: new[] { "create_shift" });

        legacyCompeting.ShouldBeEmpty();

        // NEW signal: with controlled (mocked) reranker scores where the foreign skill out-scores the
        // served skill, the margin flags the collision. This demonstrates the two signals' plumbing
        // diverges — it does NOT claim the real mE5/cross-encoder would score this phrase this way.
        ArrangeNeighbours(SkillEntry("create_shift"), SkillEntry("list_absence_types"));
        ArrangeRerankerScores(new Dictionary<string, double>
        {
            ["create_shift"] = 0.25,
            ["list_absence_types"] = 0.68
        });

        var evaluator = BuildEvaluator(enabled: true);
        var result = await evaluator.EvaluateAndLogAsync(
            Request(message, servedSkills: new[] { "create_shift" },
                oldDecision: legacyCompeting.Count > 0, competing: legacyCompeting),
            CancellationToken.None);

        result.ShouldNotBeNull();
        result!.WouldGateAtPlaceholderThreshold.ShouldBeTrue();
        result.BestForeignSkill.ShouldBe("list_absence_types");
        result.Margin!.Value.ShouldBeLessThan(0.0);

        // The divergence itself: old signal says "no competitor", margin says "would gate".
        result.OldDetectorDecision.ShouldBeFalse();
        result.WouldGateAtPlaceholderThreshold.ShouldNotBe(result.OldDetectorDecision);
    }

    [Test]
    public async Task ShadowDisabled_DoesNoComputeAndReturnsNull()
    {
        ArrangeNeighbours(SkillEntry("create_shift"));

        var evaluator = BuildEvaluator(enabled: false);
        var result = await evaluator.EvaluateAndLogAsync(
            Request("create an order", servedSkills: new[] { "create_shift" }),
            CancellationToken.None);

        result.ShouldBeNull();
        await _embedding.DidNotReceiveWithAnyArgs().EmbedQueryAsync(default!, default);
        await _repository.DidNotReceiveWithAnyArgs().FindNearestAsync(default!, default!, default, default, default);
        await _reranker.DidNotReceiveWithAnyArgs().ScoreAsync(default!, default!, default);
    }

    [Test]
    public async Task NoUserRights_LogsAdminBypassScope_AndStillComputes()
    {
        ArrangeNeighbours(SkillEntry("create_shift"), SkillEntry("search_shifts"));
        ArrangeRerankerScores(new Dictionary<string, double>
        {
            ["create_shift"] = 0.55,
            ["search_shifts"] = 0.20
        });

        var evaluator = BuildEvaluator(enabled: true);
        var result = await evaluator.EvaluateAndLogAsync(
            Request("create an order", servedSkills: new[] { "create_shift" }, userRights: null),
            CancellationToken.None);

        result.ShouldNotBeNull();
        result!.PermissionScope.ShouldBe("user-rights-unavailable-admin-bypass");
        // Admin bypass so the shadow signal is not silently narrowed by an empty permission set.
        await _repository.Received().FindNearestAsync(
            Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), true, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
