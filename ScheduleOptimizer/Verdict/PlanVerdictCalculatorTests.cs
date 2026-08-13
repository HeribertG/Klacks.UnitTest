// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.Verdict;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Verdict;

/// <summary>
/// The fuzzy plan verdict of the 2026-08-13 owner model: hard rules stay hard while plans are
/// built, the softening happens only here at the very end. The guards prove the three zones —
/// a scratch lowers but never caps when the period quota holds, a missed quota presses the lid
/// gradually, a legal-minimum breach caps always — plus period scaling, tie handling and the
/// explainability contract of the terms.
/// </summary>
[TestFixture]
public class PlanVerdictCalculatorTests
{
    private const int PeriodDays = 28;
    private const string AgentId = "A";
    private static readonly DateOnly PeriodStart = new(2026, 3, 2);
    private static readonly TimeOnly EarlyStart = new(7, 0);
    private static readonly TimeOnly EarlyEnd = new(15, 0);
    private static readonly TimeOnly LateStart = new(15, 0);
    private static readonly TimeOnly LateEnd = new(23, 0);

    private static CoreAgent MakeAgent(string id) => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: 0,
        MaxConsecutiveDays: 6,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 80,
        MaxOptimalGap: 2)
    {
        FullTime = 80,
        MaxWorkDays = 5,
        MinRestDays = 2,
        PerformsShiftWork = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };

    private static CoreWizardContext MakeContext() => new()
    {
        PeriodFrom = PeriodStart,
        PeriodUntil = PeriodStart.AddDays(PeriodDays - 1),
        Agents = [MakeAgent(AgentId)],
        Shifts = [],
    };

    private static CoreToken MakeToken(int dayOffset, TimeOnly start, TimeOnly end, int shiftTypeIndex)
    {
        var date = PeriodStart.AddDays(dayOffset);
        return new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: shiftTypeIndex,
            Date: date,
            TotalHours: 8m,
            StartAt: date.ToDateTime(start),
            EndAt: date.ToDateTime(end),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: null,
            ShiftRefId: Guid.NewGuid(),
            AgentId: AgentId);
    }

    private static CoreScenario MakeScenario(params int[][] packages)
    {
        var tokens = new List<CoreToken>();
        foreach (var package in packages)
        {
            foreach (var day in package)
            {
                tokens.Add(MakeToken(day, EarlyStart, EarlyEnd, shiftTypeIndex: 0));
            }
        }

        return new CoreScenario { Id = Guid.NewGuid().ToString(), Tokens = tokens };
    }

    [Test]
    public void RequiredWindows_ScaleLinearlyWithThePeriod()
    {
        var fortyEight = new RestWindowQuota(WindowHours: 48, WindowsPerReferencePeriod: 4);
        var seventyTwo = new RestWindowQuota(WindowHours: 72, WindowsPerReferencePeriod: 3);

        fortyEight.RequiredWindows(periodDays: 28, referencePeriodDays: 28).ShouldBe(4);
        fortyEight.RequiredWindows(periodDays: 14, referencePeriodDays: 28).ShouldBe(2);
        seventyTwo.RequiredWindows(periodDays: 14, referencePeriodDays: 28).ShouldBe(1.5);
    }

    [Test]
    public void Compute_FlawlessRhythm_IsCleanWithFullScore()
    {
        var scenario = MakeScenario([0, 1, 2, 3, 4], [7, 8, 9, 10, 11], [14, 15, 16, 17, 18], [21, 22, 23, 24, 25]);

        var verdict = PlanVerdictCalculator.Compute(scenario, MakeContext());

        verdict.Zone.ShouldBe(VerdictZone.Clean);
        verdict.Findings.ShouldBeEmpty();
        verdict.MinQuotaFulfillment.ShouldBe(1);
        verdict.Score.ShouldBe(1.0, 1e-9);
    }

    [Test]
    public void Compute_ScratchWithQuotaHeld_LowersButNeverCaps()
    {
        // The 40-hour gap misses the 48-hour rule locally, yet the long tail keeps the period
        // quota fulfilled — the owner case where the soft rules are allowed to win.
        var scenario = MakeScenario([0, 1, 2, 3, 4], [6, 7, 8, 9, 10], [13, 14, 15, 16, 17]);

        var verdict = PlanVerdictCalculator.Compute(scenario, MakeContext());

        verdict.Zone.ShouldBe(VerdictZone.Scratched);
        verdict.MinQuotaFulfillment.ShouldBe(1);
        verdict.Findings.Count.ShouldBe(1);
        verdict.Findings[0].Kind.ShouldBe(VerdictFindingKind.Scratch);
        verdict.Findings[0].GapHours.ShouldBe(40, 1e-9);
        verdict.Score.ShouldBe(0.95, 1e-9);
    }

    [Test]
    public void Compute_GapBelowLegalMinimum_CapsHardEvenWithQuotaHeld()
    {
        var scenario = MakeScenario([0, 1, 2, 3], [6, 7, 8, 9, 10], [13, 14, 15, 16, 17]);
        var lateEndOfFirstPackage = MakeToken(4, LateStart, LateEnd, shiftTypeIndex: 1);
        scenario.Tokens.Add(lateEndOfFirstPackage);

        var verdict = PlanVerdictCalculator.Compute(scenario, MakeContext());

        verdict.Zone.ShouldBe(VerdictZone.LegalMinimumBreach);
        verdict.Findings.ShouldContain(f => f.Kind == VerdictFindingKind.LegalMinimumBreach);
        verdict.Findings.Single(f => f.Kind == VerdictFindingKind.LegalMinimumBreach)
            .GapHours.ShouldBe(32, 1e-9);
        verdict.Score.ShouldBe(new PlanVerdictConfig().LegalBreachCap, 1e-12);
    }

    [Test]
    public void Compute_QuotaFullyMissed_PressesTheGradualLid()
    {
        // Single free days everywhere: every gap is 40 hours, no 48-hour window exists at all.
        var scenario = MakeScenario(
            [0, 1, 2, 3, 4], [6, 7, 8, 9, 10], [12, 13, 14, 15, 16], [18, 19, 20, 21, 22], [24, 25, 26, 27]);

        var verdict = PlanVerdictCalculator.Compute(scenario, MakeContext());

        verdict.Zone.ShouldBe(VerdictZone.QuotaShortfall);
        verdict.MinQuotaFulfillment.ShouldBe(0);
        verdict.QuotaCap.ShouldBe(new PlanVerdictConfig().QuotaShortfallCapFloor, 1e-12);
        verdict.Score.ShouldBe(new PlanVerdictConfig().QuotaShortfallCapFloor, 1e-9);
    }

    [Test]
    public void Compute_RunsDeterministically()
    {
        var scenario = MakeScenario([0, 1, 2, 3, 4], [6, 7, 8, 9, 10], [13, 14, 15, 16, 17]);
        var context = MakeContext();

        var first = PlanVerdictCalculator.Compute(scenario, context);
        var second = PlanVerdictCalculator.Compute(scenario, context);

        second.Score.ShouldBe(first.Score);
        second.Zone.ShouldBe(first.Zone);
        second.SoftScore.ShouldBe(first.SoftScore);
        second.Findings.Count.ShouldBe(first.Findings.Count);
    }

    [Test]
    public void Compute_EveryTermExplainsItselfAndWeightsSumToOne()
    {
        var scenario = MakeScenario([0, 1, 2, 3, 4], [7, 8, 9, 10, 11]);

        var verdict = PlanVerdictCalculator.Compute(scenario, MakeContext());

        verdict.Terms.Count.ShouldBe(4);
        verdict.Terms.ShouldAllBe(t => !string.IsNullOrWhiteSpace(t.Explanation));
        verdict.Terms.Sum(t => t.Weight).ShouldBe(1.0, 1e-9);
        foreach (var term in verdict.Terms)
        {
            term.Contribution.ShouldBe(term.Weight * term.RawScore, 1e-12);
        }
    }

    [Test]
    public void IsImprovement_TieGoesToFewerFindingsThenToTheIncumbent()
    {
        var scenario = MakeScenario([0, 1, 2, 3, 4], [6, 7, 8, 9, 10], [13, 14, 15, 16, 17]);
        var verdict = PlanVerdictCalculator.Compute(scenario, MakeContext());
        var fewerFindings = verdict with { Findings = [] };

        PlanVerdictComparer.IsImprovement(fewerFindings, verdict).ShouldBeTrue();
        PlanVerdictComparer.IsImprovement(verdict, fewerFindings).ShouldBeFalse();
        PlanVerdictComparer.IsImprovement(verdict, verdict).ShouldBeFalse();
    }
}
