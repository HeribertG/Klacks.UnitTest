// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Constraints;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario2;
using Klacks.UnitTest.Autofill.Scenarios.Scenario3;
using Klacks.UnitTest.Autofill.Scenarios.Scenario5;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

[TestFixture]
public class RotationWheelTests
{
    private const string Cycle = "FFFFF..SSSSS..NNNNN..";
    private const string Rank1 = "MA-1";
    private const string Rank5 = "MA-5";

    [Test]
    public void CleanStart_FourSeatsOnTheSettingsCycle_LastRankIsFiller()
    {
        var wheel = RotationWheel.Build(CleanStart(AutofillSpecConstants.GuaranteedHours).Context);

        wheel.ShouldNotBeNull();
        Symbols(wheel.Pattern).ShouldBe(Cycle);
        wheel.Members.ShouldBe(["MA-1", "MA-2", "MA-3", "MA-4"]);
        wheel.Expected(Rank5, AutofillSpecConstants.PeriodFrom).ShouldBeNull();
    }

    [Test]
    public void CleanStart_UpperRanksGetThePhasesWithTheMostWorkDays()
    {
        var definition = CleanStart(AutofillSpecConstants.GuaranteedHours);
        var wheel = RotationWheel.Build(definition.Context)!;

        wheel.Members.Select(m => WorkDays(wheel, m, definition)).ShouldBe([23, 23, 22, 21]);
    }

    [Test]
    public void CalibrationContract_RowsWouldExceedTheContract_NoWheel()
    {
        RotationWheel.Build(CleanStart(AutofillSpecConstants.CalibrationGuaranteedHours).Context).ShouldBeNull();
    }

    [Test]
    public void TwoPersonsWithoutNights_TwoGroups_NoWheel()
    {
        RotationWheel.Build(Scenario3EligibilityFixture.BuildL1().Context).ShouldBeNull();
    }

    [Test]
    public void FillersWithoutNights_BelowTheSeats_WheelStillApplies()
    {
        var context = Scenario5Fixture.BuildMainRun().Context;
        var seatHolders = context.Agents.Take(12).Select(a => a.Id).ToList();

        var wheel = RotationWheel.Build(context);

        wheel.ShouldNotBeNull();
        wheel.Members.Count.ShouldBeGreaterThanOrEqualTo(2);
        wheel.Members.ShouldAllBe(member => seatHolders.Contains(member));
    }

    [Test]
    public void CarryIn_RowsContinueThePreviousPhase_LikeTheHandCalculation()
    {
        var definition = Scenario2CarryInFixture.Build();
        var wheel = RotationWheel.Build(definition.Context)!;
        string[] handCalculatedRows =
        [
            "NNN..FFFFF..SSSSS..NNNNN..FFFFF",
            "S..NNNNN..FFFFF..SSSSS..NNNNN..",
            "FFFF..SSSSS..NNNNN..FFFFF..SSSS",
            ".SSSSS..NNNNN..FFFFF..SSSSS..NN",
        ];

        wheel.Members.ShouldBe(["MA-1", "MA-2", "MA-3", "MA-4"]);
        for (var row = 0; row < wheel.Members.Count; row++)
        {
            Row(wheel, definition.EmployeesInListOrder[row], definition).ShouldBe(handCalculatedRows[row]);
        }
    }

    [Test]
    public void LongAbsence_RowRestartsWithAnEarlyStage()
    {
        var absence = Enumerable.Range(2, 10)
            .Select(d => new AutofillBreakBlocker(Rank1, AutofillSpecConstants.PeriodFrom.AddDays(d), "Vacation", 8m));
        var definition = BuilderFor(AutofillSpecConstants.GuaranteedHours)
            .WithBreakBlockers(new AutofillBreakBlockerInput(absence))
            .Build();

        var row = Row(RotationWheel.Build(definition.Context)!, Rank1, definition);

        row.Substring(2, 10).ShouldBe("..........");
        row.Substring(12, 5).ShouldBe("FFFFF");
    }

    [Test]
    public void ShortAbsence_LeavesHolesAndKeepsThePhase()
    {
        var plain = CleanStart(AutofillSpecConstants.GuaranteedHours);
        var absence = Enumerable.Range(2, 3)
            .Select(d => new AutofillBreakBlocker(Rank1, AutofillSpecConstants.PeriodFrom.AddDays(d), "Vacation", 8m));
        var definition = BuilderFor(AutofillSpecConstants.GuaranteedHours)
            .WithBreakBlockers(new AutofillBreakBlockerInput(absence))
            .Build();

        var before = Row(RotationWheel.Build(plain.Context)!, Rank1, plain);
        var after = Row(RotationWheel.Build(definition.Context)!, Rank1, definition);

        after.Substring(2, 3).ShouldBe("...");
        after.Remove(2, 3).ShouldBe(before.Remove(2, 3));
    }

    [Test]
    public void Strategy_CleanStart_LegalFullCoverageAndSeatHoldersFollowTheirRows()
    {
        var definition = CleanStart(AutofillSpecConstants.GuaranteedHours);
        var context = definition.Context;
        var wheel = RotationWheel.Build(context)!;

        var plan = new RotationWheelTokenStrategy().BuildScenario(context, new Random(1));

        new TokenConstraintChecker().Check(plan, context).ShouldBeEmpty();
        plan.Tokens.Count.ShouldBe(AutofillSpecConstants.TotalRequiredShifts);
        foreach (var member in wheel.Members)
        {
            Planned(plan, member, definition).ShouldBe(Row(wheel, member, definition));
        }
    }

    [Test]
    public void Strategy_CarryIn_LegalAndFullCoverage()
    {
        var definition = Scenario2CarryInFixture.Build();

        var plan = new RotationWheelTokenStrategy().BuildScenario(definition.Context, new Random(1));

        new TokenConstraintChecker().Check(plan, definition.Context).ShouldBeEmpty();
        plan.Tokens.Count(t => !t.IsLocked).ShouldBe(AutofillSpecConstants.TotalRequiredShifts);
    }

    [Test]
    public void Strategy_NoWheel_DoesNotApply()
    {
        new RotationWheelTokenStrategy().AppliesTo(CleanStart(AutofillSpecConstants.CalibrationGuaranteedHours).Context).ShouldBeFalse();
    }

    private static AutofillScenarioDefinition CleanStart(double hours) => BuilderFor(hours).Build();

    private static AutofillScenarioBuilder BuilderFor(double hours)
        => new AutofillScenarioBuilder()
            .WithPeriod(AutofillSpecConstants.PeriodFrom, AutofillSpecConstants.PeriodUntil)
            .WithEmployees(AutofillSpecConstants.EmployeeCount, hours)
            .WithRandomSeed(AutofillSpecConstants.RandomSeed)
            .WithGaParameters(AutofillSpecConstants.PopulationSize, AutofillSpecConstants.MaxGenerations);

    private static string Symbols(IEnumerable<int> kinds) => string.Concat(kinds.Select(Symbol));

    private static char Symbol(int kind) => kind switch { 0 => 'F', 1 => 'S', 2 => 'N', _ => '.' };

    private static string Row(RotationWheel wheel, string agentId, AutofillScenarioDefinition definition)
        => string.Concat(Days(definition).Select(d => Symbol(wheel.Expected(agentId, d) ?? -1)));

    private static int WorkDays(RotationWheel wheel, string agentId, AutofillScenarioDefinition definition)
        => Row(wheel, agentId, definition).Count(c => c != '.');

    private static string Planned(CoreScenario plan, string agentId, AutofillScenarioDefinition definition)
        => string.Concat(Days(definition).Select(d =>
            plan.Tokens.FirstOrDefault(t => t.AgentId == agentId && t.Date == d) is { } token ? Symbol(token.ShiftTypeIndex) : '.'));

    private static IEnumerable<DateOnly> Days(AutofillScenarioDefinition definition)
        => Enumerable.Range(0, definition.PeriodUntil.DayNumber - definition.PeriodFrom.DayNumber + 1)
            .Select(d => definition.PeriodFrom.AddDays(d));
}
