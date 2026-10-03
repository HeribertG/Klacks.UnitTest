// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.HolisticHarmonizer.Loop;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Search;

[TestFixture]
public class TabuListTests
{
    private const int Tenure = 3;
    private static readonly PlanCellSwap SameDay = new(2, 5, 0, 5, string.Empty);
    private static readonly PlanCellSwap Mirrored = new(0, 5, 2, 5, string.Empty);
    private static readonly PlanCellSwap CrossDay = new(1, 4, 3, 6, string.Empty);

    [Test]
    public void Record_KeyIsTabuForTenureIterations_ThenExpires()
    {
        // Arrange
        var tabu = new TabuList(Tenure);

        // Act
        tabu.Record([SameDay], iteration: 0);

        // Assert
        for (var iteration = 1; iteration <= Tenure; iteration++)
        {
            tabu.IsTabu(SameDay, iteration).ShouldBeTrue();
            tabu.ActiveKeys(iteration).ShouldContain(ForbiddenSwapKey.From(SameDay));
        }
        tabu.IsTabu(SameDay, Tenure + 1).ShouldBeFalse();
        tabu.ActiveKeys(Tenure + 1).ShouldBeEmpty();
        tabu.Count.ShouldBe(0);
    }

    [Test]
    public void Record_MirroredSwapHitsTheSameKey()
    {
        // Arrange
        var tabu = new TabuList(Tenure);

        // Act
        tabu.Record([SameDay], iteration: 0);

        // Assert
        tabu.IsTabu(Mirrored, 1).ShouldBeTrue();
    }

    [Test]
    public void Record_ReapplyingExtendsTheTenure()
    {
        // Arrange
        var tabu = new TabuList(Tenure);
        tabu.Record([SameDay], iteration: 0);

        // Act
        tabu.Record([SameDay], iteration: 2);

        // Assert
        tabu.IsTabu(SameDay, Tenure + 2).ShouldBeTrue();
        tabu.IsTabu(SameDay, Tenure + 3).ShouldBeFalse();
    }

    [Test]
    public void Record_CrossDaySwapIsIgnored()
    {
        // Arrange
        var tabu = new TabuList(Tenure);

        // Act
        tabu.Record([CrossDay], iteration: 0);

        // Assert
        tabu.Count.ShouldBe(0);
        tabu.IsTabu(CrossDay, 1).ShouldBeFalse();
    }

    [Test]
    public void Record_ZeroTenureDisablesTheList()
    {
        // Arrange
        var tabu = new TabuList(0);

        // Act
        tabu.Record([SameDay], iteration: 0);

        // Assert
        tabu.IsTabu(SameDay, 1).ShouldBeFalse();
        tabu.ActiveKeys(1).ShouldBeEmpty();
    }

    [Test]
    public void Constructor_NegativeTenure_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new TabuList(-1));
    }
}
