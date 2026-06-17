// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Controller;
using Klacks.ScheduleOptimizer.TokenEvolution.Constraints;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution;

[TestFixture]
public sealed class EligibilityVetoTests
{
    private static readonly Guid GatedShift = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateOnly Monday = new(2026, 6, 1);

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

    private static CoreWizardContext MakeContext(params (string AgentId, Guid ShiftId, DateOnly Date)[] ineligible) => new()
    {
        PeriodFrom = new DateOnly(2026, 6, 1),
        PeriodUntil = new DateOnly(2026, 6, 30),
        SchedulingMaxConsecutiveDays = 6,
        SchedulingMaxDailyHours = 10,
        IneligibleAssignments = new HashSet<(string, Guid, DateOnly)>(ineligible),
    };

    private static CoreToken MakeToken(string agentId, Guid shiftRefId, DateOnly date, bool locked = false) => new(
        WorkIds: [],
        ShiftTypeIndex: 0,
        Date: date,
        TotalHours: 8,
        StartAt: date.ToDateTime(new TimeOnly(7, 0)),
        EndAt: date.ToDateTime(new TimeOnly(15, 0)),
        BlockId: Guid.NewGuid(),
        PositionInBlock: 0,
        IsLocked: locked,
        LocationContext: null,
        ShiftRefId: shiftRefId,
        AgentId: agentId);

    [Test]
    public void SlotConstraintFilter_IneligibleAssignment_Blocked()
    {
        var agent = MakeAgent();
        var ctx = MakeContext((agent.Id, GatedShift, Monday));

        SlotConstraintFilter.IsValidAssignment(agent, Monday, 0, GatedShift, 8, ctx, []).ShouldBeFalse();
    }

    [Test]
    public void SlotConstraintFilter_EligibleAssignment_Passes()
    {
        var agent = MakeAgent();
        var ctx = MakeContext((agent.Id, GatedShift, Monday));

        // Same agent, but a different (un-gated) shift id is fine.
        SlotConstraintFilter.IsValidAssignment(agent, Monday, 0, Guid.NewGuid(), 8, ctx, []).ShouldBeTrue();
    }

    [Test]
    public void SlotConstraintFilter_EmptyMatrix_AllowsEveryone()
    {
        var agent = MakeAgent();
        var ctx = MakeContext();

        SlotConstraintFilter.IsValidAssignment(agent, Monday, 0, GatedShift, 8, ctx, []).ShouldBeTrue();
    }

    [Test]
    public void Stage0_IneligibleAgent_VetoesMissingQualification()
    {
        var agent = MakeAgent();
        var ctx = MakeContext((agent.Id, GatedShift, Monday));
        var slot = new CoreShift(
            Id: GatedShift.ToString(),
            Name: "Care",
            Date: Monday.ToString("yyyy-MM-dd"),
            StartTime: "07:00",
            EndTime: "15:00",
            Hours: 8,
            RequiredAssignments: 1,
            Priority: 0);

        var verdict = new Stage0HardConstraintChecker().Check(agent, slot, [], ctx);

        verdict.ShouldNotBeNull();
        verdict!.RuleName.ShouldBe("MissingQualification");
    }

    [Test]
    public void TokenConstraintChecker_IneligibleToken_ReportsQualificationMissing()
    {
        var ctx = MakeContext(("A", GatedShift, Monday));
        var scenario = new CoreScenario { Id = "s", Tokens = [MakeToken("A", GatedShift, Monday)] };

        var violations = new TokenConstraintChecker().Check(scenario, ctx);

        violations.ShouldContain(v => v.Kind == ViolationKind.QualificationMissing);
    }

    [Test]
    public void TokenConstraintChecker_LockedIneligibleToken_NotReported()
    {
        // A deliberate, immutable assignment of an unqualified agent must NOT become a violation —
        // the GA cannot change it, so flagging it would create an unfixable, endlessly-repaired gap.
        var ctx = MakeContext(("A", GatedShift, Monday));
        var scenario = new CoreScenario { Id = "s", Tokens = [MakeToken("A", GatedShift, Monday, locked: true)] };

        var violations = new TokenConstraintChecker().Check(scenario, ctx);

        violations.ShouldNotContain(v => v.Kind == ViolationKind.QualificationMissing);
    }

    [Test]
    public void TokenConstraintChecker_EligibleToken_NoQualificationViolation()
    {
        var ctx = MakeContext(("A", GatedShift, Monday));
        var scenario = new CoreScenario { Id = "s", Tokens = [MakeToken("A", Guid.NewGuid(), Monday)] };

        var violations = new TokenConstraintChecker().Check(scenario, ctx);

        violations.ShouldNotContain(v => v.Kind == ViolationKind.QualificationMissing);
    }
}
