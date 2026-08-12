// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Constraints;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Operators;

/// <summary>
/// Pins the package-aware repair of SPEC.md decision 13. Under the unescalatable hour-based rest the
/// old fill scattered single days: the accuracy roulette did not care whether a candidate's fill
/// extends an existing package or opens a new one-day block. The repair now prefers the extension
/// candidate INSIDE each accuracy group and walks the open slots in date order, so every filled day
/// turns its neighbour into the next extension case and broken coverage grows back as packages.
/// The preference must hold on every seed — it narrows the roulette pool, it does not weight it —
/// which is what these fixtures prove by running the same repair under many seeds.
/// </summary>
[TestFixture]
public sealed class TokenRepairPackageAwareFillTests
{
    private const string TopAgentId = "B-top";

    private const string ExtenderAgentId = "A-extender";

    private const decimal SlotHours = 8;

    private const double SlotHoursAsDouble = 8;

    private const string SlotStart = "08:00";

    private const string SlotEnd = "16:00";

    private const string DateFormat = "yyyy-MM-dd";

    private const double GuaranteedHours = 160;

    private const int SeedProbeCount = 10;

    private static readonly DateOnly FirstDay = new(2026, 6, 1);

    /// <summary>
    /// Two valid candidates below their guarantee; the top-of-roster agent would win the accuracy
    /// roulette two times out of three, but only the lower agent holds a shift on the neighbouring
    /// day. The extension preference must send the fill to the lower agent on EVERY seed.
    /// </summary>
    [Test]
    public void DirectFill_PrefersTheAgentWhoseFillExtendsAPackage_OnEverySeed()
    {
        for (var seed = 0; seed < SeedProbeCount; seed++)
        {
            var (context, scenario) = TwoAgentFixture(openDays: 1);

            var result = new TokenRepair(new TokenConstraintChecker())
                .Apply(new TokenOperatorContext(scenario, null, context, new Random(seed)));

            var filled = result.Tokens.Single(t => t.Date == FirstDay.AddDays(1));
            filled.AgentId.ShouldBe(
                ExtenderAgentId,
                $"seed {seed.ToString(CultureInfo.InvariantCulture)}: the fill must extend the existing "
                + "package instead of opening a new one-day block for the top agent");
        }
    }

    /// <summary>
    /// A stretch of three open days next to one agent's single shift, filled through the coverage
    /// sweep the GA loop uses. The date-ordered walk plus the extension preference must give the
    /// whole stretch to that agent as ONE growing package; the empty second agent — although
    /// preferred by the roulette's top bias — receives nothing.
    /// </summary>
    [Test]
    public void Sweep_GrowsAnOpenStretchBackAsOnePackage()
    {
        for (var seed = 0; seed < SeedProbeCount; seed++)
        {
            var (context, scenario) = TwoAgentFixture(openDays: 3);

            var result = new TokenRepair(new TokenConstraintChecker())
                .FillAllUnderSupply(scenario, context, new Random(seed));

            result.Tokens.Count.ShouldBe(4);
            result.Tokens.ShouldAllBe(t => t.AgentId == ExtenderAgentId);
            result.Tokens.Select(t => t.Date).OrderBy(d => d).ShouldBe(
                Enumerable.Range(0, 4).Select(offset => FirstDay.AddDays(offset)));
        }
    }

    /// <summary>
    /// One shift on the first day owned by the extender agent, then <paramref name="openDays"/> open
    /// slots on the following days. The top-of-roster agent is empty and equally valid everywhere.
    /// </summary>
    private static (CoreWizardContext Context, CoreScenario Scenario) TwoAgentFixture(int openDays)
    {
        var shifts = new List<CoreShift>();
        for (var offset = 0; offset <= openDays; offset++)
        {
            shifts.Add(Shift(FirstDay.AddDays(offset)));
        }

        var context = new CoreWizardContext
        {
            PeriodFrom = FirstDay,
            PeriodUntil = FirstDay.AddDays(openDays),
            Agents = [MakeAgent(TopAgentId), MakeAgent(ExtenderAgentId)],
            Shifts = shifts,
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMaxDailyHours = 10,
            SchedulingMinPauseHours = 11,
        };

        var scenario = new CoreScenario
        {
            Id = "s",
            Tokens = [Token(FirstDay, shifts[0], ExtenderAgentId)],
        };

        return (context, scenario);
    }

    private static CoreShift Shift(DateOnly date) => new(
        Guid.NewGuid().ToString(),
        date.ToString(DateFormat, CultureInfo.InvariantCulture),
        date.ToString(DateFormat, CultureInfo.InvariantCulture),
        SlotStart,
        SlotEnd,
        SlotHoursAsDouble,
        1,
        0);

    private static CoreToken Token(DateOnly date, CoreShift shift, string agentId) => new(
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
        AgentId: agentId);

    private static CoreAgent MakeAgent(string id) => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: GuaranteedHours,
        MaxConsecutiveDays: 6,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        FullTime = GuaranteedHours,
        MaxWorkDays = 5,
        MinRestDays = 2,
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
