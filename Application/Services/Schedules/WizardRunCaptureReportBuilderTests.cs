// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

/// <summary>
/// The report is what a future learner would be judged on, so its arithmetic has to be exactly right.
/// The pivotal rule: a capture without an outcome is still waiting for its seal, not a rejection -
/// counting it as one would make every freshly planned period look bad and teach the learner the wrong
/// lesson. The warm-start split only exists for the engine whose score blob carries the flag.
/// </summary>
[TestFixture]
public sealed class WizardRunCaptureReportBuilderTests
{
    private const string WarmStartJson = """{"context":{"warmStart":true}}""";
    private const string ColdStartJson = """{"context":{"warmStart":false}}""";

    private static readonly DateOnly From = new(2026, 6, 1);
    private static readonly DateOnly Until = new(2026, 6, 30);

    [Test]
    public void Build_NoCaptures_ReturnsAnEmptyReport()
    {
        var report = WizardRunCaptureReportBuilder.Build([], null, 0);

        report.TotalCaptures.ShouldBe(0);
        report.EngineStats.ShouldBeEmpty();
        report.Training.RecentCount.ShouldBe(0);
        report.Training.BestConfigJson.ShouldBeNull();
    }

    [Test]
    public void Build_GroupsByEngineAndApplyKind()
    {
        var captures = new[]
        {
            Capture(WizardEngine.TokenEvolution, WizardApplyKind.Direct, CaptureOutcome.Accepted),
            Capture(WizardEngine.TokenEvolution, WizardApplyKind.Scenario, CaptureOutcome.Accepted),
            Capture(WizardEngine.Harmonizer, WizardApplyKind.Direct, CaptureOutcome.Rejected),
        };

        var report = WizardRunCaptureReportBuilder.Build(captures, null, 0);

        report.TotalCaptures.ShouldBe(3);
        report.EngineStats.Count.ShouldBe(3);
        report.EngineStats.ShouldAllBe(s => s.Total == 1);
    }

    [Test]
    public void Build_AcceptRate_IgnoresCapturesWithoutAnOutcome()
    {
        var captures = new[]
        {
            Capture(outcome: CaptureOutcome.Accepted),
            Capture(outcome: CaptureOutcome.Rejected),
            Capture(outcome: null),
            Capture(outcome: null),
        };

        var stats = WizardRunCaptureReportBuilder.Build(captures, null, 0).EngineStats.Single();

        stats.Open.ShouldBe(2);
        stats.AcceptRate.ShouldBe(0.5);
    }

    [Test]
    public void Build_OnlyOpenCaptures_HasNoAcceptRate()
    {
        var captures = new[] { Capture(outcome: null), Capture(outcome: null) };

        var stats = WizardRunCaptureReportBuilder.Build(captures, null, 0).EngineStats.Single();

        stats.Total.ShouldBe(2);
        stats.AcceptRate.ShouldBeNull();
    }

    [Test]
    public void Build_SupersededAndExpired_CountAgainstTheAcceptRate()
    {
        var captures = new[]
        {
            Capture(outcome: CaptureOutcome.Accepted),
            Capture(outcome: CaptureOutcome.Superseded),
            Capture(outcome: CaptureOutcome.Expired),
            Capture(outcome: CaptureOutcome.Rejected),
        };

        var stats = WizardRunCaptureReportBuilder.Build(captures, null, 0).EngineStats.Single();

        stats.Superseded.ShouldBe(1);
        stats.Expired.ShouldBe(1);
        stats.AcceptRate.ShouldBe(0.25);
    }

    [Test]
    public void Build_ChurnAverages_UseOnlyMeasuredCaptures()
    {
        var captures = new[]
        {
            Capture(correctionChurn: 0.2, eventChurn: 0.4),
            Capture(correctionChurn: 0.4, eventChurn: 0.6),
            Capture(correctionChurn: null, eventChurn: null),
        };

        var stats = WizardRunCaptureReportBuilder.Build(captures, null, 0).EngineStats.Single();

        stats.AvgCorrectionChurn!.Value.ShouldBe(0.3, 1e-9);
        stats.AvgEventChurn!.Value.ShouldBe(0.5, 1e-9);
    }

    [Test]
    public void Build_ChurnHistogram_HasTenBucketsAndClampsTheTop()
    {
        var captures = new[]
        {
            Capture(correctionChurn: 0.0),
            Capture(correctionChurn: 0.05),
            Capture(correctionChurn: 0.95),
            Capture(correctionChurn: 1.0),
        };

        var stats = WizardRunCaptureReportBuilder.Build(captures, null, 0).EngineStats.Single();

        stats.ChurnHistogram.Count.ShouldBe(10);
        stats.ChurnHistogram[0].ShouldBe(2);
        // A churn of exactly 1.0 must land in the last bucket, not out of bounds.
        stats.ChurnHistogram[9].ShouldBe(2);
    }

    [Test]
    public void Build_WarmStartSplit_ComparesTheTwoAcceptRates()
    {
        var captures = new[]
        {
            Capture(outcome: CaptureOutcome.Accepted, subScoreJson: WarmStartJson),
            Capture(outcome: CaptureOutcome.Accepted, subScoreJson: WarmStartJson),
            Capture(outcome: CaptureOutcome.Rejected, subScoreJson: ColdStartJson),
            Capture(outcome: CaptureOutcome.Accepted, subScoreJson: ColdStartJson),
        };

        var stats = WizardRunCaptureReportBuilder.Build(captures, null, 0).EngineStats.Single();

        stats.WarmStartCount.ShouldBe(2);
        stats.WarmStartAcceptRate.ShouldBe(1.0);
        stats.ColdStartAcceptRate.ShouldBe(0.5);
    }

    [Test]
    public void Build_ScoreBlobWithoutTheFlag_StaysOutOfTheSplit()
    {
        // Only the token-evolution schema carries the flag. Treating a harmonizer run as cold-started
        // would invent a comparison the data does not support.
        var captures = new[]
        {
            Capture(outcome: CaptureOutcome.Accepted, subScoreJson: """{"harmonizer":{"fitness":0.8}}"""),
        };

        var stats = WizardRunCaptureReportBuilder.Build(captures, null, 0).EngineStats.Single();

        stats.WarmStartCount.ShouldBe(0);
        stats.WarmStartAcceptRate.ShouldBeNull();
        stats.ColdStartAcceptRate.ShouldBeNull();
    }

    [Test]
    public void Build_MalformedScoreBlob_DoesNotFailTheReport()
    {
        var captures = new[]
        {
            Capture(outcome: CaptureOutcome.Accepted, subScoreJson: "{not json"),
            Capture(outcome: CaptureOutcome.Accepted, subScoreJson: string.Empty),
        };

        var stats = WizardRunCaptureReportBuilder.Build(captures, null, 0).EngineStats.Single();

        stats.Total.ShouldBe(2);
        stats.WarmStartCount.ShouldBe(0);
    }

    [Test]
    public void Build_TrainingSummary_CarriesTheBestFeasibleRun()
    {
        var best = new WizardTrainingRun
        {
            ConfigJson = """{"populationSize":50}""",
            Stage2Score = 0.91,
            DurationMs = 8123,
            Stage0Violations = 0,
        };

        var training = WizardRunCaptureReportBuilder.Build([], best, 17).Training;

        training.RecentCount.ShouldBe(17);
        training.BestConfigJson.ShouldBe("""{"populationSize":50}""");
        training.BestStage2Score.ShouldBe(0.91);
        training.BestDurationMs.ShouldBe(8123);
        training.BestStage0Violations.ShouldBe(0);
    }

    private static WizardRunCapture Capture(
        WizardEngine engine = WizardEngine.TokenEvolution,
        WizardApplyKind applyKind = WizardApplyKind.Direct,
        CaptureOutcome? outcome = null,
        double? correctionChurn = null,
        double? eventChurn = null,
        string subScoreJson = "{}") => new()
    {
        Id = Guid.NewGuid(),
        Engine = engine,
        ApplyKind = applyKind,
        Outcome = outcome,
        CorrectionChurn = correctionChurn,
        EventChurn = eventChurn,
        SubScoreJson = subScoreJson,
        PeriodFrom = From,
        PeriodUntil = Until,
    };
}
