// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

/// <summary>
/// The perturbator is what turns a deterministic strategy into a diverse population slice, so two
/// properties must hold for every clone: it differs from its base, and it still carries every locked
/// token - a clone that dropped an immutable cell would be an invalid plan from the first generation.
/// </summary>
[TestFixture]
public sealed class ScenarioPerturbatorTests
{
    private static readonly DateOnly Day = new(2026, 5, 4);

    [Test]
    public void Perturb_KeepsEveryLockedToken()
    {
        var source = BuildScenario(lockedCount: 4, mutableCount: 20);

        for (var seed = 0; seed < 20; seed++)
        {
            var clone = ScenarioPerturbator.Perturb(source, new Random(seed));

            clone.Tokens.Count(t => t.IsLocked).ShouldBe(4);
        }
    }

    [Test]
    public void Perturb_DropsAtLeastOneMutableToken()
    {
        var source = BuildScenario(lockedCount: 2, mutableCount: 10);

        for (var seed = 0; seed < 20; seed++)
        {
            var clone = ScenarioPerturbator.Perturb(source, new Random(seed));

            clone.Tokens.Count.ShouldBeLessThan(source.Tokens.Count);
        }
    }

    [Test]
    public void Perturb_LeavesTheSourceUntouched()
    {
        var source = BuildScenario(lockedCount: 1, mutableCount: 10);

        ScenarioPerturbator.Perturb(source, new Random(7));

        source.Tokens.Count.ShouldBe(11);
    }

    [Test]
    public void Perturb_ScenarioWithoutMutableTokens_IsANoOp()
    {
        var source = BuildScenario(lockedCount: 3, mutableCount: 0);

        var clone = ScenarioPerturbator.Perturb(source, new Random(1));

        clone.Tokens.Count.ShouldBe(3);
        clone.Tokens.ShouldAllBe(t => t.IsLocked);
    }

    [Test]
    public void Perturb_EmptyScenario_ReturnsEmptyScenario()
    {
        var clone = ScenarioPerturbator.Perturb(new CoreScenario { Id = "empty" }, new Random(1));

        clone.Tokens.ShouldBeEmpty();
        clone.Id.ShouldNotBe("empty");
    }

    private static CoreScenario BuildScenario(int lockedCount, int mutableCount)
    {
        var tokens = new List<CoreToken>();
        for (var i = 0; i < lockedCount; i++)
        {
            tokens.Add(BuildToken($"locked-{i}", isLocked: true));
        }

        for (var i = 0; i < mutableCount; i++)
        {
            tokens.Add(BuildToken($"agent-{i}", isLocked: false));
        }

        return new CoreScenario { Id = Guid.NewGuid().ToString(), Tokens = tokens };
    }

    private static CoreToken BuildToken(string agentId, bool isLocked) => new(
        WorkIds: [],
        ShiftTypeIndex: 0,
        Date: Day,
        TotalHours: 8m,
        StartAt: Day.ToDateTime(new TimeOnly(7, 0)),
        EndAt: Day.ToDateTime(new TimeOnly(15, 0)),
        BlockId: Guid.NewGuid(),
        PositionInBlock: 0,
        IsLocked: isLocked,
        LocationContext: null,
        ShiftRefId: Guid.NewGuid(),
        AgentId: agentId);
}
