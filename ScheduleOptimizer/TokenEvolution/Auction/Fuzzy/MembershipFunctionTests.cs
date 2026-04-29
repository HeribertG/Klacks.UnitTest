// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentAssertions;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Fuzzy;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Auction.Fuzzy;

[TestFixture]
public class MembershipFunctionTests
{
    [Test]
    public void Trapezoid_Plateau_ReturnsOne()
    {
        var mf = new TrapezoidMf(0, 2, 4, 6);
        mf.Mu(3).Should().Be(1.0);
    }

    [Test]
    public void Trapezoid_OutsideRange_ReturnsZero()
    {
        var mf = new TrapezoidMf(0, 2, 4, 6);
        mf.Mu(-1).Should().Be(0.0);
        mf.Mu(7).Should().Be(0.0);
    }

    [Test]
    public void Trapezoid_RisingEdge_LinearInterpolation()
    {
        var mf = new TrapezoidMf(0, 2, 4, 6);
        mf.Mu(1).Should().BeApproximately(0.5, 0.001);
    }

    [Test]
    public void Triangle_Apex_ReturnsOne()
    {
        var mf = new TriangularMf(0, 5, 10);
        mf.Mu(5).Should().Be(1.0);
    }

    [Test]
    public void Triangle_Falling_LinearInterpolation()
    {
        var mf = new TriangularMf(0, 5, 10);
        mf.Mu(7.5).Should().BeApproximately(0.5, 0.001);
    }

    [Test]
    public void Trapezoid_InvalidOrder_Throws()
    {
        Action act = () => new TrapezoidMf(0, 4, 2, 6);
        act.Should().Throw<ArgumentException>();
    }
}
