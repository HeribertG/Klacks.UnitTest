// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Controller;
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
        diagnosis.EligibleAgentCount.ShouldBe(0);
        diagnosis.PlaceableAgentCount.ShouldBe(0);
        diagnosis.EligibilityVetoCounts[Stage0RuleNames.BreakBlocker].ShouldBe(2);
        diagnosis.PlacementVetoCounts.ShouldBeEmpty();
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
        diagnosis.EligibleAgentCount.ShouldBe(2, "Nothing fixed rules out A or B.");
        diagnosis.PlaceableAgentCount.ShouldBe(1, "B could still take the open seat, so the slot is not unsolvable.");
        diagnosis.EligibilityVetoCounts.ShouldBeEmpty();
        diagnosis.PlacementVetoCounts[Stage0RuleNames.MaxDailyHours].ShouldBe(
            1, "A second 8 h seat on the same day breaks A's 10 h daily cap first; each agent counts under its first failing rule.");
    }

    [Test]
    public void Diagnose_AgentBlockedOnlyByItsOwnUnlockedToken_IsEligibleButNotPlaceable()
    {
        var context = Context([Agent("A")], [Shift(ShiftId)]);

        var diagnosis = UnfilledSlotDiagnostics.Diagnose(context, [Token("A", Guid.NewGuid())]).ShouldHaveSingleItem();

        diagnosis.EligibleAgentCount.ShouldBe(1, "A different plan (A moved off the other shift) fills the slot, so it is not unsolvable.");
        diagnosis.PlaceableAgentCount.ShouldBe(0);
        diagnosis.PlacementVetoCounts[Stage0RuleNames.MaxDailyHours].ShouldBe(1);
    }

    [Test]
    public void Diagnose_AgentBlockedByALockedToken_IsNotEligible()
    {
        var context = Context([Agent("A")], [Shift(ShiftId)]);

        var diagnosis = UnfilledSlotDiagnostics.Diagnose(context, [Token("A", Guid.NewGuid(), isLocked: true)]).ShouldHaveSingleItem();

        diagnosis.EligibleAgentCount.ShouldBe(0, "Locked work is a fixed fact the wizard cannot move.");
        diagnosis.EligibilityVetoCounts[Stage0RuleNames.MaxDailyHours].ShouldBe(1);
        diagnosis.PlacementVetoCounts.ShouldBeEmpty();
    }

    [Test]
    public void Diagnose_ContractWeekdayHiddenBehindAPlanRule_StillCountsAsNotEligible()
    {
        var agent = Agent("A") with { WorkOnMonday = false };
        var context = Context([agent], [Shift(ShiftId)]);
        var precedingWeek = Enumerable.Range(1, 6).Select(days => Token("A", Guid.NewGuid(), Day.AddDays(-days))).ToList();

        var diagnosis = UnfilledSlotDiagnostics.Diagnose(context, precedingWeek).ShouldHaveSingleItem();

        diagnosis.EligibleAgentCount.ShouldBe(
            0, "Against the finished plan MaxConsecutiveDays fails first and would hide that A never works on a Monday.");
        diagnosis.EligibilityVetoCounts[Stage0RuleNames.ContractWeekday].ShouldBe(1);
    }

    [Test]
    public void Diagnose_CancelledToken_Throws()
    {
        var context = Context([Agent("A")], [Shift(ShiftId)]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Should.Throw<OperationCanceledException>(
            () => UnfilledSlotDiagnostics.Diagnose(context, [], UnfilledSlotDiagnostics.DefaultMaxSlots, cancellation.Token));
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

    private static CoreToken Token(string agentId, Guid shiftId, DateOnly? date = null, bool isLocked = false) => new(
        WorkIds: [],
        ShiftTypeIndex: 0,
        Date: date ?? Day,
        TotalHours: 8m,
        StartAt: (date ?? Day).ToDateTime(new TimeOnly(8, 0)),
        EndAt: (date ?? Day).ToDateTime(new TimeOnly(16, 0)),
        BlockId: Guid.NewGuid(),
        PositionInBlock: 0,
        IsLocked: isLocked,
        LocationContext: null,
        ShiftRefId: shiftId,
        AgentId: agentId);
}
