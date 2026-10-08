// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

/// <summary>
/// Drift guard for the veto list: whatever <see cref="SlotConstraintFilter.ViolatesAbsoluteVeto"/> refuses,
/// <see cref="SlotConstraintFilter.IsValidAssignment"/> must refuse as well, on every relaxation rung and with or
/// without slot times. Until 2026-10-08 the two methods kept their own copies of the veto checks (the K15 root
/// cause); since then IsValidAssignment calls ViolatesAbsoluteVeto first, and this test fails the moment a veto
/// is added to one path only.
/// </summary>
[TestFixture]
public class SlotConstraintFilterVetoParityTests
{
    private const string AgentId = "A";
    private const decimal SlotHours = 8m;
    private const int Early = 0;
    private const int Late = 1;

    private static readonly DateOnly Monday = new(2026, 6, 1);
    private static readonly DateOnly Saturday = new(2026, 6, 6);
    private static readonly DateOnly SummerWednesday = new(2026, 7, 1);
    private static readonly Guid Shift = new("00000000-0000-0000-0000-00000000a001");

    private static CoreAgent MakeAgent(bool performsShiftWork = true, bool worksMonday = true, bool worksSaturday = false) => new(
        Id: AgentId,
        CurrentHours: 0,
        GuaranteedHours: 160,
        MaxConsecutiveDays: 6,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        FullTime = 160,
        MaxWorkDays = 5,
        MinRestDays = 2,
        PerformsShiftWork = performsShiftWork,
        WorkOnMonday = worksMonday,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
        WorkOnSaturday = worksSaturday,
    };

    private static CoreWizardContext MakeContext(
        IEnumerable<(string, Guid, DateOnly)>? ineligible = null,
        IReadOnlyList<CoreContractDay>? contractDays = null,
        IReadOnlyList<CoreBreakBlocker>? breaks = null,
        IReadOnlyList<CoreScheduleCommand>? commands = null,
        IReadOnlyList<CoreShiftPreference>? preferences = null,
        IReadOnlyList<CoreRestrictedTimeWindow>? windows = null,
        IReadOnlyList<CoreExistingWorkBlocker>? existingWorks = null,
        IReadOnlyList<CoreLockedWork>? boundaryLockedWorks = null) => new()
    {
        PeriodFrom = new DateOnly(2026, 6, 1),
        PeriodUntil = new DateOnly(2026, 7, 31),
        SchedulingMaxConsecutiveDays = 6,
        SchedulingMaxDailyHours = 10,
        IneligibleAssignments = new HashSet<(string, Guid, DateOnly)>(ineligible ?? []),
        ContractDays = contractDays ?? [],
        BreakBlockers = breaks ?? [],
        ScheduleCommands = commands ?? [],
        ShiftPreferences = preferences ?? [],
        RestrictedTimeWindows = windows ?? [],
        ExistingWorkBlockers = existingWorks ?? [],
        BoundaryLockedWorks = boundaryLockedWorks ?? [],
    };

    private static DateTime At(DateOnly date, int hour) => date.ToDateTime(new TimeOnly(hour, 0));

    private static CoreToken Token(DateOnly date, int startHour, int endHour) => new(
        WorkIds: [],
        ShiftTypeIndex: Early,
        Date: date,
        TotalHours: SlotHours,
        StartAt: At(date, startHour),
        EndAt: At(date, endHour),
        BlockId: Guid.NewGuid(),
        PositionInBlock: 0,
        IsLocked: false,
        LocationContext: null,
        ShiftRefId: Guid.Empty,
        AgentId: AgentId);

    private static TestCaseData Case(
        string name, CoreAgent agent, CoreWizardContext context, CoreToken[] assigned, DateOnly date, int kind, int startHour, int endHour)
        => new TestCaseData(agent, context, assigned, date, kind, At(date, startHour), At(date, endHour)).SetName($"Veto_{name}");

    private static IEnumerable<TestCaseData> VetoCases()
    {
        yield return Case("Qualification", MakeAgent(), MakeContext(ineligible: [(AgentId, Shift, Monday)]), [], Monday, Early, 7, 15);
        yield return Case("ContractDayClosed", MakeAgent(),
            MakeContext(contractDays: [new CoreContractDay(AgentId, Monday, WorksOnDay: false, PerformsShiftWork: true, FullTimeShare: 1, MaximumHoursPerDay: 10, ContractId: Guid.Empty)]),
            [], Monday, Early, 7, 15);
        yield return Case("Weekday", MakeAgent(worksMonday: false), MakeContext(), [], Monday, Early, 7, 15);
        yield return Case("NoShiftWork", MakeAgent(performsShiftWork: false), MakeContext(), [], Monday, Late, 14, 22);
        yield return Case("Break", MakeAgent(), MakeContext(breaks: [new CoreBreakBlocker(AgentId, Monday, Monday, "vacation")]), [], Monday, Early, 7, 15);
        yield return Case("Keyword", MakeAgent(), MakeContext(commands: [new CoreScheduleCommand(AgentId, Monday, ScheduleCommandKeyword.OnlyEarly)]), [], Monday, Late, 14, 22);
        yield return Case("Blacklist", MakeAgent(), MakeContext(preferences: [new CoreShiftPreference(AgentId, Shift, ShiftPreferenceKind.Blacklist)]), [], Monday, Early, 7, 15);
        yield return Case("RestrictedWindow", MakeAgent(),
            MakeContext(windows: [new CoreRestrictedTimeWindow(6, 15, 9, 15, (12 * 60) + 30, 15 * 60, new HashSet<Guid> { Shift })]),
            [], SummerWednesday, Late, 12, 16);
        yield return Case("CollisionWithPlannedToken", MakeAgent(), MakeContext(), [Token(Monday, 7, 15)], Monday, Early, 10, 18);
        yield return Case("CollisionWithExistingWork", MakeAgent(),
            MakeContext(existingWorks: [new CoreExistingWorkBlocker(AgentId, Monday, At(Monday, 6), At(Monday, 12))]),
            [], Monday, Early, 7, 15);
        yield return Case("CollisionWithBoundaryLockedWork", MakeAgent(),
            MakeContext(boundaryLockedWorks: [new CoreLockedWork("W1", AgentId, Monday, Early, SlotHours, At(Monday, 6), At(Monday, 12), Guid.Empty, null)]),
            [], Monday, Early, 7, 15);
        yield return Case("WeeklyRestDays", MakeAgent(worksSaturday: true), MakeContext(),
            [Token(Monday, 7, 15), Token(Monday.AddDays(1), 7, 15), Token(Monday.AddDays(2), 7, 15), Token(Monday.AddDays(3), 7, 15), Token(Monday.AddDays(4), 7, 15)],
            Saturday, Early, 7, 15);
    }

    [TestCaseSource(nameof(VetoCases))]
    public void AbsoluteVeto_ImpliesInvalidAssignment_OnEveryRung(
        CoreAgent agent, CoreWizardContext context, CoreToken[] assigned, DateOnly date, int kind, DateTime start, DateTime end)
    {
        SlotConstraintFilter.ViolatesAbsoluteVeto(agent, date, kind, Shift, context, assigned, start, end)
            .ShouldBeTrue("precondition: the case must actually trigger its absolute veto");

        foreach (var withTimes in new[] { true, false })
        {
            DateTime? slotStart = withTimes ? start : null;
            DateTime? slotEnd = withTimes ? end : null;
            var veto = SlotConstraintFilter.ViolatesAbsoluteVeto(agent, date, kind, Shift, context, assigned, slotStart, slotEnd);
            if (!veto)
            {
                continue;
            }

            foreach (var relaxation in Enum.GetValues<SlotRelaxation>())
            {
                SlotConstraintFilter.IsValidAssignment(agent, date, kind, Shift, SlotHours, context, assigned, slotStart, slotEnd, relaxation)
                    .ShouldBeFalse($"an absolute veto must also refuse the validated path (times: {withTimes}, rung: {relaxation})");
            }
        }
    }

    [Test]
    public void CleanSlot_PassesBothPaths()
    {
        var agent = MakeAgent();
        var context = MakeContext();

        SlotConstraintFilter.ViolatesAbsoluteVeto(agent, Monday, Early, Shift, context, [], At(Monday, 7), At(Monday, 15)).ShouldBeFalse();
        SlotConstraintFilter.IsValidAssignment(agent, Monday, Early, Shift, SlotHours, context, [], At(Monday, 7), At(Monday, 15)).ShouldBeTrue();
    }
}
