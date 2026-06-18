// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Constraints;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.Objective;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Objective;

[TestFixture]
public class CompositeObjectiveTests
{
    private static readonly DateOnly Day = new(2026, 4, 20);

    private static CoreAgent MakeAgent(string id, double guaranteed = 0, double maxHours = 0, double currentHours = 0)
        => new(
            Id: id,
            CurrentHours: currentHours,
            GuaranteedHours: guaranteed,
            MaxConsecutiveDays: 6,
            MinRestHours: 11,
            Motivation: 0.5,
            MaxDailyHours: 10,
            MaxWeeklyHours: 50,
            MaxOptimalGap: 2)
        {
            MaximumHours = maxHours,
            PerformsShiftWork = true,
        };

    private static CoreToken MakeToken(string agentId, Guid shiftRefId, decimal hours = 8, int shiftTypeIndex = 0)
    {
        var start = new TimeOnly(8, 0);
        var end = start.AddHours((double)hours);
        return new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: shiftTypeIndex,
            Date: Day,
            TotalHours: hours,
            StartAt: Day.ToDateTime(start),
            EndAt: Day.ToDateTime(end),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: null,
            ShiftRefId: shiftRefId,
            AgentId: agentId);
    }

    private static CoreShift MakeShift(Guid id, int requiredAssignments = 1, int priority = 0)
        => new(id.ToString(), "Shift", Day.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, requiredAssignments, priority);

    private static ObjectiveResult Evaluate(CoreScenario scenario, CoreWizardContext context)
        => new CompositeObjective().Evaluate(ObjectiveInputBuilder.FromScenario(scenario, context));

    private static int FlatCount(GateResult g) => g.MandatoryQualMissing + g.Legality + g.UnderSupply + g.OverSupply;

    // ---- Befund 1: severity lives in the gate, not in a fungible count -----------------------------

    [Test]
    public void Gate_CandidateWithLowerFlatCountAndHigherScalar_IsStillRejected_WhenItRegressesTheQualFloor()
    {
        var shiftS = Guid.NewGuid();
        var shiftT = Guid.NewGuid();

        // Baseline: A->S, B->S. S overstaffed (2/1), T understaffed (0/1). No qualification miss. flat = 2.
        var baselineCtx = new CoreWizardContext
        {
            PeriodFrom = Day,
            PeriodUntil = Day,
            Agents = [MakeAgent("A", guaranteed: 40), MakeAgent("B", guaranteed: 40)],
            Shifts = [MakeShift(shiftS), MakeShift(shiftT)],
        };
        var baseline = new CoreScenario
        {
            Id = "baseline",
            Tokens = [MakeToken("A", shiftS), MakeToken("B", shiftS)],
        };

        // Candidate: A->S, B->T but B is INELIGIBLE for T. Supply perfect (no over/under) but introduces
        // one mandatory-qualification miss. flat = 1 (looks better) AND the scalar rises (S_Fehler -> 1).
        var candidateCtx = new CoreWizardContext
        {
            PeriodFrom = Day,
            PeriodUntil = Day,
            Agents = [MakeAgent("A", guaranteed: 40), MakeAgent("B", guaranteed: 40)],
            Shifts = [MakeShift(shiftS), MakeShift(shiftT)],
            IneligibleAssignments = new HashSet<(string, Guid, DateOnly)> { ("B", shiftT, Day) },
        };
        var candidate = new CoreScenario
        {
            Id = "candidate",
            Tokens = [MakeToken("A", shiftS), MakeToken("B", shiftT)],
        };

        var baselineResult = Evaluate(baseline, baselineCtx);
        var candidateResult = Evaluate(candidate, candidateCtx);

        // The fungible flat count went DOWN and the composite scalar went UP — a naive count gate would accept it.
        FlatCount(candidateResult.Gate).ShouldBeLessThan(FlatCount(baselineResult.Gate));
        candidateResult.Scalar.ShouldBeGreaterThan(baselineResult.Scalar);

        // The tiered gate rejects it anyway, because it raised the mandatory-qualification floor.
        candidateResult.Gate.MandatoryQualMissing.ShouldBe(1);
        baselineResult.Gate.MandatoryQualMissing.ShouldBe(0);
        candidateResult.Gate.RegressesAgainst(baselineResult.Gate).ShouldBeTrue();
        baselineResult.Gate.RegressesAgainst(baselineResult.Gate).ShouldBeFalse();
    }

    [Test]
    public void Gate_GroupsViolationsIntoSeparateTiers()
    {
        var shift = Guid.NewGuid();
        var context = new CoreWizardContext
        {
            PeriodFrom = Day,
            PeriodUntil = Day,
            Agents = [MakeAgent("A", guaranteed: 40)],
            Shifts = [MakeShift(shift, requiredAssignments: 1)],
            ScheduleCommands = [new CoreScheduleCommand("A", Day, ScheduleCommandKeyword.Free)],
        };
        // A works S although a FREE command forbids the day -> 1 legality (PerDayKeyword). S covered 1/1.
        var scenario = new CoreScenario { Id = "s", Tokens = [MakeToken("A", shift)] };

        var result = Evaluate(scenario, context);

        result.Gate.Legality.ShouldBe(1);
        result.Gate.MandatoryQualMissing.ShouldBe(0);
        result.Gate.UnderSupply.ShouldBe(0);
        result.Gate.IsFeasibleStandalone.ShouldBeFalse();
    }

    // ---- S_Stundenabgleich: worst-agent floor + zero-target exclusion ------------------------------

    [Test]
    public void Stundenabgleich_WorstAgentScore_ReflectsTheUnderservedAgent_NotTheMean()
    {
        var sA = Guid.NewGuid();
        var sB = Guid.NewGuid();
        var sC = Guid.NewGuid();
        var context = new CoreWizardContext
        {
            PeriodFrom = Day,
            PeriodUntil = Day,
            Agents =
            [
                MakeAgent("A", guaranteed: 8),
                MakeAgent("B", guaranteed: 8),
                MakeAgent("C", guaranteed: 8),
            ],
            Shifts = [MakeShift(sA), MakeShift(sB), MakeShift(sC)],
        };
        // A and B hit their 8h target exactly; C gets nothing (0/8 -> deviation 1.0 -> score 0).
        var scenario = new CoreScenario
        {
            Id = "s",
            Tokens = [MakeToken("A", sA, hours: 8), MakeToken("B", sB, hours: 8)],
        };

        var result = Evaluate(scenario, context);

        result.Diagnostics.WorstStundenabgleich.ShouldBe(0.0, 1e-9);
        result.SubScores.Stundenabgleich.ShouldBeGreaterThan(result.Diagnostics.WorstStundenabgleich);
    }

    [Test]
    public void Stundenabgleich_ZeroTargetAgent_IsExcludedFromReward()
    {
        var shift = Guid.NewGuid();
        var contextWithFlexAgent = new CoreWizardContext
        {
            PeriodFrom = Day,
            PeriodUntil = Day,
            // A has a real 8h target and hits it; F is a zero-target flex agent with surplus parked on it.
            Agents = [MakeAgent("A", guaranteed: 8), MakeAgent("F", guaranteed: 0)],
            Shifts = [MakeShift(shift, requiredAssignments: 2)],
        };
        var scenario = new CoreScenario
        {
            Id = "s",
            Tokens = [MakeToken("A", shift, hours: 8), MakeToken("F", shift, hours: 8)],
        };

        var result = Evaluate(scenario, contextWithFlexAgent);

        // Only A is scored (perfect); the zero-target F neither earns reward nor drags the score.
        result.SubScores.Stundenabgleich.ShouldBe(1.0, 1e-9);
        result.Diagnostics.WorstStundenabgleich.ShouldBe(1.0, 1e-9);
    }

    [Test]
    public void Stundenabgleich_CountsBreakHoursTowardTarget()
    {
        var shift = Guid.NewGuid();
        var context = new CoreWizardContext
        {
            PeriodFrom = Day,
            PeriodUntil = Day,
            Agents = [MakeAgent("A", guaranteed: 8)],
            Shifts = [MakeShift(shift)],
            // A is on a paid break worth 8h on the day -> meets the 8h target without any work.
            BreakBlockers = [new CoreBreakBlocker("A", Day, Day, "Vacation", Hours: 8)],
        };
        var scenario = new CoreScenario { Id = "s", Tokens = [] };

        var result = Evaluate(scenario, context);

        result.SubScores.Stundenabgleich.ShouldBe(1.0, 1e-9);
    }

    // ---- S_Praeferenzen: per-agent blacklist cap + preferred reward --------------------------------

    [Test]
    public void Praeferenzen_BlacklistConcentratedOnOneAgent_IsCaughtPerAgent_NotDilutedAcrossThePlan()
    {
        var hated = Guid.NewGuid();
        var neutral = Guid.NewGuid();
        var context = new CoreWizardContext
        {
            PeriodFrom = Day,
            PeriodUntil = Day,
            Agents = [MakeAgent("A", guaranteed: 8), MakeAgent("B", guaranteed: 8)],
            Shifts = [MakeShift(hated, requiredAssignments: 1), MakeShift(neutral, requiredAssignments: 1)],
            ShiftPreferences = [new CoreShiftPreference("A", hated, ShiftPreferenceKind.Blacklist)],
        };
        // A works ONLY its blacklisted shift (1/1 -> fraction 1.0); B works a neutral shift.
        var scenario = new CoreScenario
        {
            Id = "s",
            Tokens = [MakeToken("A", hated), MakeToken("B", neutral)],
        };

        var result = Evaluate(scenario, context);

        // Per-agent: A's blacklist fraction is 1.0 regardless of how clean the rest of the plan is.
        result.Diagnostics.MaxBlacklistFraction.ShouldBe(1.0, 1e-9);
        result.Diagnostics.WorstPraeferenzen.ShouldBe(0.0, 1e-9);
    }

    [Test]
    public void Praeferenzen_PreferredShift_IsRewarded()
    {
        var preferredShift = Guid.NewGuid();
        var otherShift = Guid.NewGuid();
        var context = new CoreWizardContext
        {
            PeriodFrom = Day,
            PeriodUntil = Day,
            Agents = [MakeAgent("A", guaranteed: 8)],
            Shifts = [MakeShift(preferredShift), MakeShift(otherShift)],
            ShiftPreferences = [new CoreShiftPreference("A", preferredShift, ShiftPreferenceKind.Preferred)],
        };

        var satisfied = Evaluate(
            new CoreScenario { Id = "sat", Tokens = [MakeToken("A", preferredShift)] },
            context);
        var unsatisfied = Evaluate(
            new CoreScenario { Id = "uns", Tokens = [MakeToken("A", otherShift)] },
            context);

        satisfied.SubScores.Praeferenzen.ShouldBeGreaterThan(unsatisfied.SubScores.Praeferenzen);
        satisfied.SubScores.Praeferenzen.ShouldBe(1.0, 1e-9);
    }

    [Test]
    public void Praeferenzen_NoPreferencesAtAll_IsNeutralOne()
    {
        var shift = Guid.NewGuid();
        var context = new CoreWizardContext
        {
            PeriodFrom = Day,
            PeriodUntil = Day,
            Agents = [MakeAgent("A", guaranteed: 8)],
            Shifts = [MakeShift(shift)],
        };
        var result = Evaluate(new CoreScenario { Id = "s", Tokens = [MakeToken("A", shift)] }, context);

        result.SubScores.Praeferenzen.ShouldBe(1.0, 1e-9);
    }

    // ---- Determinism + scalar range ----------------------------------------------------------------

    [Test]
    public void Evaluate_IsDeterministic_AndScalarStaysInUnitInterval()
    {
        var shift = Guid.NewGuid();
        var context = new CoreWizardContext
        {
            PeriodFrom = Day,
            PeriodUntil = Day,
            Agents = [MakeAgent("A", guaranteed: 8), MakeAgent("B", guaranteed: 8)],
            Shifts = [MakeShift(shift, requiredAssignments: 2)],
            ShiftPreferences = [new CoreShiftPreference("B", shift, ShiftPreferenceKind.Blacklist)],
        };
        var scenario = new CoreScenario
        {
            Id = "s",
            Tokens = [MakeToken("A", shift), MakeToken("B", shift)],
        };

        var first = Evaluate(scenario, context);
        var second = Evaluate(scenario, context);

        first.Scalar.ShouldBe(second.Scalar);
        first.Scalar.ShouldBeInRange(0.0, 1.0);
        first.SubScores.Fehler.ShouldBeInRange(0.0, 1.0);
        first.SubScores.Stundenabgleich.ShouldBeInRange(0.0, 1.0);
        first.SubScores.Praeferenzen.ShouldBeInRange(0.0, 1.0);
    }

    // ---- Adapter equivalence: scenario vs bitmap produce the same gate ------------------------------

    [Test]
    public void Adapters_ScenarioAndBitmap_ProduceEquivalentViolationKinds()
    {
        var shift = Guid.NewGuid();
        var context = new CoreWizardContext
        {
            PeriodFrom = Day,
            PeriodUntil = Day,
            Agents = [MakeAgent("A", guaranteed: 8)],
            // Capacity 2 but only one assignment -> exactly one UnderSupply on both paths.
            Shifts = [MakeShift(shift, requiredAssignments: 2)],
        };

        var scenarioInput = ObjectiveInputBuilder.FromScenario(
            new CoreScenario { Id = "s", Tokens = [MakeToken("A", shift, hours: 8)] },
            context);

        var start = Day.ToDateTime(new TimeOnly(8, 0));
        var end = Day.ToDateTime(new TimeOnly(16, 0));
        var cells = new Cell[1, 1];
        cells[0, 0] = new Cell(CellSymbol.Early, shift, [], false, start, end, 8m);
        var bitmap = new HarmonyBitmap(
            [new BitmapAgent("A", "A", 8m, new HashSet<CellSymbol>())],
            [Day],
            cells);
        var bitmapInput = ObjectiveInputBuilder.FromBitmap(bitmap, context);

        var scenarioKinds = scenarioInput.Violations.Select(v => v.Kind).OrderBy(k => k).ToList();
        var bitmapKinds = bitmapInput.Violations.Select(v => v.Kind).OrderBy(k => k).ToList();

        bitmapKinds.ShouldBe(scenarioKinds);
        scenarioKinds.ShouldBe([ViolationKind.UnderSupply]);

        // And the composite gate is identical from either engine.
        var objective = new CompositeObjective();
        objective.Evaluate(bitmapInput).Gate.ShouldBe(objective.Evaluate(scenarioInput).Gate);
    }
}
