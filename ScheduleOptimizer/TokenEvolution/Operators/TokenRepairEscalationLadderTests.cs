// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Constraints;
using Klacks.ScheduleOptimizer.TokenEvolution.Diagnostics;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Operators;

/// <summary>
/// Pins the coverage escalation of the repair operator as a ladder of three rungs. The operator asks
/// for the strictest candidate set first and only widens where the slot would otherwise stay empty,
/// so a slot the strict rung can staff is staffed exactly as before, while a slot that can only be
/// taken on the sixth consecutive day is still taken — coverage is the highest rule of the
/// specification, the 5/2 package ideal is far below it. What no rung buys is a hard rule.
/// <para>
/// The fixtures hold a single agent on purpose: with no second agent the one-move relocation escape
/// can never find a receiver, so the rung under test is the only thing that can answer the slot. The
/// prefix of the six-day case is locked for the same reason — locked tokens are not offered as
/// blockers, which closes the relocation escape from the other side as well.
/// </para>
/// </summary>
[TestFixture]
public sealed class TokenRepairEscalationLadderTests
{
    private const string AgentId = "A";

    private const int SoftCapDays = 5;

    private const int HardCapDays = 6;

    private const int RestDays = 2;

    private const decimal SlotHours = 8;

    private const double SlotHoursAsDouble = 8;

    private const string SlotStart = "08:00";

    private const string SlotEnd = "16:00";

    private const string DateFormat = "yyyy-MM-dd";

    private const string DirectFill = "direct";

    private const string EscalationLinePrefix = "ESCALATED gen";

    private const string EscalatedStageMarker = ".escalated ";

    private const string RepairStage = "mutation.repair";

    private const string EscalationBody = "A 2026-06-06 direct";

    private const int TracedGeneration = 7;

    private static readonly DateOnly FirstDay = new(2026, 6, 1);

    [Test]
    public void TheRestDayRung_FillsTheSlotAndNeverReportsAnEscalation()
    {
        var thirdDay = FirstDay.AddDays(2);
        var shifts = new List<CoreShift> { Shift(FirstDay), Shift(thirdDay) };
        var context = Context(shifts, MakeAgent());
        var scenario = new CoreScenario
        {
            Id = "s",
            Tokens = [Token(FirstDay, shifts[0])],
        };

        Accepts(context, scenario, thirdDay, shifts[1], SlotRelaxation.None).ShouldBeFalse();
        Accepts(context, scenario, thirdDay, shifts[1], SlotRelaxation.RestDaysOnly).ShouldBeTrue();

        var escalations = new List<string>();
        var result = new TokenRepair(new TokenConstraintChecker())
            .Apply(new TokenOperatorContext(scenario, null, context, new Random(0)), escalations.Add);

        result.Tokens.Count.ShouldBe(2);
        result.Tokens.ShouldContain(t => t.Date == thirdDay && t.AgentId == AgentId);
        escalations.ShouldBeEmpty();
        OverlongBlockTrace.Overlong(result, context).ShouldBeEmpty();
    }

    [Test]
    public void TheWidestRung_TakesTheSixthConsecutiveDayNoOtherRungCanTake()
    {
        var sixthDay = FirstDay.AddDays(SoftCapDays);
        var context = LockedPrefixContext(HardCapDays, out var scenario, out var sixthShift);
        var sixthSlot = context.Shifts[SoftCapDays];

        Accepts(context, scenario, sixthDay, sixthSlot, SlotRelaxation.None).ShouldBeFalse();
        Accepts(context, scenario, sixthDay, sixthSlot, SlotRelaxation.RestDaysOnly).ShouldBeFalse();
        Accepts(context, scenario, sixthDay, sixthSlot, SlotRelaxation.All).ShouldBeTrue();

        var escalations = new List<string>();
        var result = new TokenRepair(new TokenConstraintChecker())
            .Apply(new TokenOperatorContext(scenario, null, context, new Random(0)), escalations.Add);

        result.Tokens.Count.ShouldBe(HardCapDays);
        result.Tokens.ShouldContain(t => t.ShiftRefId == sixthShift && t.AgentId == AgentId && !t.IsLocked);
        escalations.ShouldHaveSingleItem();
        escalations[0].ShouldBe(
            $"{AgentId} {sixthDay.ToString(DateFormat, CultureInfo.InvariantCulture)} {DirectFill}");

        var overlong = OverlongBlockTrace.Overlong(result, context);
        overlong.ShouldHaveSingleItem();
        overlong.Single().ShouldContain($"len={(SoftCapDays + 1).ToString(CultureInfo.InvariantCulture)}");
    }

    [Test]
    public void TheWidestRung_LeavesTheSlotEmptyWhenAHardRuleVetoesIt()
    {
        var context = LockedPrefixContext(SoftCapDays, out var scenario, out _);

        var escalations = new List<string>();
        var result = new TokenRepair(new TokenConstraintChecker())
            .Apply(new TokenOperatorContext(scenario, null, context, new Random(0)), escalations.Add);

        result.Tokens.Count.ShouldBe(SoftCapDays);
        escalations.ShouldBeEmpty();
        OverlongBlockTrace.Overlong(result, context).ShouldBeEmpty();
    }

    [Test]
    public void TheLadderIsDeterministic()
    {
        var context = LockedPrefixContext(HardCapDays, out var scenario, out _);
        var repair = new TokenRepair(new TokenConstraintChecker());

        var first = new List<string>();
        var firstResult = repair.Apply(
            new TokenOperatorContext(scenario, null, context, new Random(0)), first.Add);

        var second = new List<string>();
        var secondResult = repair.Apply(
            new TokenOperatorContext(scenario, null, context, new Random(0)), second.Add);

        Identity(firstResult).ShouldBe(Identity(secondResult));
        first.ShouldBe(second);
        first.ShouldNotBeEmpty();
    }

    /// <summary>
    /// The escalated fill is attributable: the repair operator names the slot, the evolution loop
    /// names the step, and the trace joins both into the producer label the overlong-package
    /// diagnosis reads.
    /// </summary>
    [Test]
    public void TheProducerLabelNamesTheStepThatEscalated()
    {
        var lines = new List<string>();

        var sink = RepairEscalationTrace.For(lines.Add, TracedGeneration, RepairStage);
        sink.ShouldNotBeNull();
        sink!(EscalationBody);

        lines.ShouldHaveSingleItem();
        lines[0].ShouldBe(
            $"{EscalationLinePrefix}{TracedGeneration.ToString(CultureInfo.InvariantCulture)}."
            + $"{RepairStage}{EscalatedStageMarker}{EscalationBody}");
        RepairEscalationTrace.For(null, TracedGeneration, RepairStage).ShouldBeNull();
    }

    /// <summary>
    /// Whether the single agent may take the given slot on the given rung, measured on the plan the
    /// repair operator sees. Makes the rung under test explicit instead of inferring it from the fill.
    /// </summary>
    /// <param name="context">Wizard context of the fixture</param>
    /// <param name="scenario">Plan the operator starts from</param>
    /// <param name="date">Day of the slot</param>
    /// <param name="slot">Slot definition</param>
    /// <param name="relaxation">Rung to measure</param>
    private static bool Accepts(
        CoreWizardContext context,
        CoreScenario scenario,
        DateOnly date,
        CoreShift slot,
        SlotRelaxation relaxation)
        => SlotConstraintFilter.IsValidAssignment(
            context.Agents[0],
            date,
            0,
            Guid.Parse(slot.Id),
            SlotHours,
            context,
            scenario.Tokens,
            date.ToDateTime(new TimeOnly(8, 0)),
            date.ToDateTime(new TimeOnly(16, 0)),
            relaxation);

    private static List<string> Identity(CoreScenario scenario) => scenario.Tokens
        .Select(t => string.Join(
            '|',
            t.AgentId,
            t.Date.ToString(DateFormat, CultureInfo.InvariantCulture),
            t.ShiftRefId.ToString(),
            t.TotalHours.ToString(CultureInfo.InvariantCulture)))
        .OrderBy(key => key, StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// A single agent standing on a locked five-day package with an open slot on the sixth day. The
    /// package is exactly the block ideal, so only the widest rung can answer the sixth day, and the
    /// hard consecutive cap decides whether even that rung may.
    /// </summary>
    /// <param name="maxConsecutiveDays">Hard cap of the agent and of the scheduling defaults</param>
    /// <param name="scenario">Plan holding the five locked tokens</param>
    /// <param name="sixthShift">Reference of the open slot on the sixth day</param>
    private static CoreWizardContext LockedPrefixContext(
        int maxConsecutiveDays, out CoreScenario scenario, out Guid sixthShift)
    {
        var shifts = new List<CoreShift>();
        for (var offset = 0; offset <= SoftCapDays; offset++)
        {
            shifts.Add(Shift(FirstDay.AddDays(offset)));
        }

        sixthShift = Guid.Parse(shifts[SoftCapDays].Id);
        var lockedWorks = shifts
            .Take(SoftCapDays)
            .Select((shift, index) => LockedWork(FirstDay.AddDays(index), shift))
            .ToList();
        var context = Context(shifts, MakeAgent(maxConsecutiveDays), maxConsecutiveDays, lockedWorks);

        scenario = new CoreScenario
        {
            Id = "s",
            Tokens = LockedTokens(context),
        };

        return context;
    }

    private static List<CoreToken> LockedTokens(CoreWizardContext context) => context.LockedWorks
        .Select(work => new CoreToken(
            WorkIds: [work.WorkId],
            ShiftTypeIndex: work.ShiftTypeIndex,
            Date: work.Date,
            TotalHours: work.TotalHours,
            StartAt: work.StartAt,
            EndAt: work.EndAt,
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: true,
            LocationContext: null,
            ShiftRefId: work.ShiftRefId,
            AgentId: work.AgentId))
        .ToList();

    private static CoreLockedWork LockedWork(DateOnly date, CoreShift shift) => new(
        WorkId: shift.Id,
        AgentId: AgentId,
        Date: date,
        ShiftTypeIndex: 0,
        TotalHours: SlotHours,
        StartAt: date.ToDateTime(new TimeOnly(8, 0)),
        EndAt: date.ToDateTime(new TimeOnly(16, 0)),
        ShiftRefId: Guid.Parse(shift.Id),
        LocationContext: null);

    private static CoreToken Token(DateOnly date, CoreShift shift) => new(
        WorkIds: [],
        ShiftTypeIndex: 0,
        Date: date,
        TotalHours: SlotHours,
        StartAt: date.ToDateTime(new TimeOnly(8, 0)),
        EndAt: date.ToDateTime(new TimeOnly(16, 0)),
        BlockId: Guid.NewGuid(),
        PositionInBlock: 0,
        IsLocked: false,
        LocationContext: null,
        ShiftRefId: Guid.Parse(shift.Id),
        AgentId: AgentId);

    private static CoreShift Shift(DateOnly date) => new(
        Guid.NewGuid().ToString(),
        date.ToString(DateFormat, CultureInfo.InvariantCulture),
        date.ToString(DateFormat, CultureInfo.InvariantCulture),
        SlotStart,
        SlotEnd,
        SlotHoursAsDouble,
        1,
        0);

    private static CoreWizardContext Context(
        IReadOnlyList<CoreShift> shifts,
        CoreAgent agent,
        int maxConsecutiveDays = HardCapDays,
        IReadOnlyList<CoreLockedWork>? lockedWorks = null)
        => new()
        {
            PeriodFrom = FirstDay,
            PeriodUntil = FirstDay.AddDays(SoftCapDays),
            Agents = [agent],
            Shifts = shifts,
            LockedWorks = lockedWorks ?? [],
            SchedulingMaxConsecutiveDays = maxConsecutiveDays,
            SchedulingMaxDailyHours = 10,
            SchedulingMinPauseHours = 11,
        };

    private static CoreAgent MakeAgent(int maxConsecutiveDays = HardCapDays, string id = AgentId) => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: 0,
        MaxConsecutiveDays: maxConsecutiveDays,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        MaxWorkDays = SoftCapDays,
        MinRestDays = RestDays,
        PerformsShiftWork = true,
        WorkOnMonday = true,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };
}
