// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Diagnostics;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Diagnostics;

/// <summary>
/// The rule-7 counter the Pareto gate reads: how many packages hold more than one shift kind. The cases
/// pin the three decisions the definition makes — a package ends at a free day, a day is represented by
/// its earliest shift, and the counter is per package, not per employee.
/// </summary>
[TestFixture]
public class MixedKindPackageTraceTests
{
    private const string FirstAgent = "MA-1";
    private const string SecondAgent = "MA-2";

    private const int EarlyKind = 0;
    private const int LateKind = 1;

    private const int EarlyStartHour = 6;
    private const int LateStartHour = 14;
    private const double ShiftHours = 8;

    private static readonly DateOnly D1 = new(2026, 5, 4);

    private static CoreToken Token(string agentId, int dayOffset, int kind)
    {
        var date = D1.AddDays(dayOffset);
        var start = date.ToDateTime(new TimeOnly(kind == EarlyKind ? EarlyStartHour : LateStartHour, 0));
        return new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: kind,
            Date: date,
            TotalHours: (decimal)ShiftHours,
            StartAt: start,
            EndAt: start.AddHours(ShiftHours),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: null,
            ShiftRefId: Guid.Empty,
            AgentId: agentId);
    }

    private static int CountOf(params CoreToken[] tokens)
        => MixedKindPackageTrace.Count(new CoreScenario { Id = "mixed-trace", Tokens = [.. tokens] });

    [Test]
    public void Count_EveryPackageKeepsItsKind_IsZero()
    {
        CountOf(
            Token(FirstAgent, 0, EarlyKind),
            Token(FirstAgent, 1, EarlyKind),
            Token(SecondAgent, 0, LateKind),
            Token(SecondAgent, 1, LateKind)).ShouldBe(0);
    }

    [Test]
    public void Count_OnePackageChangesItsKindMidRun_IsOne()
    {
        CountOf(
            Token(FirstAgent, 0, EarlyKind),
            Token(FirstAgent, 1, EarlyKind),
            Token(FirstAgent, 2, LateKind)).ShouldBe(1);
    }

    /// <summary>
    /// A free day ends the package. The kind change then happens BETWEEN two packages, which is what the
    /// rotation rule asks for and never a rule-7 break.
    /// </summary>
    [Test]
    public void Count_TheKindChangesAcrossAFreeDay_IsZero()
    {
        CountOf(
            Token(FirstAgent, 0, EarlyKind),
            Token(FirstAgent, 1, EarlyKind),
            Token(FirstAgent, 3, LateKind),
            Token(FirstAgent, 4, LateKind)).ShouldBe(0);
    }

    /// <summary>
    /// Two shifts on one day are a permitted split duty, and the day counts with its earliest shift — the
    /// same reading the block-ordering term of the fitness uses.
    /// </summary>
    [Test]
    public void Count_ASplitDutyPutsTwoKindsOnOneDay_IsZero()
    {
        CountOf(
            Token(FirstAgent, 0, EarlyKind),
            Token(FirstAgent, 0, LateKind),
            Token(FirstAgent, 1, EarlyKind)).ShouldBe(0);
    }

    [Test]
    public void Count_TwoEmployeesEachBreakOnePackage_IsTwo()
    {
        CountOf(
            Token(FirstAgent, 0, EarlyKind),
            Token(FirstAgent, 1, LateKind),
            Token(SecondAgent, 0, LateKind),
            Token(SecondAgent, 1, EarlyKind)).ShouldBe(2);
    }

    /// <summary>
    /// Both packages of one employee break their kind; the counter is per package, so it reads two.
    /// </summary>
    [Test]
    public void Count_OneEmployeeBreaksBothOfItsPackages_IsTwo()
    {
        CountOf(
            Token(FirstAgent, 0, EarlyKind),
            Token(FirstAgent, 1, LateKind),
            Token(FirstAgent, 3, LateKind),
            Token(FirstAgent, 4, EarlyKind)).ShouldBe(2);
    }
}
