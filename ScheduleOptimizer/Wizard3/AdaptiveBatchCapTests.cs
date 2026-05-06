// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Wizard3.Loop;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Wizard3;

[TestFixture]
public class AdaptiveBatchCapTests
{
    [Test]
    public void Default_StartsAtThree()
    {
        new AdaptiveBatchCap().Current.ShouldBe(3);
    }

    [Test]
    public void RecordAccept_GrowsByOne_UpToMaximum()
    {
        var cap = new AdaptiveBatchCap(initial: 3, minimum: 1, maximum: 5);

        cap.RecordAccept();
        cap.Current.ShouldBe(4);

        cap.RecordAccept();
        cap.Current.ShouldBe(5);

        cap.RecordAccept();
        cap.Current.ShouldBe(5);
    }

    [Test]
    public void RecordReject_ShrinksByOne_DownToMinimum()
    {
        var cap = new AdaptiveBatchCap(initial: 3, minimum: 1, maximum: 8);

        cap.RecordReject();
        cap.Current.ShouldBe(2);

        cap.RecordReject();
        cap.Current.ShouldBe(1);

        cap.RecordReject();
        cap.Current.ShouldBe(1);
    }

    [Test]
    public void Constructor_RejectsInvalidBounds()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new AdaptiveBatchCap(initial: 5, minimum: 1, maximum: 3));
        Should.Throw<ArgumentOutOfRangeException>(() => new AdaptiveBatchCap(minimum: 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new AdaptiveBatchCap(initial: 1, minimum: 5, maximum: 3));
    }
}
