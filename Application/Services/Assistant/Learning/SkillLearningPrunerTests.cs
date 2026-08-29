// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the pruner. Two of its rules are easy to get subtly wrong and both are checked here from
/// both sides.
/// The first is the idle clock. A freshly learned artefact has no last use at all, and reading a missing
/// last use as "infinitely old" would have the very first pruning pass delete everything learned the day
/// before - so the clock starts at the activation.
/// The second is the observation floor: a poor quote may only retire an artefact once there is enough
/// evidence to call it poor, otherwise one unlucky turn unlearns faster than the loop learns.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Learning;

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Services.Assistant.Learning;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SkillLearningPrunerTests
{
    private const string SkillName = "list_clients";
    private const string RecipeName = "learned-open-shift-report";

    private static readonly DateTime Now = new(2026, 8, 29, 9, 0, 0, DateTimeKind.Utc);

    private ILearnedArtefactResolver _resolver = null!;
    private ISkillLearningFitnessRepository _fitness = null!;
    private ISkillPhraseRepository _phrases = null!;
    private IAgentRecipeRepository _recipes = null!;
    private ISkillLearningClusterRepository _clusters = null!;
    private ISkillLearningCandidateRepository _candidates = null!;
    private ISkillCatalogRefresher _refresher = null!;
    private SkillLearningPruner _pruner = null!;

    private Guid _clusterId;
    private Guid _candidateId;
    private Guid _phraseId;

    [SetUp]
    public void SetUp()
    {
        _clusterId = Guid.NewGuid();
        _candidateId = Guid.NewGuid();
        _phraseId = Guid.NewGuid();

        _resolver = Substitute.For<ILearnedArtefactResolver>();
        _fitness = Substitute.For<ISkillLearningFitnessRepository>();
        _phrases = Substitute.For<ISkillPhraseRepository>();
        _recipes = Substitute.For<IAgentRecipeRepository>();
        _clusters = Substitute.For<ISkillLearningClusterRepository>();
        _candidates = Substitute.For<ISkillLearningCandidateRepository>();
        _refresher = Substitute.For<ISkillCatalogRefresher>();

        var options = Substitute.For<ISkillLearningOptionsProvider>();
        options.GetAsync(Arg.Any<CancellationToken>()).Returns(
            new SkillLearningOptions(3, 2, SkillLearningDefaults.PruneDays, 90));

        _pruner = new SkillLearningPruner(
            _resolver, _fitness, _phrases, _recipes, _clusters, _candidates, options, _refresher,
            new SettableTimeProvider(Now), Substitute.For<ILogger<SkillLearningPruner>>());
    }

    [Test]
    public async Task AnArtefactNobodyUsedForThePruningWindow_IsRetired()
    {
        GivenPhrase(activatedAt: Now.AddDays(-SkillLearningDefaults.PruneDays - 1));

        var retired = await _pruner.RunAsync();

        retired.ShouldBe(1);
        await _phrases.Received(1).SetStatusAsync(
            _phraseId, SkillPhraseStatuses.Rejected, Arg.Any<CancellationToken>());
    }

    // The regression this rule exists for: without the activation clock a phrase learned yesterday has a
    // null last use, and a naive comparison would treat that as older than any threshold.
    [Test]
    public async Task AFreshlyLearnedArtefactNobodyHasUsedYet_SurvivesItsFirstPruningPass()
    {
        GivenPhrase(activatedAt: Now.AddDays(-1));

        var retired = await _pruner.RunAsync();

        retired.ShouldBe(0);
        await _phrases.DidNotReceive().SetStatusAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnOldArtefactSomebodyStillUses_Survives()
    {
        GivenPhrase(activatedAt: Now.AddDays(-200));
        GivenFitness(uses: 9, quote: 0.9m, lastUsed: Now.AddDays(-2));

        var retired = await _pruner.RunAsync();

        retired.ShouldBe(0);
    }

    [Test]
    public async Task AnArtefactWithEnoughUsesAndAPoorQuote_IsRetired()
    {
        GivenPhrase(activatedAt: Now.AddDays(-5));
        GivenFitness(uses: SkillLearningDefaults.PruneMinUsesForQuote, quote: 0.4m, lastUsed: Now.AddDays(-1));

        var retired = await _pruner.RunAsync();

        retired.ShouldBe(1);
    }

    [Test]
    public async Task APoorQuoteBelowTheObservationFloor_DoesNotRetireAnything()
    {
        GivenPhrase(activatedAt: Now.AddDays(-5));
        GivenFitness(
            uses: SkillLearningDefaults.PruneMinUsesForQuote - 1, quote: 0m, lastUsed: Now.AddDays(-1));

        var retired = await _pruner.RunAsync();

        retired.ShouldBe(0);
    }

    [Test]
    public async Task AQuoteExactlyAtTheThreshold_IsGoodEnoughToSurvive()
    {
        GivenPhrase(activatedAt: Now.AddDays(-5));
        GivenFitness(uses: 20, quote: SkillLearningDefaults.PruneMinQuote, lastUsed: Now.AddDays(-1));

        var retired = await _pruner.RunAsync();

        retired.ShouldBe(0);
    }

    // Rejected rather than deleted: the row keeps its unique key occupied, which is what stops a later
    // round from proposing the identical wording again.
    [Test]
    public async Task ARetiredPhrase_IsRejectedRatherThanDeleted()
    {
        GivenPhrase(activatedAt: Now.AddDays(-SkillLearningDefaults.PruneDays - 1));

        await _pruner.RunAsync();

        await _phrases.Received(1).SetStatusAsync(
            _phraseId, SkillPhraseStatuses.Rejected, Arg.Any<CancellationToken>());
        await _candidates.Received(1).RetireAsync(
            _candidateId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _clusters.Received(1).FinishRetirementAsync(
            _clusterId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ARetiredCapability_IsDisabledRatherThanDeleted()
    {
        var recipe = new AgentRecipe { Id = Guid.NewGuid(), Name = RecipeName, IsEnabled = true };
        _recipes.GetByNameAsync(RecipeName, Arg.Any<CancellationToken>()).Returns(recipe);
        GivenCapability(activatedAt: Now.AddDays(-SkillLearningDefaults.PruneDays - 1));

        await _pruner.RunAsync();

        recipe.IsEnabled.ShouldBeFalse();
        await _recipes.Received(1).UpdateAsync(recipe, Arg.Any<CancellationToken>());
        await _recipes.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ThePass_RebuildsTheCatalogueOnlyWhenSomethingWasActuallyRetired()
    {
        GivenPhrase(activatedAt: Now.AddDays(-1));

        await _pruner.RunAsync();

        await _refresher.DidNotReceive().RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ThePass_RebuildsTheCatalogueOnceAfterRetiring()
    {
        GivenPhrase(activatedAt: Now.AddDays(-SkillLearningDefaults.PruneDays - 1));

        await _pruner.RunAsync();

        await _refresher.Received(1).RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private void GivenPhrase(DateTime activatedAt) =>
        _resolver.ListActiveAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
        [
            new LearnedArtefact(
                _clusterId, SkillLearningOutcomeKinds.Phrase, SkillName, _phraseId, _candidateId,
                activatedAt, false)
        ]);

    private void GivenCapability(DateTime activatedAt) =>
        _resolver.ListActiveAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
        [
            new LearnedArtefact(
                _clusterId, SkillLearningOutcomeKinds.Capability, RecipeName, null, _candidateId,
                activatedAt, false)
        ]);

    private void GivenFitness(int uses, decimal quote, DateTime lastUsed) =>
        _fitness.GetLatestAsync(_candidateId, Arg.Any<CancellationToken>()).Returns(
            new SkillLearningFitness { Uses = uses, Quote = quote, LastUsedAtUtc = lastUsed });
}
