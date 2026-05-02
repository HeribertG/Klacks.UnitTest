// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
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

        scenario.Tokens.ShouldNotBeNull();
        scenario.FitnessStage1.ShouldBe(1.0);
        scenario.FitnessStage4.ShouldBe(0.5);
    }

}
