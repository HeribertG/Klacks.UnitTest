// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Diagnostics;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Fitness;

[TestFixture]
public class LevelShortfallFitnessTests
{
    private static readonly DateOnly Date = new(2026, 4, 20);
    private static readonly Guid ShiftRef = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static CoreAgent MakeAgent(string id) => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: 0,
        MaxConsecutiveDays: 6,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        FullTime = 40,
        PerformsShiftWork = true,
        WorkOnMonday = true,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
    };

    private static CoreToken MakeToken(string agentId)
    {
        var start = new TimeOnly(8, 0);
        return new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: 0,
            Date: Date,
            TotalHours: 8,
            StartAt: Date.ToDateTime(start),
            EndAt: Date.ToDateTime(start.AddHours(8)),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: null,
            ShiftRefId: ShiftRef,
            AgentId: agentId);
    }

    private static CoreWizardContext MakeContext(IReadOnlyDictionary<(string AgentId, Guid ShiftId, DateOnly Date), int> shortfall) => new()
    {
        PeriodFrom = Date,
        PeriodUntil = Date,
        Agents = [MakeAgent("A"), MakeAgent("B"), MakeAgent("C")],
        Shifts = [new CoreShift(ShiftRef.ToString(), "FD", Date.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 1, 0)],
        LevelShortfall = shortfall,
    };

    private static CoreScenario Scenario(string agentId) => new() { Id = agentId, Tokens = [MakeToken(agentId)] };

    [Test]
    public void Stage3_AgentBelowANonBindingLevel_ScoresWorseThanAQualifiedAgent()
    {
        // Arrange
        var context = MakeContext(new Dictionary<(string, Guid, DateOnly), int> { [("A", ShiftRef, Date)] = 2 });
        var sut = TokenFitnessEvaluator.Create(context);
        var belowLevel = Scenario("A");
        var qualified = Scenario("B");

        // Act
        sut.Evaluate(belowLevel, context);
        sut.Evaluate(qualified, context);

        // Assert
        belowLevel.FitnessStage3.ShouldBeLessThan(qualified.FitnessStage3);
        belowLevel.FitnessStage0.ShouldBe(qualified.FitnessStage0);
    }

    [Test]
    public void Stage3_PenaltyGrowsWithTheLevelGap()
    {
        // Arrange
        var context = MakeContext(new Dictionary<(string, Guid, DateOnly), int>
        {
            [("A", ShiftRef, Date)] = 1,
            [("C", ShiftRef, Date)] = 3,
        });
        var sut = TokenFitnessEvaluator.Create(context);
        var oneLevelShort = Scenario("A");
        var threeLevelsShort = Scenario("C");

        // Act
        sut.Evaluate(oneLevelShort, context);
        sut.Evaluate(threeLevelsShort, context);

        // Assert
        threeLevelsShort.FitnessStage3.ShouldBeLessThan(oneLevelShort.FitnessStage3);
    }

    /// <summary>
    /// Reference value from the Stage-3 formula as it stood before the level term existed: the five weighted components
    /// (block order, blacklist, location, gap, package compactness; no carry-in package in this context) over the sum of
    /// their weights, in the same evaluation order. With an empty shortfall map the new code must reproduce it exactly.
    /// </summary>
    [Test]
    public void Stage3_WithoutAnyShortfall_IsBitIdenticalToTheFormulaBeforeTheTerm()
    {
        // Arrange
        var context = MakeContext(new Dictionary<(string, Guid, DateOnly), int>());
        var sut = TokenFitnessEvaluator.Create(context);
        var scenario = Scenario("A");

        // Act
        var components = sut.EvaluateDetailed(scenario, context).Stage3Components;

        // Assert
        var expected = (components.BlockOrder * sut.Stage3BlockOrderWeight
                + components.Blacklist * sut.Stage3BlacklistWeight
                + components.Location * sut.Stage3LocationWeight
                + components.MaxGap * sut.Stage3MaxGapWeight
                + components.PackageCompactness * sut.Stage3PackageLengthWeight)
            / (sut.Stage3BlockOrderWeight + sut.Stage3BlacklistWeight + sut.Stage3LocationWeight
                + sut.Stage3MaxGapWeight + sut.Stage3PackageLengthWeight);
        scenario.FitnessStage3.ShouldBe(expected);
        components.LevelShortfall.ShouldBe(1);
    }

    [Test]
    public void Stage3_ShortfallAboveTheMaximumPerToken_IsCappedAtTheWorstCase()
    {
        // Arrange
        var capped = MakeContext(new Dictionary<(string, Guid, DateOnly), int> { [("A", ShiftRef, Date)] = 4 });
        var excessive = MakeContext(new Dictionary<(string, Guid, DateOnly), int> { [("A", ShiftRef, Date)] = 9 });
        var sut = TokenFitnessEvaluator.Create(capped);

        // Act
        var atCap = sut.EvaluateDetailed(Scenario("A"), capped).Stage3Components.LevelShortfall;
        var aboveCap = sut.EvaluateDetailed(Scenario("A"), excessive).Stage3Components.LevelShortfall;

        // Assert
        atCap.ShouldBe(0);
        aboveCap.ShouldBe(atCap);
        LevelShortfallTrace.MissingLevels(Scenario("A"), excessive).ShouldBe(LevelShortfallTrace.MaxLevelGapPerToken);
    }

    [Test]
    public void ParetoSnapshot_CarriesTheCappedMissingLevels()
    {
        // Arrange
        var context = MakeContext(new Dictionary<(string, Guid, DateOnly), int> { [("A", ShiftRef, Date)] = 2 });
        var sut = TokenFitnessEvaluator.Create(context);

        // Act
        var snapshot = ParetoFairnessGate.SnapshotOf(Scenario("A"), context, sut);

        // Assert
        snapshot.MissingLevels.ShouldBe(2);
    }

    [Test]
    public void EvaluateDetailed_ReportsTheShortfallComponent()
    {
        // Arrange
        var context = MakeContext(new Dictionary<(string, Guid, DateOnly), int> { [("A", ShiftRef, Date)] = 2 });
        var sut = TokenFitnessEvaluator.Create(context);

        // Act
        var detailed = sut.EvaluateDetailed(Scenario("A"), context);

        // Assert
        detailed.Stage3Components.LevelShortfall.ShouldBe(0.5, 1e-9);
    }
}
