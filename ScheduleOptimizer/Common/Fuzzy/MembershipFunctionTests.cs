// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Common.Fuzzy;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Common.Fuzzy;

[TestFixture]
public class MembershipFunctionTests
{
    [Test]
    public void Trapezoid_Plateau_ReturnsOne()
    {
        var mf = new TrapezoidMf(0, 2, 4, 6);
        mf.Mu(3).ShouldBe(1.0);
    }

    [Test]
    public void Trapezoid_OutsideRange_ReturnsZero()
    {
        var mf = new TrapezoidMf(0, 2, 4, 6);
        mf.Mu(-1).ShouldBe(0.0);
        mf.Mu(7).ShouldBe(0.0);
    }

    [Test]
    public void Trapezoid_RisingEdge_LinearInterpolation()
    {
        var mf = new TrapezoidMf(0, 2, 4, 6);
        mf.Mu(1).ShouldBe(0.5, 0.001);
    }

    [Test]
    public void Triangle_Apex_ReturnsOne()
    {
        var mf = new TriangularMf(0, 5, 10);
        mf.Mu(5).ShouldBe(1.0);
    }

    [Test]
    public void Triangle_Falling_LinearInterpolation()
    {
        var mf = new TriangularMf(0, 5, 10);
        mf.Mu(7.5).ShouldBe(0.5, 0.001);
    }

    [Test]
    public void Trapezoid_InvalidOrder_Throws()
    {
        Action act = () => new TrapezoidMf(0, 4, 2, 6);
        act.ShouldThrow<ArgumentException>();
    }
}
