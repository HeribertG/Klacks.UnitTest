// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Conductor;

[TestFixture]
public class EmergencyUnlockManagerTests
{
    [Test]
    public void CanUnlock_RowAboveThreshold_Forbidden()
    {
        var state = new EmergencyUnlockState(3);
        var manager = new EmergencyUnlockManager(state, 0.5);

        manager.CanUnlock(0, rowScore: 0.6, medianScore: 0.8).ShouldBeFalse();
    }

    [Test]
    public void CanUnlock_RowBelowThreshold_Allowed()
    {
        var state = new EmergencyUnlockState(3);
        var manager = new EmergencyUnlockManager(state, 0.5);

        manager.CanUnlock(0, rowScore: 0.3, medianScore: 0.8).ShouldBeTrue();
    }

    [Test]
    public void CanUnlock_AfterUse_RejectsSameRow()
    {
        var state = new EmergencyUnlockState(3);
        var manager = new EmergencyUnlockManager(state, 0.5);

        manager.MarkUsed(1);

        manager.CanUnlock(1, rowScore: 0.0, medianScore: 1.0).ShouldBeFalse();
        manager.CanUnlock(2, rowScore: 0.0, medianScore: 1.0).ShouldBeTrue();
    }

    [Test]
    public void Constructor_InvalidThreshold_Throws()
    {
        var state = new EmergencyUnlockState(1);

        Should.Throw<ArgumentException>(() => new EmergencyUnlockManager(state, -0.1));
        Should.Throw<ArgumentException>(() => new EmergencyUnlockManager(state, 1.1));
    }
}
