// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for oracle O3. The quote is what the pruner later acts on, so the two things that must be right
/// are what goes into the numerator - successes AND the thumbs-up, because a measurement made of
/// negatives alone cannot tell "helped" from "nobody complained" - and what counts as a recurrence: only
/// occurrences after the artefact went live, since everything before is the evidence that justified
/// learning it.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Learning;

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
public class SkillLearningFitnessServiceTests
{
    private static readonly DateTime Wednesday = new(2026, 8, 26, 14, 0, 0, DateTimeKind.Utc);

    private ILearnedArtefactResolver _resolver = null!;
    private ISkillSelectionTrajectoryRepository _trajectories = null!;
    private ISkillLearningCaseRepository _cases = null!;
    private ISkillLearningFitnessRepository _fitness = null!;
    private SettableTimeProvider _clock = null!;
    private SkillLearningFitnessService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _resolver = Substitute.For<ILearnedArtefactResolver>();
        _trajectories = Substitute.For<ISkillSelectionTrajectoryRepository>();
        _cases = Substitute.For<ISkillLearningCaseRepository>();
        _fitness = Substitute.For<ISkillLearningFitnessRepository>();
        _clock = new SettableTimeProvider(Wednesday);

        _cases.CountSinceAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(0);

        _service = new SkillLearningFitnessService(
            _resolver, _trajectories, _cases, _fitness, _clock,
            Substitute.For<ILogger<SkillLearningFitnessService>>());
    }

    [Test]
    public async Task TheQuote_CountsSuccessesAndThumbsUpAgainstUses()
    {
        GivenPhrase();
        GivenPhraseUsage(new LearnedArtefactUsage(10, 6, 2, 1, Wednesday));

        var snapshot = await MeasureAsync();

        snapshot.Uses.ShouldBe(10);
        snapshot.Successes.ShouldBe(6);
        snapshot.Helpful.ShouldBe(1);
        snapshot.Quote.ShouldBe(0.7m);
    }

    // A turn can be both a success and a thumbs-up. Left uncapped the "quote" would read above one,
    // which is not a quote any more.
    [Test]
    public async Task AQuoteThatWouldExceedOne_IsCapped()
    {
        GivenPhrase();
        GivenPhraseUsage(new LearnedArtefactUsage(4, 4, 0, 4, Wednesday));

        var snapshot = await MeasureAsync();

        snapshot.Quote.ShouldBe(1m);
    }

    [Test]
    public async Task AnArtefactNobodyUsed_HasAQuoteOfZeroRatherThanADivisionByZero()
    {
        GivenPhrase();
        GivenPhraseUsage(LearnedArtefactUsage.None);

        var snapshot = await MeasureAsync();

        snapshot.Uses.ShouldBe(0);
        snapshot.Quote.ShouldBe(0m);
        snapshot.LastUsedAtUtc.ShouldBeNull();
    }

    [Test]
    public async Task TheWindow_ReachesExactlyThirtyDaysBack()
    {
        GivenPhrase();
        GivenPhraseUsage(LearnedArtefactUsage.None);

        await MeasureAsync();

        await _trajectories.Received(1).CountPhraseUsageAsync(
            Arg.Any<string>(),
            Wednesday.AddDays(-SkillLearningDefaults.FitnessWindowDays),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheSnapshot_IsFiledUnderTheMondayOfTheCurrentWeek()
    {
        GivenPhrase();
        GivenPhraseUsage(LearnedArtefactUsage.None);

        var snapshot = await MeasureAsync();

        snapshot.WindowStartUtc.ShouldBe(new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc));
    }

    // Everything the cluster collected before the activation is the evidence that justified learning,
    // not a verdict on the result. Only what came afterwards is a recurrence.
    [Test]
    public async Task Recurrences_AreCountedOnlyFromTheActivationOnwards()
    {
        var activatedAt = Wednesday.AddDays(-3);
        GivenPhrase(activatedAt);
        GivenPhraseUsage(LearnedArtefactUsage.None);
        _cases.CountSinceAsync(Arg.Any<Guid>(), activatedAt, Arg.Any<CancellationToken>()).Returns(2);

        var snapshot = await MeasureAsync();

        snapshot.Recurrences.ShouldBe(2);
        await _cases.Received(1).CountSinceAsync(Arg.Any<Guid>(), activatedAt, Arg.Any<CancellationToken>());
    }

    // An artefact older than the window would otherwise have its recurrences counted back to its
    // activation while its uses only reach thirty days back - two clocks in one quote.
    [Test]
    public async Task ForAnOlderArtefact_RecurrencesStartAtTheWindowRatherThanTheActivation()
    {
        GivenPhrase(Wednesday.AddDays(-90));
        GivenPhraseUsage(LearnedArtefactUsage.None);

        await MeasureAsync();

        await _cases.Received(1).CountSinceAsync(
            Arg.Any<Guid>(),
            Wednesday.AddDays(-SkillLearningDefaults.FitnessWindowDays),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Failures_AreCorrectionsPlusRecurrences()
    {
        GivenPhrase();
        GivenPhraseUsage(new LearnedArtefactUsage(5, 2, 3, 0, Wednesday));
        _cases.CountSinceAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(4);

        var snapshot = await MeasureAsync();

        snapshot.Corrections.ShouldBe(3);
        snapshot.Recurrences.ShouldBe(4);
        snapshot.Failures.ShouldBe(7);
    }

    [Test]
    public async Task ACapability_IsMeasuredThroughTheRecipeThatForcedTheTurn()
    {
        GivenCapability();
        _trajectories
            .CountRecipeUsageAsync(Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new LearnedArtefactUsage(3, 3, 0, 0, Wednesday));

        var snapshot = await MeasureAsync();

        snapshot.Quote.ShouldBe(1m);
        await _trajectories.Received(1).CountRecipeUsageAsync(
            "learned-open-shift-report", Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _trajectories.DidNotReceive().CountPhraseUsageAsync(
            Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    // The snapshot table hangs off the candidate. An artefact whose candidate row is gone has nothing to
    // key a row by, so it is skipped rather than written under a made-up id.
    [Test]
    public async Task AnArtefactWithoutACandidate_IsSkipped()
    {
        _resolver.ListActiveAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
        [
            new LearnedArtefact(
                Guid.NewGuid(), SkillLearningOutcomeKinds.Phrase, "list_clients", Guid.NewGuid(), null,
                Wednesday, false)
        ]);

        var measured = await _service.RunAsync();

        measured.ShouldBe(0);
        await _fitness.DidNotReceive().UpsertAsync(
            Arg.Any<SkillLearningFitness>(), Arg.Any<CancellationToken>());
    }

    private async Task<SkillLearningFitness> MeasureAsync()
    {
        SkillLearningFitness? captured = null;
        await _fitness.UpsertAsync(
            Arg.Do<SkillLearningFitness>(f => captured = f), Arg.Any<CancellationToken>());

        await _service.RunAsync();

        captured.ShouldNotBeNull();
        return captured!;
    }

    private void GivenPhrase(DateTime? activatedAt = null) =>
        _resolver.ListActiveAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
        [
            new LearnedArtefact(
                Guid.NewGuid(),
                SkillLearningOutcomeKinds.Phrase,
                "list_clients",
                Guid.NewGuid(),
                Guid.NewGuid(),
                activatedAt ?? Wednesday.AddDays(-1),
                false)
        ]);

    private void GivenCapability() =>
        _resolver.ListActiveAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
        [
            new LearnedArtefact(
                Guid.NewGuid(),
                SkillLearningOutcomeKinds.Capability,
                "learned-open-shift-report",
                null,
                Guid.NewGuid(),
                Wednesday.AddDays(-1),
                false)
        ]);

    private void GivenPhraseUsage(LearnedArtefactUsage usage) =>
        _trajectories
            .CountPhraseUsageAsync(Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(usage);
}
