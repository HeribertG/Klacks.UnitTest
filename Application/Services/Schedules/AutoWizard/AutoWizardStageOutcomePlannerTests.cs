// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules.AutoWizard;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules.AutoWizard;

/// <summary>
/// The chain used to leave every intermediate scenario behind on success and to throw away minutes of
/// compute on failure. These rules decide what survives.
/// </summary>
[TestFixture]
public sealed class AutoWizardStageOutcomePlannerTests
{
    private static readonly string[] AllStages = ["Wizard", "Harmonizer", "HolisticHarmonizer"];

    private static AutoWizardStageScenario Stage(string name)
        => new(name, Guid.NewGuid(), Guid.NewGuid(), $"Auto {name}");

    [Test]
    public void OnSuccess_OnlyTheFinalScenarioSurvives()
    {
        var wizard = Stage("Wizard");
        var harmonizer = Stage("Harmonizer");
        var holistic = Stage("HolisticHarmonizer");

        var toDelete = AutoWizardStageOutcomePlanner.ScenariosToDeleteOnSuccess([wizard, harmonizer, holistic]);

        toDelete.ShouldBe(new[] { wizard.ScenarioId, harmonizer.ScenarioId });
    }

    [Test]
    public void OnSuccess_SingleScenario_IsKept()
    {
        AutoWizardStageOutcomePlanner.ScenariosToDeleteOnSuccess([Stage("Wizard")]).ShouldBeEmpty();
    }

    [Test]
    public void OnSuccess_NothingProduced_IsANoOp()
    {
        AutoWizardStageOutcomePlanner.ScenariosToDeleteOnSuccess([]).ShouldBeEmpty();
    }

    [Test]
    public void OnFailure_TheLastResultIsKeptAsPartial()
    {
        var wizard = Stage("Wizard");
        var harmonizer = Stage("Harmonizer");

        var failure = AutoWizardStageOutcomePlanner.BuildFailure(
            Guid.NewGuid(), [wizard, harmonizer], AllStages, "model unreachable");

        // Two stages produced a scenario, so the third is where it stopped.
        failure.FailedStage.ShouldBe("HolisticHarmonizer");
        failure.PartialScenarioId.ShouldBe(harmonizer.ScenarioId);
        failure.PartialScenarioName.ShouldBe(harmonizer.Name);
        AutoWizardStageOutcomePlanner.ScenariosToDeleteOnFailure([wizard, harmonizer])
            .ShouldBe(new[] { wizard.ScenarioId });
    }

    [Test]
    public void OnFailure_InTheFirstStage_ReportsNoPartialResult()
    {
        var failure = AutoWizardStageOutcomePlanner.BuildFailure(
            Guid.NewGuid(), [], AllStages, "Wizard stage did not produce a result.");

        failure.FailedStage.ShouldBe("Wizard");
        failure.PartialScenarioId.ShouldBeNull();
        failure.PartialScenarioToken.ShouldBeNull();
        failure.PartialScenarioName.ShouldBeNull();
    }

    [Test]
    public void OnFailure_AfterTheLastStage_StaysOnThatStage()
    {
        var all = new[] { Stage("Wizard"), Stage("Harmonizer"), Stage("HolisticHarmonizer") };

        var failure = AutoWizardStageOutcomePlanner.BuildFailure(
            Guid.NewGuid(), all, AllStages, "gap report failed");

        failure.FailedStage.ShouldBe("HolisticHarmonizer");
        failure.PartialScenarioId.ShouldBe(all[^1].ScenarioId);
    }

    [Test]
    public void StatusReason_NamesTheStageAndThePartialResult()
    {
        var harmonizer = Stage("Harmonizer");
        var failure = AutoWizardStageOutcomePlanner.BuildFailure(
            Guid.NewGuid(), [Stage("Wizard"), harmonizer], AllStages, "model unreachable");

        var reason = AutoWizardStageOutcomePlanner.BuildStatusReason(failure);

        reason.ShouldContain("HolisticHarmonizer");
        reason.ShouldContain("model unreachable");
        reason.ShouldContain(harmonizer.Name);
    }

    [Test]
    public void StatusReason_WithoutPartialResult_MentionsOnlyTheStage()
    {
        var failure = AutoWizardStageOutcomePlanner.BuildFailure(
            Guid.NewGuid(), [], AllStages, "Wizard stage did not produce a result.");

        var reason = AutoWizardStageOutcomePlanner.BuildStatusReason(failure);

        reason.ShouldContain("Wizard");
        reason.ShouldNotContain("partial result");
    }

    [Test]
    public void HolisticStageFailure_WithMissingPrerequisite_DegradesToSkip()
    {
        AutoWizardStageOutcomePlanner
            .ShouldSkipHolisticStageAfterFailure(HolisticHarmonizerReadiness.NotReady("model is text-only"))
            .ShouldBeTrue();
    }

    [Test]
    public void HolisticStageFailure_WithPrerequisiteMet_StaysAFailure()
    {
        AutoWizardStageOutcomePlanner
            .ShouldSkipHolisticStageAfterFailure(HolisticHarmonizerReadiness.Ready())
            .ShouldBeFalse();
    }

    [Test]
    public void PartialResult_PointsAtTheKeptScenario()
    {
        var harmonizer = Stage("Harmonizer");
        var jobId = Guid.NewGuid();
        var failure = AutoWizardStageOutcomePlanner.BuildFailure(
            jobId, [Stage("Wizard"), harmonizer], AllStages, "engine crashed");

        var partial = AutoWizardStageOutcomePlanner.BuildPartialResult(failure, elapsedMs: 1234);

        partial.ShouldNotBeNull();
        partial.JobId.ShouldBe(jobId);
        partial.FinalScenarioId.ShouldBe(harmonizer.ScenarioId);
        partial.FinalScenarioToken.ShouldBe(harmonizer.Token);
        partial.FinalScenarioName.ShouldBe(harmonizer.Name);
        partial.ElapsedMs.ShouldBe(1234);
        partial.HarmonizationSkipped.ShouldBeFalse();
    }

    [Test]
    public void PartialResult_WithoutAnyScenario_IsNull()
    {
        var failure = AutoWizardStageOutcomePlanner.BuildFailure(Guid.NewGuid(), [], AllStages, "no agents");

        AutoWizardStageOutcomePlanner.BuildPartialResult(failure, elapsedMs: 10).ShouldBeNull();
    }

    [Test]
    public void HarmonizationSkippedNote_NamesTheReason()
    {
        AutoWizardStageOutcomePlanner.BuildHarmonizationSkippedNote("no model configured")
            .ShouldContain("no model configured");
    }
}
