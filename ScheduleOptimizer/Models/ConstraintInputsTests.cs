// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Models;

[TestFixture]
public class ConstraintInputsTests
{
    [Test]
    public void CoreScheduleCommand_StoresAgentDateAndKeyword()
    {
        var cmd = new CoreScheduleCommand(
            AgentId: "agent-007",
            Date: new DateOnly(2026, 4, 21),
            Keyword: ScheduleCommandKeyword.Free);

        cmd.AgentId.ShouldBe("agent-007");
        cmd.Keyword.ShouldBe(ScheduleCommandKeyword.Free);
    }

    [Test]
    public void ScheduleCommandKeyword_HasAllEightExpectedValues()
    {
        Enum.GetValues<ScheduleCommandKeyword>().ShouldBeEquivalentTo(new[]
        {
            ScheduleCommandKeyword.Free,
            ScheduleCommandKeyword.NotFree,
            ScheduleCommandKeyword.OnlyEarly,
            ScheduleCommandKeyword.NoEarly,
            ScheduleCommandKeyword.OnlyLate,
            ScheduleCommandKeyword.NoLate,
            ScheduleCommandKeyword.OnlyNight,
            ScheduleCommandKeyword.NoNight,
        });
    }

    [Test]
    public void CoreShiftPreference_StoresAgentShiftKind()
    {
        var pref = new CoreShiftPreference(
            AgentId: "agent-007",
            ShiftRefId: Guid.NewGuid(),
            Kind: ShiftPreferenceKind.Preferred);

        pref.Kind.ShouldBe(ShiftPreferenceKind.Preferred);
    }

    [Test]
    public void CoreBreakBlocker_BlocksDateRangeForAgent()
    {
        var blocker = new CoreBreakBlocker(
            AgentId: "agent-007",
            FromInclusive: new DateOnly(2026, 4, 21),
            UntilInclusive: new DateOnly(2026, 4, 25),
            Reason: "Vacation");

        blocker.UntilInclusive.ShouldBe(new DateOnly(2026, 4, 25));
        blocker.Reason.ShouldBe("Vacation");
    }

    [Test]
    public void CoreLockedWork_RepresentsExistingWorkAsTokenInput()
    {
        var locked = new CoreLockedWork(
            WorkId: "work-existing-1",
            AgentId: "agent-007",
            Date: new DateOnly(2026, 4, 21),
            ShiftTypeIndex: 1,
            TotalHours: 8m,
            StartAt: new DateTime(2026, 4, 21, 14, 0, 0),
            EndAt: new DateTime(2026, 4, 21, 22, 0, 0),
            ShiftRefId: Guid.NewGuid(),
            LocationContext: "store-9");

        locked.WorkId.ShouldBe("work-existing-1");
        locked.ShiftTypeIndex.ShouldBe(1);
    }
}
