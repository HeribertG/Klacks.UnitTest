// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Diagnostics;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Diagnostics;

[TestFixture]
public class UnfilledSlotDiagnosticsTests
{
    private static readonly DateOnly Day = new(2026, 4, 20);
    private static readonly Guid ShiftId = Guid.NewGuid();

    [Test]
    public void Diagnose_TwoAgentsOnVacation_ReportsTheSlotAsUnsolvableWithTwoBreakVetoes()
    {
        var context = Context(
            [Agent("A"), Agent("B")],
            [Shift(ShiftId)],
            breaks: [new CoreBreakBlocker("A", Day, Day, "Vacation", 8m), new CoreBreakBlocker("B", Day, Day, "Vacation", 8m)]);

        var diagnoses = UnfilledSlotDiagnostics.Diagnose(context, []);

        var diagnosis = diagnoses.ShouldHaveSingleItem();
        diagnosis.ShiftId.ShouldBe(ShiftId);
        diagnosis.Date.ShouldBe(Day);
        diagnosis.MissingSeats.ShouldBe(1);
        diagnosis.FeasibleAgentCount.ShouldBe(0);
        diagnosis.VetoCounts["BreakBlocker"].ShouldBe(2);
    }

    [Test]
    public void Diagnose_FullyStaffedSlot_ReportsNothing()
    {
        var context = Context([Agent("A")], [Shift(ShiftId)]);

        UnfilledSlotDiagnostics.Diagnose(context, [Token("A", ShiftId)]).ShouldBeEmpty();
    }

    [Test]
    public void Diagnose_TwoSeatSlotWithOneAssignment_CountsTheMissingSeatAndTheStillFeasibleAgent()
    {
        var context = Context([Agent("A"), Agent("B")], [Shift(ShiftId), Shift(ShiftId)]);

        var diagnosis = UnfilledSlotDiagnostics.Diagnose(context, [Token("A", ShiftId)]).ShouldHaveSingleItem();

        diagnosis.MissingSeats.ShouldBe(1, "Two rows with the same id and date are two seats.");
        diagnosis.FeasibleAgentCount.ShouldBe(1, "B could still take the open seat, so the slot is not unsolvable.");
        diagnosis.VetoCounts.Values.Sum().ShouldBe(1, "Only A, who already holds the other seat, is ruled out.");
        diagnosis.VetoCounts["MaxDailyHours"].ShouldBe(
            1, "A second 8 h seat on the same day breaks A's 10 h daily cap first; each agent counts under its first failing rule.");
    }

    [Test]
    public void Diagnose_RespectsTheSlotLimit()
    {
        var context = Context([Agent("A")], [Shift(Guid.NewGuid()), Shift(Guid.NewGuid()), Shift(Guid.NewGuid())]);

        UnfilledSlotDiagnostics.Diagnose(context, [], maxSlots: 2).Count.ShouldBe(2);
    }

    private static CoreWizardContext Context(
        IReadOnlyList<CoreAgent> agents,
        IReadOnlyList<CoreShift> shifts,
        IReadOnlyList<CoreBreakBlocker>? breaks = null) => new()
    {
        PeriodFrom = Day,
        PeriodUntil = Day,
        Agents = agents,
        Shifts = shifts,
        BreakBlockers = breaks ?? [],
        SchedulingMaxConsecutiveDays = 6,
    };

    private static CoreAgent Agent(string id) => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: 0,
        MaxConsecutiveDays: 6,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        PerformsShiftWork = true,
        WorkOnMonday = true,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };

    private static CoreShift Shift(Guid id) =>
        new(id.ToString(), "FD", Day.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 1, 0);

    private static CoreToken Token(string agentId, Guid shiftId) => new(
        WorkIds: [],
        ShiftTypeIndex: 0,
        Date: Day,
        TotalHours: 8m,
        StartAt: Day.ToDateTime(new TimeOnly(8, 0)),
        EndAt: Day.ToDateTime(new TimeOnly(16, 0)),
        BlockId: Guid.NewGuid(),
        PositionInBlock: 0,
        IsLocked: false,
        LocationContext: null,
        ShiftRefId: shiftId,
        AgentId: agentId);
}
