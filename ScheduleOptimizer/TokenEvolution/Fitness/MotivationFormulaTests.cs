// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentAssertions;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Fitness;

[TestFixture]
public class MotivationFormulaTests
{
    private static CoreAgent MakeAgent(string id, double fullTime, double currentHours)
    {
        return new CoreAgent(
            Id: id,
            CurrentHours: currentHours,
            GuaranteedHours: 0,
            MaxConsecutiveDays: 6,
            MinRestHours: 11,
            Motivation: 0.5,
            MaxDailyHours: 10,
            MaxWeeklyHours: 50,
            MaxOptimalGap: 2)
        {
            FullTime = fullTime,
        };
    }

    [Test]
    public void Compute_FullTimeAgentWithZeroCurrentHours_ReturnsAtLeast0_6()
    {
        var agent = MakeAgent("A", fullTime: 40, currentHours: 0);
        var shiftId = Guid.NewGuid();

        var motivation = MotivationFormula.Compute(agent, shiftId, 8m, []);

        motivation.Should().BeGreaterThanOrEqualTo(0.5);
    }

    [Test]
    public void Compute_BlacklistedShift_ReturnsZero()
    {
        var agent = MakeAgent("A", fullTime: 40, currentHours: 10);
        var shiftId = Guid.NewGuid();
        var preferences = new[] { new CoreShiftPreference("A", shiftId, ShiftPreferenceKind.Blacklist) };

        var motivation = MotivationFormula.Compute(agent, shiftId, 8m, preferences);

        motivation.Should().Be(0);
    }

    [Test]
    public void Compute_PreferredShift_IncreasesMotivation()
    {
        var agent = MakeAgent("A", fullTime: 40, currentHours: 10);
        var shiftId = Guid.NewGuid();
        var withPref = new[] { new CoreShiftPreference("A", shiftId, ShiftPreferenceKind.Preferred) };

        var motivationWithPreference = MotivationFormula.Compute(agent, shiftId, 8m, withPref);
        var motivationWithoutPreference = MotivationFormula.Compute(agent, shiftId, 8m, []);

        motivationWithPreference.Should().BeGreaterThan(motivationWithoutPreference);
    }

    [Test]
    public void Compute_AgentWithoutFullTime_ReturnsZeroHunger()
    {
        var agent = MakeAgent("A", fullTime: 0, currentHours: 0);

        var motivation = MotivationFormula.Compute(agent, Guid.NewGuid(), 8m, []);

        motivation.Should().Be(0);
    }

    [Test]
    public void Compute_AgentApproachingFullTime_ReturnsLowerMotivationThanEmptyAgent()
    {
        var empty = MakeAgent("A", fullTime: 40, currentHours: 0);
        var almostFull = MakeAgent("B", fullTime: 40, currentHours: 38);
        var shiftId = Guid.NewGuid();

        var motivationEmpty = MotivationFormula.Compute(empty, shiftId, 8m, []);
        var motivationAlmostFull = MotivationFormula.Compute(almostFull, shiftId, 8m, []);

        motivationEmpty.Should().BeGreaterThan(motivationAlmostFull);
    }
}
