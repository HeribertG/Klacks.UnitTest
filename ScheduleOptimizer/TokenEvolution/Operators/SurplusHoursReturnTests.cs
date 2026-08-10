// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Operators;

[TestFixture]
public class SurplusHoursReturnTests
{
    private const string DonorAgent = "MA-1";
    private const string ReceiverAgent = "MA-2";
    private const double ShiftHours = 8;
    private const int ShiftTypeIndexEarly = 0;
    private const int PackageLength = 5;
    private const int PlanningDays = 10;
    private const double OneShiftGuarantee = 8;
    private const double TwoShiftGuarantee = 16;
    private const double ThreeShiftGuarantee = 24;
    private const double UnreachableGuarantee = 22;

    private static readonly DateOnly FirstDay = new(2026, 4, 20);
    private static readonly Guid OrderAShift = new("00000000-0000-0000-0000-00000000ca01");

    /// <summary>
    /// Roster of exactly two employees on one identically cut order, with the rest-day rule switched
    /// off so the block geometry of the pass is the only thing under test. Both hold the same contract,
    /// so a shift is worth the same hours on either side and the surplus is the only asymmetry.
    /// </summary>
    /// <param name="guaranteedHours">Guaranteed hours of both employees</param>
    /// <param name="boundaryLocked">Fixed work of the previous period, source of the carried-in package</param>
    private static CoreWizardContext Context(
        double guaranteedHours, IReadOnlyList<CoreLockedWork>? boundaryLocked = null) => new()
        {
            PeriodFrom = FirstDay,
            PeriodUntil = FirstDay.AddDays(PlanningDays),
            Agents = [Agent(DonorAgent, guaranteedHours), Agent(ReceiverAgent, guaranteedHours)],
            BoundaryLockedWorks = boundaryLocked ?? Array.Empty<CoreLockedWork>(),
        };

    private static CoreAgent Agent(string id, double guaranteedHours) => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: guaranteedHours,
        MaxConsecutiveDays: 6,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        FullTime = guaranteedHours,
        MaxWorkDays = PackageLength,
        PerformsShiftWork = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };

    private static CoreToken Token(string agentId, int dayOffset, bool isLocked = false)
    {
        var date = FirstDay.AddDays(dayOffset);
        var start = new TimeOnly(8, 0);
        return new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: ShiftTypeIndexEarly,
            Date: date,
            TotalHours: (decimal)ShiftHours,
            StartAt: date.ToDateTime(start),
            EndAt: date.ToDateTime(start.AddHours(ShiftHours)),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: isLocked,
            LocationContext: null,
            ShiftRefId: OrderAShift,
            AgentId: agentId);
    }

    private static CoreLockedWork PreviousPeriodWork(string agentId)
    {
        var date = FirstDay.AddDays(-1);
        var start = new TimeOnly(8, 0);
        return new CoreLockedWork(
            WorkId: $"{agentId}-carry-in",
            AgentId: agentId,
            Date: date,
            ShiftTypeIndex: ShiftTypeIndexEarly,
            TotalHours: (decimal)ShiftHours,
            StartAt: date.ToDateTime(start),
            EndAt: date.ToDateTime(start.AddHours(ShiftHours)),
            ShiftRefId: OrderAShift,
            LocationContext: null);
    }

    private static CoreScenario Plan(string id, params CoreToken[] tokens) =>
        new() { Id = id, Tokens = tokens.ToList() };

    private static decimal HoursOf(CoreScenario scenario, string agentId) =>
        scenario.Tokens.Where(t => t.AgentId == agentId).Sum(t => t.TotalHours);

    private static IEnumerable<DateOnly> DaysOf(CoreScenario scenario, string agentId) => scenario.Tokens
        .Where(t => t.AgentId == agentId)
        .OrderBy(t => t.Date)
        .Select(t => t.Date);

    private static IReadOnlyList<string> OwnershipOf(CoreScenario scenario) => scenario.Tokens
        .OrderBy(t => t.Date)
        .ThenBy(t => t.ShiftRefId)
        .Select(t => $"{t.Date:yyyy-MM-dd}|{t.ShiftRefId}|{t.AgentId}")
        .ToList();

    /// <summary>
    /// Donor a full shift above its guarantee, receiver below its own: the pass hands exactly one
    /// shift down the roster and leaves coverage, hours and shift kinds untouched.
    /// </summary>
    [Test]
    public void Apply_DonorAFullShiftAboveItsGuarantee_ReturnsOneShiftDownTheRoster()
    {
        var context = Context(TwoShiftGuarantee);
        var scenario = Plan(
            "surplus", Token(DonorAgent, 0), Token(DonorAgent, 1), Token(DonorAgent, 2));

        var returned = new SurplusHoursReturn().Apply(scenario, context);

        returned.ShouldNotBeSameAs(scenario);
        returned.Tokens.Count.ShouldBe(scenario.Tokens.Count);
        returned.Tokens.Sum(t => t.TotalHours).ShouldBe(scenario.Tokens.Sum(t => t.TotalHours));
        DaysOf(returned, ReceiverAgent).ShouldBe([FirstDay]);
        DaysOf(returned, DonorAgent).ShouldBe([FirstDay.AddDays(1), FirstDay.AddDays(2)]);
    }

    /// <summary>
    /// The receiver stays short of its guarantee rather than the donor dropping below its own: the
    /// donor keeps the flag the fitness rewards, which is the whole reason the rule exists.
    /// </summary>
    [Test]
    public void Apply_SecondShiftWouldPushTheDonorBelowItsGuarantee_StopsAtTheGuarantee()
    {
        var context = Context(TwoShiftGuarantee);
        var scenario = Plan(
            "floor", Token(DonorAgent, 0), Token(DonorAgent, 1), Token(DonorAgent, 2));

        var returned = new SurplusHoursReturn().Apply(scenario, context);

        HoursOf(returned, DonorAgent).ShouldBe((decimal)TwoShiftGuarantee);
        HoursOf(returned, ReceiverAgent).ShouldBe((decimal)OneShiftGuarantee);
        new SurplusHoursReturn().Apply(returned, context).ShouldBeSameAs(returned);
    }

    /// <summary>
    /// A surplus smaller than a single shift produces no donor at all — the structural no-op of every
    /// run whose supply falls short of the sum of the guarantees.
    /// </summary>
    [Test]
    public void Apply_SurplusSmallerThanOneShift_IsANoOp()
    {
        var context = Context(UnreachableGuarantee);
        var scenario = Plan(
            "short-supply", Token(DonorAgent, 0), Token(DonorAgent, 1), Token(DonorAgent, 2));

        new SurplusHoursReturn().Apply(scenario, context).ShouldBeSameAs(scenario);
    }

    [Test]
    public void Apply_LockedTokens_AreNeverReturned()
    {
        var context = Context(TwoShiftGuarantee);
        var scenario = Plan(
            "locked",
            Token(DonorAgent, 0, isLocked: true),
            Token(DonorAgent, 1, isLocked: true),
            Token(DonorAgent, 2, isLocked: true));

        new SurplusHoursReturn().Apply(scenario, context).ShouldBeSameAs(scenario);
    }

    /// <summary>
    /// A day enclosed by two worked days of the donor is not returnable: releasing it would split the
    /// donor's block and leave a single free day. Here the two edges of the block are already taken by
    /// the receiver, so the enclosed days are the only remaining candidates and the pass finds nothing.
    /// </summary>
    [Test]
    public void Apply_DayEnclosedByTheDonorsBlock_IsNeverReleased()
    {
        var context = Context(ThreeShiftGuarantee);
        var scenario = Plan(
            "enclosed",
            Token(DonorAgent, 0), Token(DonorAgent, 1), Token(DonorAgent, 2), Token(DonorAgent, 3),
            Token(ReceiverAgent, 0), Token(ReceiverAgent, 3));

        new SurplusHoursReturn().Apply(scenario, context).ShouldBeSameAs(scenario);
    }

    /// <summary>
    /// The same day, the same hours and the same receiver as the enclosed case — only the donor's block
    /// is split, which turns the day into an edge. It is returned, so the block geometry is provably the
    /// deciding difference and not some other filter of the pass.
    /// </summary>
    [Test]
    public void Apply_SameDayAtTheEdgeOfTheDonorsBlock_IsReturned()
    {
        var context = Context(ThreeShiftGuarantee);
        var scenario = Plan(
            "edge",
            Token(DonorAgent, 0), Token(DonorAgent, 1), Token(DonorAgent, 3), Token(DonorAgent, 4),
            Token(ReceiverAgent, 0), Token(ReceiverAgent, 3));

        var returned = new SurplusHoursReturn().Apply(scenario, context);

        returned.ShouldNotBeSameAs(scenario);
        DaysOf(returned, ReceiverAgent)
            .ShouldBe([FirstDay, FirstDay.AddDays(1), FirstDay.AddDays(3)]);
        DaysOf(returned, DonorAgent).ShouldBe([FirstDay, FirstDay.AddDays(3), FirstDay.AddDays(4)]);
    }

    /// <summary>
    /// The days an open carried-in package still owes outrank the hour balance. The donor's block is
    /// detached from the previous period's work, so both of its edge days are releasable geometry and
    /// legal for the receiver — the identical plan without that work returns one of them. Only the
    /// package membership stops the pass.
    /// </summary>
    [Test]
    public void Apply_OpenCarriedInPackage_IsNeverReturned()
    {
        var carriedIn = Context(TwoShiftGuarantee, [PreviousPeriodWork(DonorAgent)]);
        var withoutCarryIn = Context(TwoShiftGuarantee);
        var scenario = Plan(
            "carry-in", Token(DonorAgent, 1), Token(DonorAgent, 2), Token(DonorAgent, 3));

        new SurplusHoursReturn().Apply(scenario, carriedIn).ShouldBeSameAs(scenario);

        var withoutPackage = new SurplusHoursReturn().Apply(scenario, withoutCarryIn);
        withoutPackage.ShouldNotBeSameAs(scenario);
        DaysOf(withoutPackage, ReceiverAgent).ShouldBe([FirstDay.AddDays(1)]);
    }

    [Test]
    public void Apply_RunTwice_ProducesTheIdenticalPlan()
    {
        var context = Context(TwoShiftGuarantee);
        var first = Plan("run-1", Token(DonorAgent, 0), Token(DonorAgent, 1), Token(DonorAgent, 2));
        var second = Plan("run-2", Token(DonorAgent, 0), Token(DonorAgent, 1), Token(DonorAgent, 2));

        var firstResult = new SurplusHoursReturn().Apply(first, context);
        var secondResult = new SurplusHoursReturn().Apply(second, context);

        OwnershipOf(secondResult).ShouldBe(OwnershipOf(firstResult));
        new SurplusHoursReturn().Apply(firstResult, context).ShouldBeSameAs(firstResult);
    }
}
