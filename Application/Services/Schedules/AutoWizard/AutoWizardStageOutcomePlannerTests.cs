// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules.AutoWizard;
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
    private static readonly string[] AllStages = ["Wizard", "HolisticHarmonizer"];

    private static AutoWizardStageScenario Stage(string name)
        => new(name, Guid.NewGuid(), Guid.NewGuid(), $"Auto {name}");

    [Test]
    public void OnSuccess_OnlyTheFinalScenarioSurvives()
    {
        var wizard = Stage("Wizard");
        var holistic = Stage("HolisticHarmonizer");

        var toDelete = AutoWizardStageOutcomePlanner.ScenariosToDeleteOnSuccess([wizard, holistic]);

        toDelete.ShouldBe(new[] { wizard.ScenarioId });
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

        var failure = AutoWizardStageOutcomePlanner.BuildFailure(
            Guid.NewGuid(), [wizard], AllStages, "engine crashed");

        failure.FailedStage.ShouldBe("HolisticHarmonizer");
        failure.PartialScenarioId.ShouldBe(wizard.ScenarioId);
        failure.PartialScenarioName.ShouldBe(wizard.Name);
        AutoWizardStageOutcomePlanner.ScenariosToDeleteOnFailure([wizard]).ShouldBeEmpty();
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
        var all = new[] { Stage("Wizard"), Stage("HolisticHarmonizer") };

        var failure = AutoWizardStageOutcomePlanner.BuildFailure(
            Guid.NewGuid(), all, AllStages, "gap report failed");

        failure.FailedStage.ShouldBe("HolisticHarmonizer");
        failure.PartialScenarioId.ShouldBe(all[^1].ScenarioId);
    }

    [Test]
    public void StatusReason_NamesTheStageAndThePartialResult()
    {
        var wizard = Stage("Wizard");
        var failure = AutoWizardStageOutcomePlanner.BuildFailure(
            Guid.NewGuid(), [wizard], AllStages, "engine crashed");

        var reason = AutoWizardStageOutcomePlanner.BuildStatusReason(failure);

        reason.ShouldContain("HolisticHarmonizer");
        reason.ShouldContain("engine crashed");
        reason.ShouldContain(wizard.Name);
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
    public void HolisticStageFailure_FallsBackAndCarriesTheFailure()
    {
        AutoWizardStageOutcomePlanner
            .HolisticStageFallbackReason("engine crashed")
            .ShouldBe("the stage failed and the plan of stage 1 is kept: engine crashed");
    }

    [Test]
    public void PartialResult_PointsAtTheKeptScenario()
    {
        var wizard = Stage("Wizard");
        var jobId = Guid.NewGuid();
        var failure = AutoWizardStageOutcomePlanner.BuildFailure(
            jobId, [wizard], AllStages, "engine crashed");

        var partial = AutoWizardStageOutcomePlanner.BuildPartialResult(failure, elapsedMs: 1234);

        partial.ShouldNotBeNull();
        partial.JobId.ShouldBe(jobId);
        partial.FinalScenarioId.ShouldBe(wizard.ScenarioId);
        partial.FinalScenarioToken.ShouldBe(wizard.Token);
        partial.FinalScenarioName.ShouldBe(wizard.Name);
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
