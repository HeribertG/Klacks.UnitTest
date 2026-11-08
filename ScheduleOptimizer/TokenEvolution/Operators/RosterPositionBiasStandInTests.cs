// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Operators;

/// <summary>
/// The hour-adding pick of repair and reassign gives a slot to a stand-in only when no real person is a valid candidate,
/// also when every real candidate has already reached the guaranteed hours (the surplus rule would otherwise pick the
/// bottom of the roster, where the stand-ins are).
/// </summary>
[TestFixture]
public class RosterPositionBiasStandInTests
{
    private const int Draws = 200;

    [Test]
    public void PickAccuracyAware_RealPersonAtTarget_WinsAgainstAStandInAtTheBottom()
    {
        var real = Agent("real", currentHours: 160, guaranteedHours: 160);
        var standIn = Agent("stand-in", currentHours: 0, guaranteedHours: 0) with { IsStandIn = true };
        var roster = new[] { real, standIn };
        var rng = new Random(1);

        for (var i = 0; i < Draws; i++)
        {
            RosterPositionBias.PickAccuracyAware(roster, [], roster, rng).Id.ShouldBe(real.Id);
        }
    }

    [Test]
    public void PickAccuracyAware_OnlyStandInsValid_PicksAStandIn()
    {
        var real = Agent("real", currentHours: 160, guaranteedHours: 160);
        var standIn = Agent("stand-in", currentHours: 0, guaranteedHours: 0) with { IsStandIn = true };

        RosterPositionBias.PickAccuracyAware([standIn], [], [real, standIn], new Random(1)).Id.ShouldBe(standIn.Id);
    }

    private static CoreAgent Agent(string id, double currentHours, double guaranteedHours)
        => new(id, currentHours, guaranteedHours, 6, 11, 0.5, 10, 50, 2);
}
