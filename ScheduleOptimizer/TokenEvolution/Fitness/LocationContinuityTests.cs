// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Fitness;

[TestFixture]
public class LocationContinuityTests
{
    private const string OrderA = "order-a";
    private const string OrderB = "order-b";
    private static readonly DateOnly Monday = new(2026, 4, 20);

    private static CoreToken Token(DateOnly date, string? location)
    {
        var start = new TimeOnly(8, 0);
        return new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: 0,
            Date: date,
            TotalHours: 8,
            StartAt: date.ToDateTime(start),
            EndAt: date.ToDateTime(start.AddHours(8)),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: location,
            ShiftRefId: Guid.Empty,
            AgentId: "A");
    }

    private static CoreScenario Scenario(params CoreToken[] tokens)
        => new() { Id = "s", Tokens = tokens.ToList() };

    [Test]
    public void Score_EveryLocationNull_IsExactlyOne()
    {
        var scenario = Scenario(
            Token(Monday, null), Token(Monday.AddDays(1), null), Token(Monday.AddDays(4), null));

        LocationContinuity.Score(scenario).ShouldBe(1.0);
    }

    [Test]
    public void Score_OneUniformOrder_IsExactlyOne()
    {
        var scenario = Scenario(
            Token(Monday, OrderA), Token(Monday.AddDays(1), OrderA), Token(Monday.AddDays(4), OrderA));

        LocationContinuity.Score(scenario).ShouldBe(1.0);
    }

    [Test]
    public void Score_EmptyPlan_IsExactlyOne()
    {
        LocationContinuity.Score(Scenario()).ShouldBe(1.0);
    }

    [Test]
    public void Score_SwitchInsideThePackage_CostsMoreThanSwitchBetweenPackages()
    {
        var insideThePackage = Scenario(Token(Monday, OrderA), Token(Monday.AddDays(1), OrderB));
        var betweenPackages = Scenario(Token(Monday, OrderA), Token(Monday.AddDays(4), OrderB));

        LocationContinuity.Score(insideThePackage)
            .ShouldBe(LocationContinuity.AcrossBlockWeight);
        LocationContinuity.Score(betweenPackages)
            .ShouldBe(LocationContinuity.WithinBlockWeight);
        LocationContinuity.Score(insideThePackage)
            .ShouldBeLessThan(LocationContinuity.Score(betweenPackages));
    }

    [Test]
    public void IsSamePackage_ConsecutiveOrSameDay_IsTrue()
    {
        LocationContinuity.IsSamePackage(Token(Monday, OrderA), Token(Monday, OrderA)).ShouldBeTrue();
        LocationContinuity.IsSamePackage(Token(Monday, OrderA), Token(Monday.AddDays(1), OrderA)).ShouldBeTrue();
        LocationContinuity.IsSamePackage(Token(Monday, OrderA), Token(Monday.AddDays(2), OrderA)).ShouldBeFalse();
    }
}
