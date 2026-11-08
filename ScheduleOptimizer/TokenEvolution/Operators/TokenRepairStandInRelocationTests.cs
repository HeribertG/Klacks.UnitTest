// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Constraints;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Operators;

/// <summary>
/// The one-move relocation of the repair hands the blocking neighbour shift to a real person before a stand-in
/// (placeholder): the donor's late shift on day 1 walls in the open early slot on day 2 (rest time), only the donor may
/// work day 2, and both a real person and a stand-in could take the late shift. The roster order puts the stand-in at the
/// bottom, where the surplus group is searched first.
/// </summary>
[TestFixture]
public sealed class TokenRepairStandInRelocationTests
{
    private const string DonorId = "donor";
    private const string RealId = "real";
    private const string StandInId = "stand-in";

    private static readonly DateOnly DayOne = new(2026, 6, 4);
    private static readonly DateOnly DayTwo = DayOne.AddDays(1);
    private static readonly Guid LateShiftId = Guid.NewGuid();
    private static readonly Guid EarlyShiftId = Guid.NewGuid();

    [Test]
    public void Repair_RelocatedBlocker_GoesToTheRealPersonBeforeTheStandIn()
    {
        var context = new CoreWizardContext
        {
            PeriodFrom = DayOne,
            PeriodUntil = DayTwo,
            Agents = [Agent(DonorId), Agent(RealId), Agent(StandInId) with { IsStandIn = true }],
            Shifts =
            [
                new CoreShift(LateShiftId.ToString(), "Late", DayOne.ToString("yyyy-MM-dd"), "15:00", "23:00", 8, 1, 0),
                new CoreShift(EarlyShiftId.ToString(), "Early", DayTwo.ToString("yyyy-MM-dd"), "06:00", "14:00", 8, 1, 0),
            ],
            ContractDays =
            [
                ContractDay(DonorId, DayOne, true),
                ContractDay(DonorId, DayTwo, true),
                ContractDay(RealId, DayOne, true),
                ContractDay(RealId, DayTwo, false),
                ContractDay(StandInId, DayOne, true),
                ContractDay(StandInId, DayTwo, false),
            ],
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMaxDailyHours = 10,
            SchedulingMinPauseHours = 11,
        };
        var scenario = new CoreScenario
        {
            Id = "s",
            Tokens =
            [
                new CoreToken(
                    [], 1, DayOne, 8m,
                    DayOne.ToDateTime(new TimeOnly(15, 0)), DayOne.ToDateTime(new TimeOnly(23, 0)),
                    Guid.NewGuid(), 0, false, null, LateShiftId, DonorId),
            ],
        };

        var result = new TokenRepair(new TokenConstraintChecker())
            .Apply(new TokenOperatorContext(scenario, null, context, new Random(0)));

        result.Tokens.ShouldContain(t => t.ShiftRefId == EarlyShiftId && t.AgentId == DonorId);
        result.Tokens.ShouldContain(t => t.ShiftRefId == LateShiftId && t.AgentId == RealId);
        result.Tokens.ShouldNotContain(t => t.AgentId == StandInId);
    }

    private static CoreAgent Agent(string id) => new(id, 0, 0, 6, 11, 0.5, 10, 50, 2)
    {
        PerformsShiftWork = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };

    private static CoreContractDay ContractDay(string agentId, DateOnly date, bool worksOnDay) => new(
        AgentId: agentId,
        Date: date,
        WorksOnDay: worksOnDay,
        PerformsShiftWork: true,
        FullTimeShare: 1,
        MaximumHoursPerDay: 10,
        ContractId: Guid.Empty);
}
