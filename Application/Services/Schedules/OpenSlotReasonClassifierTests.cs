// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the static cause the scenario summary names for an open shift slot, checked from the most fundamental
/// cause to the least.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Models.Associations;
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
        OpenSlotReasonClassifier.Classify([], DayOfWeek.Monday, true).ShouldBe(ScenarioSummaryReasonCodes.NoAgentInScope);
    }

    [Test]
    public void NobodyWithAnActiveContract_NamesNoActiveContract()
    {
        OpenSlotReasonClassifier.Classify([Contract(active: false)], DayOfWeek.Monday, true)
            .ShouldBe(ScenarioSummaryReasonCodes.NoActiveContract);
    }

    [Test]
    public void NobodyWorksTheWeekday_NamesTheWeekday()
    {
        OpenSlotReasonClassifier.Classify([Contract(weekend: false)], DayOfWeek.Saturday, true)
            .ShouldBe(ScenarioSummaryReasonCodes.NoAgentWorksOnWeekday);
    }

    [Test]
    public void LateShiftWithoutAnyShiftWorker_NamesShiftWork_ButAnEarlyShiftDoesNot()
    {
        var contracts = new[] { Contract(shiftWork: false) };

        OpenSlotReasonClassifier.Classify(contracts, DayOfWeek.Monday, false)
            .ShouldBe(ScenarioSummaryReasonCodes.NoAgentPerformsShiftWork);
        OpenSlotReasonClassifier.Classify(contracts, DayOfWeek.Monday, true)
            .ShouldBe(ScenarioSummaryReasonCodes.CapacityOrRules);
    }

    [Test]
    public void OneShiftWorkerAmongOthers_LeavesCapacityOrRules()
    {
        OpenSlotReasonClassifier.Classify([Contract(shiftWork: false), Contract()], DayOfWeek.Monday, false)
            .ShouldBe(ScenarioSummaryReasonCodes.CapacityOrRules);
    }
}
