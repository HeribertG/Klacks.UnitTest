// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution;

[TestFixture]
public class WheelRunRankGuardTests
{
    private const double ShiftHours = 8;
    private const double LongShiftHours = 12;
    private const double FullTime = 180;
    private const double PartTime = 90;
    private const int HolidayDays = 10;

    private static readonly DateOnly PeriodFrom = new(2026, 3, 1);
    private static readonly DateOnly PeriodUntil = new(2026, 3, 31);

    [Test]
    public void LowerRankTwoShiftsAheadOfAHigherRank_FirstRunInOrder_Rejected()
    {
        var context = Context(ShiftHours, Agent(FullTime), Agent(FullTime), Agent(FullTime), Agent(FullTime), Agent(FullTime));
        var wheel = Plan(context, 184, 168, 184, 176, 32);
        var first = Plan(context, 184, 176, 176, 152, 56);

        WheelRunRankGuard.LargestExcess(wheel, context).ShouldBe(2, 1e-9);
        WheelRunRankGuard.Accepts(wheel, first, context).ShouldBeFalse();
    }

    [Test]
    public void LeadWithinOneShift_Accepted()
    {
        var context = Context(ShiftHours, Agent(FullTime), Agent(FullTime), Agent(FullTime), Agent(FullTime), Agent(FullTime));
        var wheel = Plan(context, 176, 168, 176, 168, 32);
        var first = Plan(context, 184, 176, 176, 152, 56);

        WheelRunRankGuard.LargestExcess(wheel, context).ShouldBe(1, 1e-9);
        WheelRunRankGuard.Accepts(wheel, first, context).ShouldBeTrue();
    }

    [Test]
    public void FirstRunAlreadyInvertedAsMuch_NotStricterThanToday()
    {
        var context = Context(ShiftHours, Agent(FullTime), Agent(FullTime), Agent(FullTime), Agent(FullTime), Agent(FullTime));
        var wheel = Plan(context, 184, 168, 184, 176, 32);
        var firstEqual = Plan(context, 184, 160, 176, 152, 56);
        var firstWorse = Plan(context, 184, 152, 176, 152, 56);

        WheelRunRankGuard.Accepts(wheel, firstEqual, context).ShouldBeTrue();
        WheelRunRankGuard.Accepts(wheel, firstWorse, context).ShouldBeTrue();
    }

    [Test]
    public void FirstRunLessInverted_WheelInvertingMore_Rejected()
    {
        var context = Context(ShiftHours, Agent(FullTime), Agent(FullTime), Agent(FullTime), Agent(FullTime), Agent(FullTime));
        var wheel = Plan(context, 184, 152, 184, 176, 32);
        var first = Plan(context, 184, 168, 184, 152, 56);

        WheelRunRankGuard.Accepts(wheel, first, context).ShouldBeFalse();
    }

    [Test]
    public void PartTimeHigherRankWithFewerHours_DoesNotBlock()
    {
        var context = Context(ShiftHours, Agent(FullTime), Agent(PartTime), Agent(FullTime));
        var wheel = Plan(context, 184, 88, 160);
        var first = Plan(context, 184, 88, 152);

        WheelRunRankGuard.LargestExcess(wheel, context).ShouldBe(0);
        WheelRunRankGuard.Accepts(wheel, first, context).ShouldBeTrue();
    }

    [Test]
    public void PartTimeHigherRankFarBelowItsShare_StillBlocks()
    {
        var context = Context(ShiftHours, Agent(PartTime), Agent(FullTime));
        var wheel = Plan(context, 40, 184);
        var first = Plan(context, 88, 176);

        WheelRunRankGuard.LargestExcess(wheel, context).ShouldBeGreaterThan(WheelRunRankGuard.ToleranceShifts);
        WheelRunRankGuard.Accepts(wheel, first, context).ShouldBeFalse();
    }

    [Test]
    public void HolidayHoursAreCredited_HigherRankOnHolidayIsNotInverted()
    {
        var holiday = new CoreBreakBlocker(
            AgentId(0), PeriodFrom, PeriodFrom.AddDays(HolidayDays - 1), "Vacation", (decimal)ShiftHours);
        var context = Context(ShiftHours, [holiday], Agent(FullTime), Agent(FullTime));
        var wheel = Plan(context, 104, 184);
        var first = Plan(context, 104, 176);

        WheelRunRankGuard.LargestExcess(wheel, context).ShouldBe(0);
        WheelRunRankGuard.Accepts(wheel, first, context).ShouldBeTrue();
    }

    [Test]
    public void HoursBeforeThePeriodAreCredited()
    {
        var context = Context(ShiftHours, Agent(FullTime, currentHours: 40), Agent(FullTime));
        var wheel = Plan(context, 144, 184);

        WheelRunRankGuard.LargestExcess(wheel, context).ShouldBe(0);
    }

    [Test]
    public void PeopleWithoutGuaranteedHoursOrShiftWorkAndPlaceholders_AreLeftOut()
    {
        var context = Context(
            ShiftHours,
            Agent(FullTime),
            Agent(0),
            Agent(FullTime) with { PerformsShiftWork = false },
            Agent(FullTime),
            Agent(FullTime) with { IsStandIn = true });
        var wheel = Plan(context, 160, 0, 0, 160, 184);
        var first = Plan(context, 160, 0, 0, 160, 0);

        WheelRunRankGuard.LargestExcess(wheel, context).ShouldBe(0);
        WheelRunRankGuard.Accepts(wheel, first, context).ShouldBeTrue();
    }

    [Test]
    public void ToleranceFollowsTheTypicalShiftLength()
    {
        var shortShifts = Context(ShiftHours, Agent(FullTime), Agent(FullTime));
        var longShifts = Context(LongShiftHours, Agent(FullTime), Agent(FullTime));

        WheelRunRankGuard.Accepts(Plan(shortShifts, 170, 180), Plan(shortShifts, 180, 170), shortShifts).ShouldBeFalse();
        WheelRunRankGuard.Accepts(Plan(longShifts, 170, 180), Plan(longShifts, 180, 170), longShifts).ShouldBeTrue();
    }

    [TestCase(4, 4, 1.0)]
    [TestCase(3, 1, 1.25)]
    public void TypicalShiftIsTheMedianShiftLength(int shortShifts, int longShifts, double expectedLead)
    {
        var shifts = Enumerable.Range(0, shortShifts).Select(i => Shift(i, ShiftHours))
            .Concat(Enumerable.Range(shortShifts, longShifts).Select(i => Shift(i, LongShiftHours)))
            .ToList();
        var context = new CoreWizardContext
        {
            PeriodFrom = PeriodFrom,
            PeriodUntil = PeriodUntil,
            Agents = [Agent(FullTime) with { Id = AgentId(0) }, Agent(FullTime) with { Id = AgentId(1) }],
            Shifts = shifts,
        };

        WheelRunRankGuard.LargestExcess(Plan(context, 170, 180), context).ShouldBe(expectedLead, 1e-9);
    }

    [Test]
    public void SurchargesAreCredited()
    {
        var context = Context(ShiftHours, Agent(FullTime), Agent(FullTime));
        var plan = Plan(context, 168, 184);
        var surcharged = new CoreScenario
        {
            Id = plan.Id,
            Tokens = plan.Tokens.Select(t => t.AgentId == AgentId(0) ? t with { Surcharges = (decimal)ShiftHours * 2 } : t).ToList(),
        };

        WheelRunRankGuard.LargestExcess(plan, context).ShouldBe(2, 1e-9);
        WheelRunRankGuard.LargestExcess(surcharged, context).ShouldBe(0);
    }

    [Test]
    public void PeriodWithoutShifts_MeasuresNoLead()
    {
        var context = new CoreWizardContext
        {
            PeriodFrom = PeriodFrom,
            PeriodUntil = PeriodUntil,
            Agents = [Agent(FullTime), Agent(FullTime)],
        };

        WheelRunRankGuard.LargestExcess(Plan(context, 0, 184), context).ShouldBe(0);
    }

    private static CoreAgent Agent(double guaranteedHours, double currentHours = 0)
        => new(string.Empty, currentHours, guaranteedHours, 6, 11, 0.5, 10, 50, 2);

    private static string AgentId(int rank) => $"MA-{rank + 1}";

    private static CoreShift Shift(int day, double hours)
    {
        var date = PeriodFrom.AddDays(day);
        return new CoreShift($"S-{date:yyyyMMdd}", "Early", date.ToString("yyyy-MM-dd"), "07:00", "15:00", hours, 1, 0);
    }

    private static CoreWizardContext Context(double shiftHours, params CoreAgent[] agents)
        => Context(shiftHours, [], agents);

    private static CoreWizardContext Context(double shiftHours, IReadOnlyList<CoreBreakBlocker> breaks, params CoreAgent[] agents)
    {
        var shifts = new List<CoreShift>();
        for (var day = 0; day <= PeriodUntil.DayNumber - PeriodFrom.DayNumber; day++)
        {
            shifts.Add(Shift(day, shiftHours));
        }

        return new CoreWizardContext
        {
            PeriodFrom = PeriodFrom,
            PeriodUntil = PeriodUntil,
            Agents = agents.Select((a, rank) => a with { Id = AgentId(rank) }).ToList(),
            Shifts = shifts,
            BreakBlockers = breaks,
        };
    }

    private static CoreScenario Plan(CoreWizardContext context, params double[] hoursPerRank)
    {
        var tokens = new List<CoreToken>();
        for (var rank = 0; rank < hoursPerRank.Length; rank++)
        {
            if (hoursPerRank[rank] <= 0)
            {
                continue;
            }

            tokens.Add(new CoreToken(
                WorkIds: [],
                ShiftTypeIndex: 0,
                Date: PeriodFrom,
                TotalHours: (decimal)hoursPerRank[rank],
                StartAt: PeriodFrom.ToDateTime(new TimeOnly(7, 0)),
                EndAt: PeriodFrom.ToDateTime(new TimeOnly(15, 0)),
                BlockId: Guid.NewGuid(),
                PositionInBlock: 0,
                IsLocked: false,
                LocationContext: null,
                ShiftRefId: Guid.Empty,
                AgentId: context.Agents[rank].Id));
        }

        return new CoreScenario { Id = Guid.NewGuid().ToString(), Tokens = tokens };
    }
}
