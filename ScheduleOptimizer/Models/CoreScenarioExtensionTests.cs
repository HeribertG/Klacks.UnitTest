// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentAssertions;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Models;

[TestFixture]
public class CoreScenarioExtensionTests
{
    [Test]
    public void CoreScenario_CarriesTokensAndStageScores()
    {
        var scenario = new CoreScenario
        {
            Id = "scenario-1",
            Tokens = new List<CoreToken>(),
            FitnessStage0 = 0,
            FitnessStage1 = 1.0,
            FitnessStage2 = 0.92,
            FitnessStage3 = 0.7,
            FitnessStage4 = 0.5,
        };

        scenario.Tokens.Should().NotBeNull();
        scenario.FitnessStage1.Should().Be(1.0);
        scenario.FitnessStage4.Should().Be(0.5);
    }

    [Test]
    public void CoreScenario_BackwardCompatibility_OldFieldsStillExist()
    {
        var scenario = new CoreScenario
        {
            Id = "legacy",
            Assignments = [new CoreAssignment("shift-1", "agent-1", 0.5)],
            Fitness = 0.5,
            Coverage = 0.8,
            HardViolations = 0,
        };

        scenario.Assignments.Should().HaveCount(1);
    }
}
