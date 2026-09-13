// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the eval regression alert. It compares the latest two COMPLETED runs of the same goldset,
/// model and scorer version - partial runs cover a different population, and a composite scored under
/// different rules is a different number entirely, so mixing either in would make the alert fire on
/// bookkeeping instead of on quality. Runs persisted before the two dimensions existed carry neither, and
/// must produce silence rather than a division by an assumed zero.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Triggers;

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class EvalRegressionDetectorTests
{
    private const string Model = "deepseek-v4-pro";

    private static readonly DateTime Earlier = new(2026, 9, 6, 2, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 9, 13, 2, 0, 0, DateTimeKind.Utc);

    private IEvalRunRepository _evalRuns = null!;
    private EvalRegressionDetector _detector = null!;

    [SetUp]
    public void SetUp()
    {
        _evalRuns = Substitute.For<IEvalRunRepository>();
        _evalRuns.ListRecentFullRunsAsync(
                TurnEvalDefaults.DefaultGoldset, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        _detector = new EvalRegressionDetector(_evalRuns, NullLogger<EvalRegressionDetector>.Instance);
    }

    [Test]
    public void Kind_IsTheEvalRegressionKind()
    {
        _detector.Kind.ShouldBe(AgentTriggerKinds.EvalRegression);
    }

    [Test]
    public async Task WithoutTwoComparableRuns_NothingIsEmitted()
    {
        GivenRuns(Run(Later, retrieval: 0.80, selection: 0.60));

        (await _detector.DetectAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task ADropBelowTheThreshold_IsNotWorthAnAlert()
    {
        GivenRuns(
            Run(Later, retrieval: 0.78, selection: 0.60),
            Run(Earlier, retrieval: 0.80, selection: 0.60));

        (await _detector.DetectAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task ARetrievalDropBeyondTheThreshold_EmitsOneAlert()
    {
        GivenRuns(
            Run(Later, retrieval: 0.70, selection: 0.60),
            Run(Earlier, retrieval: 0.80, selection: 0.60));

        var events = await _detector.DetectAsync();

        events.Count.ShouldBe(1);
        var alert = events[0].ShouldBeOfType<EvalRegressionTriggerEvent>();
        alert.Model.ShouldBe(Model);
        alert.RetrievalDrop.ShouldBe(0.10, 0.000001);
        alert.SelectionDrop.ShouldBe(0.0, 0.000001);
    }

    [Test]
    public async Task ASelectionDropBeyondTheThreshold_EmitsOneAlert()
    {
        GivenRuns(
            Run(Later, retrieval: 0.80, selection: 0.50),
            Run(Earlier, retrieval: 0.80, selection: 0.60));

        var events = await _detector.DetectAsync();

        events.Count.ShouldBe(1);
        events[0].ShouldBeOfType<EvalRegressionTriggerEvent>().SelectionDrop.ShouldBe(0.10, 0.000001);
    }

    [Test]
    public async Task AnImprovement_IsNeverAnAlert()
    {
        GivenRuns(
            Run(Later, retrieval: 0.95, selection: 0.90),
            Run(Earlier, retrieval: 0.80, selection: 0.60));

        (await _detector.DetectAsync()).ShouldBeEmpty();
    }

    // Every row written before Package B carries neither dimension. Reading a missing value as 0.0
    // would report a 100 % collapse on the first run after the deploy.
    [Test]
    public async Task RunsWithoutTheNewDimensions_ProduceSilence()
    {
        GivenRuns(
            Run(Later, retrieval: null, selection: null),
            Run(Earlier, retrieval: null, selection: null));

        (await _detector.DetectAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task UnparsableDimensions_ProduceSilenceInsteadOfAnException()
    {
        var broken = Run(Later, retrieval: 0.10, selection: 0.10);
        broken.DimensionsJson = "{not json";
        GivenRuns(broken, Run(Earlier, retrieval: 0.80, selection: 0.60));

        (await _detector.DetectAsync()).ShouldBeEmpty();
    }

    // Two runs of different models are two measurements, not a trend.
    [Test]
    public async Task RunsOfDifferentModels_AreNotComparedWithEachOther()
    {
        var other = Run(Earlier, retrieval: 0.95, selection: 0.95);
        other.Model = "gpt-54";
        GivenRuns(Run(Later, retrieval: 0.40, selection: 0.40), other);

        (await _detector.DetectAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task RunsOfDifferentScorerVersions_AreNotComparedWithEachOther()
    {
        var older = Run(Earlier, retrieval: 0.95, selection: 0.95);
        older.ScorerVersion = TurnEvalScorer.ScorerVersion - 1;
        GivenRuns(Run(Later, retrieval: 0.40, selection: 0.40), older);

        (await _detector.DetectAsync()).ShouldBeEmpty();
    }

    // Both runs are "full" against the goldset file of their day. When the file grew between them the
    // two cover different populations, and every dimension moves on bookkeeping rather than on quality.
    [Test]
    public async Task RunsOverDifferentlySizedGoldsets_AreNotComparedWithEachOther()
    {
        var older = Run(Earlier, retrieval: 0.95, selection: 0.95);
        older.ItemsTotal = 300;
        GivenRuns(Run(Later, retrieval: 0.40, selection: 0.40), older);

        (await _detector.DetectAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task TheEvent_IsAdminOnlyMediumAndDeepLinksToTheLearningCard()
    {
        GivenRuns(
            Run(Later, retrieval: 0.70, selection: 0.60),
            Run(Earlier, retrieval: 0.80, selection: 0.60));

        var alert = (await _detector.DetectAsync())[0];

        alert.AdminOnly.ShouldBeTrue();
        alert.PlannersOnly.ShouldBeFalse();
        alert.TargetUserId.ShouldBeNull();
        alert.RequiresGroupScope.ShouldBeFalse();
        alert.Severity.ShouldBe(AgentTriggerSeverity.Medium);
        alert.Summary.ShouldBe(ProactiveMessageMarkers.I18nPrefix + ProactiveMessageI18nKeys.EvalRegression);
        alert.ActionRoute.ShouldBe(ProactiveActionRoutes.Settings);
        alert.ActionParams.ShouldNotBeNull();
        alert.ActionParams![ProactiveActionParamKeys.Target]
            .ShouldBe(ProactiveActionRoutes.SettingsTargetKlacksyLearning);
    }

    // One alert per newer run, so a scan every hour does not re-announce the same regression.
    [Test]
    public async Task TheDedupKey_IsTheNewerRunId()
    {
        var newer = Run(Later, retrieval: 0.70, selection: 0.60);
        GivenRuns(newer, Run(Earlier, retrieval: 0.80, selection: 0.60));

        var alert = (await _detector.DetectAsync())[0];

        alert.DedupKey.ShouldBe(newer.Id.ToString());
    }

    [Test]
    public async Task TheFingerprints_MatchTheEventsLedgerSpelling()
    {
        GivenRuns(
            Run(Later, retrieval: 0.70, selection: 0.60),
            Run(Earlier, retrieval: 0.80, selection: 0.60));

        var alert = (await _detector.DetectAsync())[0];
        var fingerprints = await _detector.GetActiveFingerprintsAsync();

        fingerprints.ShouldHaveSingleItem()
            .ShouldBe(AgentConditionLedgerPolicy.FingerprintFor(alert));
    }

    private void GivenRuns(params EvalRun[] runs) =>
        _evalRuns.ListRecentFullRunsAsync(
                TurnEvalDefaults.DefaultGoldset, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(runs);

    private static EvalRun Run(DateTime at, double? retrieval, double? selection) => new()
    {
        Id = Guid.NewGuid(),
        Goldset = TurnEvalDefaults.DefaultGoldset,
        Model = Model,
        Provider = "deepseek",
        CompositeScore = 0.5m,
        ItemsTotal = 335,
        ItemsPassed = 99,
        ScorerVersion = TurnEvalScorer.ScorerVersion,
        IsPartial = false,
        CreateTime = at,
        DimensionsJson = JsonSerializer.Serialize(new TurnEvalDimensions(
            ToolAccuracy: 0.3,
            SlotAccuracy: null,
            NoToolAccuracy: 0.9,
            NameResolutionAccuracy: null,
            AvgLatencyMs: 1200,
            TotalCost: 0.1m,
            ItemsTotal: 335,
            ItemsPassed: 99,
            ItemsExcluded: 0,
            ItemsErrored: 0,
            RetrievalHit: retrieval,
            SelectionHit: selection))
    };
}
