// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Analysis;

/// <summary>
/// Pure-computation checks of the eligibility-derived measures against the scenario-3 arithmetic of
/// the specification: 73 ban triples (MA-3 and MA-4 all month, MA-2 from 21 March) must produce a
/// night pool of 3 falling to 2, exactly one provably forced night shortening in the 11-day tail,
/// keyword violations from the independent plan scan including the carry-in days, the
/// keywordIneligible rotation reason, the night cohort normalisation and the assignment-level diff.
/// No engine run is involved — these tests pin the measuring instrument itself, so a scenario-3 red
/// later is attributable to the algorithm and not to the analyzer.
/// </summary>
[TestFixture]
public sealed class EligibilityAnalyzerTests
{
    private const string Employee1 = "MA-1";
    private const string Employee2 = "MA-2";
    private const string Employee3 = "MA-3";
    private const string Employee4 = "MA-4";
    private const string Employee5 = "MA-5";
    private const string NightKeyword = "NACHT-BEF";
    private const int ExpectedBanTripleCount = 73;
    private const int FullMonthEligibleNightDays = 31;
    private const int Employee2EligibleNightDays = 20;

    private static readonly DateOnly RestrictionStart = new(2026, 3, 21);
    private static readonly DateOnly Employee2ValidUntil = new(2026, 3, 20);

    [Test]
    public void ActiveInput_PoolsFallFromThreeToTwoOnTheRestrictionStart()
    {
        var definition = BuildRestrictedDefinition();

        definition.Context.IneligibleAssignments.Count.ShouldBe(
            ExpectedBanTripleCount,
            "The builder must hand the engine exactly the 73 ban triples of the fixture.");

        var eligibility = EligibilityAnalyzer.BuildEligibility(definition);

        eligibility.PoolPerShift.Count.ShouldBe(
            AutofillSpecConstants.TotalRequiredShifts,
            "Every demanded slot must report its pool.");

        var nightBefore = eligibility.PoolPerShift.Single(
            p => p.Date == RestrictionStart.AddDays(-1) && p.ShiftType == AutofillShiftKind.Night);
        nightBefore.EligibleEmployees.ShouldBe(
            [Employee1, Employee2, Employee5],
            "Until 20 March the night pool must hold MA-1, MA-2 and MA-5 in list order.");

        var nightAfter = eligibility.PoolPerShift.Single(
            p => p.Date == RestrictionStart && p.ShiftType == AutofillShiftKind.Night);
        nightAfter.EligibleEmployees.ShouldBe(
            [Employee1, Employee5],
            "From 21 March the night pool must fall to MA-1 and MA-5.");

        var earlyAfter = eligibility.PoolPerShift.Single(
            p => p.Date == RestrictionStart && p.ShiftType == AutofillShiftKind.Early);
        earlyAfter.PoolSize.ShouldBe(
            AutofillSpecConstants.EmployeeCount,
            "The ban list only restricts nights, so every early pool must stay full.");

        eligibility.SingletonDays.ShouldBeEmpty("A pool of two is not a singleton.");
        eligibility.EmptyPoolDays.ShouldBeEmpty("No slot of the fixture is unfillable.");
    }

    [Test]
    public void InactiveInput_AllEligibilityDerivedMetricsStayEmpty()
    {
        var definition = BuildUnrestrictedDefinition();

        var eligibility = EligibilityAnalyzer.BuildEligibility(definition);
        eligibility.PoolPerShift.ShouldBeEmpty("Without a ban list no pools are reported.");
        eligibility.SingletonDays.ShouldBeEmpty();
        eligibility.EmptyPoolDays.ShouldBeEmpty();

        EligibilityAnalyzer.BuildKeyword(new CoreScenario(), definition)
            .Violations.ShouldBeEmpty("Without a ban list nothing can be violated.");

        var (forced, unexplained) = EligibilityAnalyzer.BuildShortenings([], definition);
        forced.ShouldBeEmpty();
        unexplained.ShouldBe(0);

        var (shares, spread) = EligibilityAnalyzer.BuildNightShares([], definition);
        shares.ShouldBeEmpty();
        spread.ShouldBe(0);
    }

    [Test]
    public void KeywordScan_FlagsBannedPlanTokensAndBannedCarryInDays()
    {
        var definition = BuildRestrictedDefinitionWithBannedCarryIn();
        var plan = new CoreScenario
        {
            Tokens =
            [
                NightToken(Employee2, RestrictionStart.AddDays(4)),
                NightToken(Employee1, RestrictionStart.AddDays(5)),
            ],
        };

        var keyword = EligibilityAnalyzer.BuildKeyword(plan, definition);

        keyword.Violations.Count.ShouldBe(
            2,
            "The banned MA-2 night and the banned carry-in day must be flagged; the legal MA-1 night must not.");

        var carryInViolation = keyword.Violations[0];
        carryInViolation.Employee.ShouldBe(Employee1);
        carryInViolation.Date.ShouldBe(AutofillSpecConstants.CarryInMonthUntil);
        carryInViolation.MissingKeyword.ShouldBe(
            AutofillEligibilityInput.UnspecifiedKeywordPlaceholder,
            "A ban triple without a keyword fact must fall back to the uniform placeholder.");

        var planViolation = keyword.Violations[1];
        planViolation.Employee.ShouldBe(Employee2);
        planViolation.Date.ShouldBe(RestrictionStart.AddDays(4));
        planViolation.ShiftType.ShouldBe(AutofillShiftKind.Night);
        planViolation.MissingKeyword.ShouldBe(NightKeyword);
        planViolation.ValidUntil.ShouldBe(Employee2ValidUntil);
    }

    [Test]
    public void TransitionReason_IsKeywordIneligibleOnlyWhenTheForwardStepIsFullyBanned()
    {
        var definition = BuildRestrictedDefinition();
        var nextPackage = MakePackage(
            Employee3,
            RestrictionStart.AddDays(-11),
            RestrictionStart.AddDays(-7),
            AutofillSpecConstants.MaxWorkDays,
            AutofillShiftKind.Early);

        EligibilityAnalyzer.TransitionReasonOf(
                Employee3, AutofillShiftKind.Late, forward: false, nextPackage, definition)
            .ShouldBe(
                RotationTransitionReason.KeywordIneligible,
                "MA-3 is banned from every night, so his late-to-early jump is provably forced.");

        EligibilityAnalyzer.TransitionReasonOf(
                Employee1, AutofillShiftKind.Late, forward: false, nextPackage with { Employee = Employee1 }, definition)
            .ShouldBe(
                RotationTransitionReason.Unexplained,
                "MA-1 is never banned, so the same jump has no provable cause.");

        EligibilityAnalyzer.TransitionReasonOf(
                Employee3, AutofillShiftKind.Early, forward: true, nextPackage, definition)
            .ShouldBe(RotationTransitionReason.None, "A forward transition needs no reason.");
    }

    private static WorkPackage MakePackage(
        string employee, DateOnly start, DateOnly end, int lengthDays, AutofillShiftKind kind)
        => new(
            employee,
            start,
            end,
            lengthDays,
            kind,
            MixedTypes: false,
            FirstStartAt: start.ToDateTime(TimeOnly.MinValue),
            LastEndAt: end.ToDateTime(TimeOnly.MinValue));

    [Test]
    public void Shortenings_TailWindowForcesExactlyOneNightShortening()
    {
        var definition = BuildRestrictedDefinition();
        var packages = new List<WorkPackage>
        {
            MakePackage(Employee5, RestrictionStart, RestrictionStart.AddDays(4), 5, AutofillShiftKind.Night),
            MakePackage(Employee5, AutofillSpecConstants.PeriodUntil.AddDays(-1), AutofillSpecConstants.PeriodUntil, 2, AutofillShiftKind.Night),
            MakePackage(Employee1, new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 6), 2, AutofillShiftKind.Night),
            MakePackage(Employee4, new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 11), 2, AutofillShiftKind.Early),
        };

        var (forced, unexplained) = EligibilityAnalyzer.BuildShortenings(packages, definition);

        forced.Count.ShouldBe(1, "The 11-day tail on a pool of two forces exactly one night shortening.");
        forced[0].Employee.ShouldBe(Employee5);
        forced[0].StartDate.ShouldBe(AutofillSpecConstants.PeriodUntil.AddDays(-1));
        forced[0].ProvableCause.ShouldContain(
            "11 Night shift(s)", customMessage: "The cause must spell out the tail-window arithmetic.");
        forced[0].ProvableCause.ShouldContain(
            "pool of 2", customMessage: "The cause must name the restricted pool size.");

        unexplained.ShouldBe(
            1,
            "The head-window night shortening has no pool proof; the early shortening is outside the "
            + "measure because the ban list never mentions early shifts.");
    }

    [Test]
    public void NightShares_NormaliseToEligibleDaysAndSpreadOverTheCohortOnly()
    {
        var definition = BuildRestrictedDefinition();
        var counts = new List<EmployeeShiftTypeCounts>
        {
            new(Employee1, 1, Early: 0, Late: 0, Night: 10),
            new(Employee2, 2, Early: 0, Late: 0, Night: 5),
            new(Employee3, 3, Early: 0, Late: 0, Night: 0),
            new(Employee4, 4, Early: 0, Late: 0, Night: 0),
            new(Employee5, 5, Early: 0, Late: 0, Night: 11),
        };

        var (shares, spread) = EligibilityAnalyzer.BuildNightShares(counts, definition);

        shares.Count.ShouldBe(AutofillSpecConstants.EmployeeCount, "Every employee is reported.");
        shares[0].EligibleDays.ShouldBe(FullMonthEligibleNightDays);
        shares[1].EligibleDays.ShouldBe(
            Employee2EligibleNightDays, "MA-2 is night-eligible on the first 20 days only.");
        shares[2].EligibleDays.ShouldBe(0, "MA-3 can never take a night.");
        shares[2].SharePerEligibleDay.ShouldBe(0, "No denominator means a share of zero.");

        var expectedSpread = (11.0 / FullMonthEligibleNightDays) - (5.0 / Employee2EligibleNightDays);
        spread.ShouldBe(
            expectedSpread,
            tolerance: 1e-12,
            "The spread compares only MA-1, MA-2 and MA-5 — the employees with at least one eligible night day.");
    }

    [Test]
    public void DiffAssignments_ReportsChangedAndUnfilledSlotsAtAssignmentLevel()
    {
        var baselineDefinition = BuildUnrestrictedDefinition();
        var treatmentDefinition = BuildRestrictedDefinition();
        var firstDay = AutofillSpecConstants.PeriodFrom;

        var baseline = new CoreScenario
        {
            Tokens =
            [
                NightToken(Employee1, firstDay),
                Token(Employee3, AutofillShiftKind.Late, firstDay),
            ],
        };
        var treatment = new CoreScenario
        {
            Tokens = [NightToken(Employee5, firstDay)],
        };

        var diff = AutofillPlanAnalyzer.DiffAssignments(baseline, baselineDefinition, treatment, treatmentDefinition);

        diff.ChangedCount.ShouldBe(2, "One reassigned night and one slot the treatment left unfilled.");
        diff.ChangedAssignments[0].ShouldBe(
            new ChangedAssignment(firstDay, AutofillShiftKind.Late, Employee3, null)
            {
                ShiftRefId = AutofillShiftCatalog.ShiftIdOf(AutofillShiftKind.Late),
            });
        diff.ChangedAssignments[1].ShouldBe(
            new ChangedAssignment(firstDay, AutofillShiftKind.Night, Employee1, Employee5)
            {
                ShiftRefId = AutofillShiftCatalog.ShiftIdOf(AutofillShiftKind.Night),
            });

        diff.ChangedAssignments.ShouldAllBe(
            c => c.Order == AutofillShiftCatalog.SingleOrderIndex,
            "This scenario plans a single unnamed order, so every changed slot resolves back to the order-less "
            + "shift triple. The shift reference is part of the comparison because date and shift class stopped "
            + "identifying a slot once one order could hold two slots of the same class — a day shift and a late "
            + "shift — which is what scenario 5 introduces.");
    }

    private static AutofillScenarioDefinition BuildUnrestrictedDefinition()
        => new AutofillScenarioBuilder()
            .WithEmployees(AutofillSpecConstants.EmployeeCount, AutofillSpecConstants.GuaranteedHours)
            .Build();

    private static AutofillScenarioDefinition BuildRestrictedDefinition()
        => new AutofillScenarioBuilder()
            .WithEmployees(AutofillSpecConstants.EmployeeCount, AutofillSpecConstants.GuaranteedHours)
            .WithEligibility(BuildScenario3LikeInput(banCarryInDay: false))
            .Build();

    private static AutofillScenarioDefinition BuildRestrictedDefinitionWithBannedCarryIn()
        => new AutofillScenarioBuilder()
            .WithEmployees(AutofillSpecConstants.EmployeeCount, AutofillSpecConstants.GuaranteedHours)
            .WithCarryIn(new AutofillCarryIn(
                Employee1,
                AutofillShiftKind.Night,
                AutofillSpecConstants.CarryInMonthUntil,
                AutofillSpecConstants.CarryInMonthUntil,
                AutofillSpecConstants.MaxWorkDays,
                AutofillShiftKind.Night,
                AutofillSpecConstants.MaxWorkDays - 1))
            .WithEligibility(BuildScenario3LikeInput(banCarryInDay: true))
            .Build();

    /// <summary>
    /// The 73 triples of the scenario-3 fixture: MA-3 and MA-4 banned from every night of the month,
    /// MA-2 banned from 21 March. With <paramref name="banCarryInDay"/> one February night of MA-1 is
    /// banned on top, without a keyword fact, to exercise the carry-in scan and the placeholder.
    /// </summary>
    /// <param name="banCarryInDay">True to additionally ban MA-1's night on the last carry-in day</param>
    private static AutofillEligibilityInput BuildScenario3LikeInput(bool banCarryInDay)
    {
        var nightId = AutofillShiftCatalog.NightShiftId;
        var bans = new HashSet<(string AgentId, Guid ShiftId, DateOnly Date)>();
        bans.UnionWith(AutofillEligibilityInput.BanTriples(
            Employee3, nightId, AutofillSpecConstants.PeriodFrom, AutofillSpecConstants.PeriodUntil));
        bans.UnionWith(AutofillEligibilityInput.BanTriples(
            Employee4, nightId, AutofillSpecConstants.PeriodFrom, AutofillSpecConstants.PeriodUntil));
        bans.UnionWith(AutofillEligibilityInput.BanTriples(
            Employee2, nightId, RestrictionStart, AutofillSpecConstants.PeriodUntil));
        if (banCarryInDay)
        {
            bans.Add((Employee1, nightId, AutofillSpecConstants.CarryInMonthUntil));
        }

        return new AutofillEligibilityInput(
            bans,
            new Dictionary<Guid, AutofillShiftKind>
            {
                [AutofillShiftCatalog.EarlyShiftId] = AutofillShiftKind.Early,
                [AutofillShiftCatalog.LateShiftId] = AutofillShiftKind.Late,
                [AutofillShiftCatalog.NightShiftId] = AutofillShiftKind.Night,
            })
        {
            KeywordFacts = new Dictionary<(string AgentId, Guid ShiftId), AutofillKeywordFact>
            {
                [(Employee2, nightId)] = new(NightKeyword, null, Employee2ValidUntil),
                [(Employee3, nightId)] = new(NightKeyword, null, null),
                [(Employee4, nightId)] = new(NightKeyword, null, null),
            },
        };
    }

    private static CoreToken NightToken(string employee, DateOnly date)
        => Token(employee, AutofillShiftKind.Night, date);

    private static CoreToken Token(string employee, AutofillShiftKind kind, DateOnly date)
    {
        var (startAt, endAt) = AutofillShiftCatalog.SpanOf(kind, date);
        return new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: AutofillShiftCatalog.ShiftTypeIndexOf(kind),
            Date: date,
            TotalHours: (decimal)AutofillSpecConstants.ShiftHours,
            StartAt: startAt,
            EndAt: endAt,
            BlockId: Guid.Empty,
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: null,
            ShiftRefId: AutofillShiftCatalog.ShiftIdOf(kind),
            AgentId: employee);
    }
}
