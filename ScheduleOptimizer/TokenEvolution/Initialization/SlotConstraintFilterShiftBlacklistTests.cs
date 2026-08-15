// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

/// <summary>
/// The 2026-08-14 blacklist hardening: scenario-5 run S5a proved that every token-writing
/// evolution operator gates through IsValidAssignment while the blacklist was checked only in
/// the auction — 9 blacklisted night shifts survived into the final plan. These guards pin the
/// new veto: a blacklisted shift is refused on every rung, everything else stays untouched.
/// </summary>
[TestFixture]
public sealed class SlotConstraintFilterShiftBlacklistTests
{
    private static readonly Guid NightShift = Guid.NewGuid();
    private static readonly Guid EarlyShift = Guid.NewGuid();

    private static CoreAgent MakeAgent(string id = "A") => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: 160,
        MaxConsecutiveDays: 6,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        FullTime = 160,
        MaxWorkDays = 5,
        MinRestDays = 2,
        PerformsShiftWork = true,
        WorkOnMonday = true,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
    };

    private static CoreWizardContext MakeContext(params CoreShiftPreference[] preferences) => new()
    {
        PeriodFrom = new DateOnly(2026, 6, 1),
        PeriodUntil = new DateOnly(2026, 6, 30),
        SchedulingMaxConsecutiveDays = 6,
        SchedulingMaxDailyHours = 10,
        ShiftPreferences = preferences,
    };

    [Test]
    public void IsValidAssignment_BlacklistedShift_IsRefused()
    {
        var agent = MakeAgent();
        var monday = new DateOnly(2026, 6, 1);
        var ctx = MakeContext(new CoreShiftPreference(agent.Id, NightShift, ShiftPreferenceKind.Blacklist));

        SlotConstraintFilter.IsValidAssignment(agent, monday, 2, NightShift, 8, ctx, []).ShouldBeFalse();
    }

    [Test]
    public void IsValidAssignment_OtherShiftAndOtherAgent_StayValid()
    {
        var agent = MakeAgent();
        var monday = new DateOnly(2026, 6, 1);
        var ctx = MakeContext(
            new CoreShiftPreference(agent.Id, NightShift, ShiftPreferenceKind.Blacklist),
            new CoreShiftPreference("B", EarlyShift, ShiftPreferenceKind.Blacklist));

        SlotConstraintFilter.IsValidAssignment(agent, monday, 0, EarlyShift, 8, ctx, []).ShouldBeTrue();
    }

    [Test]
    public void IsValidAssignment_PreferredKind_IsNoVeto()
    {
        var agent = MakeAgent();
        var monday = new DateOnly(2026, 6, 1);
        var ctx = MakeContext(new CoreShiftPreference(agent.Id, EarlyShift, ShiftPreferenceKind.Preferred));

        SlotConstraintFilter.IsValidAssignment(agent, monday, 0, EarlyShift, 8, ctx, []).ShouldBeTrue();
    }

    [Test]
    public void IsValidAssignment_EmptyShiftRef_IsNoVeto()
    {
        var agent = MakeAgent();
        var monday = new DateOnly(2026, 6, 1);
        var ctx = MakeContext(new CoreShiftPreference(agent.Id, NightShift, ShiftPreferenceKind.Blacklist));

        SlotConstraintFilter.IsValidAssignment(agent, monday, 0, Guid.Empty, 8, ctx, []).ShouldBeTrue();
    }
}
