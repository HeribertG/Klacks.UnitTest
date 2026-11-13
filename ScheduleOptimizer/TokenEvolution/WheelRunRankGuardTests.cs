// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution;

[TestFixture]
public class WheelRunRankGuardTests
{
    private const int ShiftHours = 8;

    private static readonly CoreWizardContext Context = new AutofillScenarioBuilder()
        .WithPeriod(AutofillSpecConstants.PeriodFrom, AutofillSpecConstants.PeriodUntil)
        .WithEmployees(AutofillSpecConstants.EmployeeCount, AutofillSpecConstants.GuaranteedHours)
        .Build()
        .Context;

    [Test]
    public void LowerRankSixteenHoursAboveAHigherRank_FirstRunInOrder_Rejected()
    {
        var wheel = Plan(23, 21, 23, 22, 4);
        var first = Plan(23, 22, 22, 19, 7);

        WheelRunRankGuard.LargestExcess(wheel, Context).ShouldBe(16);
        WheelRunRankGuard.Accepts(wheel, first, Context).ShouldBeFalse();
    }

    [Test]
    public void ExcessWithinOneShift_Accepted()
    {
        var wheel = Plan(22, 21, 22, 21, 4);
        var first = Plan(23, 22, 22, 19, 7);

        WheelRunRankGuard.LargestExcess(wheel, Context).ShouldBe(8);
        WheelRunRankGuard.Accepts(wheel, first, Context).ShouldBeTrue();
    }

    [Test]
    public void FirstRunAlreadyInvertedAsMuch_NotStricterThanToday()
    {
        var wheel = Plan(23, 21, 23, 22, 4);
        var firstEqual = Plan(23, 20, 22, 19, 7);
        var firstWorse = Plan(23, 19, 22, 19, 7);

        WheelRunRankGuard.Accepts(wheel, firstEqual, Context).ShouldBeTrue();
        WheelRunRankGuard.Accepts(wheel, firstWorse, Context).ShouldBeTrue();
    }

    [Test]
    public void FirstRunLessInverted_WheelInvertingMore_Rejected()
    {
        var wheel = Plan(23, 19, 23, 22, 4);
        var first = Plan(23, 21, 23, 19, 7);

        WheelRunRankGuard.Accepts(wheel, first, Context).ShouldBeFalse();
    }

    private static CoreScenario Plan(params int[] shiftsPerRank)
    {
        var tokens = new List<CoreToken>();
        for (var rank = 0; rank < shiftsPerRank.Length; rank++)
        {
            var agentId = Context.Agents[rank].Id;
            for (var day = 0; day < shiftsPerRank[rank]; day++)
            {
                var date = AutofillSpecConstants.PeriodFrom.AddDays(day);
                tokens.Add(new CoreToken(
                    WorkIds: [],
                    ShiftTypeIndex: 0,
                    Date: date,
                    TotalHours: ShiftHours,
                    StartAt: date.ToDateTime(new TimeOnly(7, 0)),
                    EndAt: date.ToDateTime(new TimeOnly(15, 0)),
                    BlockId: Guid.NewGuid(),
                    PositionInBlock: 0,
                    IsLocked: false,
                    LocationContext: null,
                    ShiftRefId: Guid.Empty,
                    AgentId: agentId));
            }
        }

        return new CoreScenario { Id = Guid.NewGuid().ToString(), Tokens = tokens };
    }
}
