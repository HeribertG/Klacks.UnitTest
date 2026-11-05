// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the static cause the scenario summary names for an open shift slot, checked from the most fundamental
/// cause to the least. Employees without an active contract are candidates with their settings-default data.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class OpenSlotReasonClassifierTests
{
    private static EffectiveContractData Contract(bool active = true, bool shiftWork = true, bool weekend = true) => new()
    {
        HasActiveContract = active,
        PerformsShiftWork = shiftWork,
        WorkOnSaturday = weekend,
        WorkOnSunday = weekend,
    };

    [Test]
    public void NoContracts_NamesNoAgentInScope()
    {
        OpenSlotReasonClassifier.Classify([], DayOfWeek.Monday, ShiftTypeInference.EarlyIndex).ShouldBe(ScenarioSummaryReasonCodes.NoAgentInScope);
    }

    [Test]
    public void EmployeeWithSettingsDefaultsOnATickedWeekday_IsACandidate()
    {
        OpenSlotReasonClassifier.Classify([Contract(active: false)], DayOfWeek.Monday, ShiftTypeInference.EarlyIndex)
            .ShouldBe(ScenarioSummaryReasonCodes.CapacityOrRules);
    }

    [Test]
    public void SettingsDefaultsAreCheckedLikeAContract()
    {
        OpenSlotReasonClassifier.Classify([Contract(active: false, weekend: false)], DayOfWeek.Saturday, ShiftTypeInference.EarlyIndex)
            .ShouldBe(ScenarioSummaryReasonCodes.NoAgentWorksOnWeekday);
        OpenSlotReasonClassifier.Classify([Contract(active: false, shiftWork: false)], DayOfWeek.Monday, ShiftTypeInference.LateIndex)
            .ShouldBe(ScenarioSummaryReasonCodes.NoAgentPerformsShiftWork);
    }

    [Test]
    public void ShiftWorkerWithSettingsDefaults_CoversALateShiftNextToANonShiftWorkerWithAContract()
    {
        OpenSlotReasonClassifier.Classify([Contract(shiftWork: false), Contract(active: false)], DayOfWeek.Monday, ShiftTypeInference.LateIndex)
            .ShouldBe(ScenarioSummaryReasonCodes.CapacityOrRules);
    }

    [Test]
    public void NobodyWorksTheWeekday_NamesTheWeekday()
    {
        OpenSlotReasonClassifier.Classify([Contract(weekend: false)], DayOfWeek.Saturday, ShiftTypeInference.EarlyIndex)
            .ShouldBe(ScenarioSummaryReasonCodes.NoAgentWorksOnWeekday);
    }

    [Test]
    public void LateShiftWithoutAnyShiftWorker_NamesShiftWork_ButAnEarlyShiftDoesNot()
    {
        var contracts = new[] { Contract(shiftWork: false) };

        OpenSlotReasonClassifier.Classify(contracts, DayOfWeek.Monday, ShiftTypeInference.LateIndex)
            .ShouldBe(ScenarioSummaryReasonCodes.NoAgentPerformsShiftWork);
        OpenSlotReasonClassifier.Classify(contracts, DayOfWeek.Monday, ShiftTypeInference.EarlyIndex)
            .ShouldBe(ScenarioSummaryReasonCodes.CapacityOrRules);
    }

    [Test]
    public void OneShiftWorkerAmongOthers_LeavesCapacityOrRules()
    {
        OpenSlotReasonClassifier.Classify([Contract(shiftWork: false), Contract()], DayOfWeek.Monday, ShiftTypeInference.LateIndex)
            .ShouldBe(ScenarioSummaryReasonCodes.CapacityOrRules);
    }
}
