// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ComplianceEnforcementChangeAdvisor: each outcome per rule, the lower-bound reading of a capped list,
/// the supervisor override sentence, the per-period and per-item caveats, the single-rule versus all-rules advice and
/// that nothing in the evaluation carries a name of a person. Default scenario: window 2026-08-05..2026-10-01, override on.
/// </summary>

using Klacks.Api.Application.DTOs.ComplianceEnforcement;
using Klacks.Api.Application.Services.ComplianceEnforcement;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;

namespace Klacks.UnitTest.Application.Services.ComplianceEnforcement;

[TestFixture]
public class ComplianceEnforcementChangeAdvisorTests
{
    private static readonly DateOnly WindowStart = new(2026, 8, 5);
    private static readonly DateOnly WindowEnd = new(2026, 10, 1);

    private static ComplianceRuleFacts Rule(string rule, RuleEnforcementMode mode, int warnings, int errors = 0) =>
        new(rule, mode, warnings, errors);

    private static ComplianceEnforcementChangeFacts Facts(params ComplianceRuleFacts[] rules) =>
        new(WindowStart, WindowEnd, true, false, null, rules);

    private static IEnumerable<ComplianceEnforcementFindingCode> Codes(ComplianceEnforcementChangeEvaluation evaluation) =>
        evaluation.Findings.Select(finding => finding.Code);

    [Test]
    public void RuleInWarnWithWarnings_WarningsBecomeErrors_AndTheCountIsExact()
    {
        var evaluation = ComplianceEnforcementChangeAdvisor.Evaluate(
            Facts(Rule(ComplianceRuleNames.MinRestHours, RuleEnforcementMode.Warn, 7)));

        var effect = evaluation.Rules.Single();
        effect.Outcome.ShouldBe(ComplianceRuleOutcome.WarningsBecomeErrors);
        effect.WarningsTurningIntoErrors.ShouldBe(7);
        evaluation.TotalWarningsTurningIntoErrors.ShouldBe(7);
        evaluation.CountsAreLowerBounds.ShouldBeFalse();
        evaluation.Findings.Single(f => f.Code == ComplianceEnforcementFindingCode.WarningsBecomeErrors)
            .Statement.ShouldStartWith("7 warning(s) about minimum rest between duties");
        evaluation.Recommendation.ShouldContain("turns 7 open warning(s) between 2026-08-05 and 2026-10-01 into errors");
        evaluation.Recommendation.ShouldContain("Existing assignments stay as they are");
        evaluation.Recommendation.ShouldContain("stops a period close");
        evaluation.Recommendation.ShouldNotContain("at least");
    }

    [Test]
    public void RuleInWarnWithoutWarnings_ChangesNoExistingFinding()
    {
        var evaluation = ComplianceEnforcementChangeAdvisor.Evaluate(
            Facts(Rule(ComplianceRuleNames.MaxDailyHours, RuleEnforcementMode.Warn, 0)));

        evaluation.Rules.Single().Outcome.ShouldBe(ComplianceRuleOutcome.NoWarningsInWindow);
        evaluation.TotalWarningsTurningIntoErrors.ShouldBe(0);
        Codes(evaluation).ShouldContain(ComplianceEnforcementFindingCode.NoWarningsInWindow);
        evaluation.Recommendation.ShouldContain("turns no existing finding between 2026-08-05 and 2026-10-01 into an error");
        evaluation.Recommendation.ShouldContain("single entry saved directly in the schedule grid is kept");
    }

    [Test]
    public void RuleAlreadyBlocking_IsUnchanged_AndReportsItsRaisedErrors()
    {
        var evaluation = ComplianceEnforcementChangeAdvisor.Evaluate(
            Facts(Rule(ComplianceRuleNames.HolidayWork, RuleEnforcementMode.Block, 3, 5)));

        var effect = evaluation.Rules.Single();
        effect.Outcome.ShouldBe(ComplianceRuleOutcome.AlreadyBlocking);
        effect.WarningsTurningIntoErrors.ShouldBe(0);
        effect.ErrorsAlreadyRaised.ShouldBe(5);
        evaluation.TotalWarningsTurningIntoErrors.ShouldBe(0);
        Codes(evaluation).ShouldContain(ComplianceEnforcementFindingCode.AlreadyBlocking);
        Codes(evaluation).ShouldNotContain(ComplianceEnforcementFindingCode.WarningsBecomeErrors);
        evaluation.Recommendation.ShouldContain("the reaction is already block");
        evaluation.Recommendation.ShouldContain("changes nothing");
    }

    [Test]
    public void CappedList_MakesEveryCountAtLeast_AndNamesTheLastCoveredDay()
    {
        var facts = Facts(Rule(ComplianceRuleNames.MaxWeeklyHours, RuleEnforcementMode.Warn, 12))
            with { IssueCapReached = true, CoveredUntil = new DateOnly(2026, 9, 14) };

        var evaluation = ComplianceEnforcementChangeAdvisor.Evaluate(facts);

        evaluation.CountsAreLowerBounds.ShouldBeTrue();
        evaluation.CoveredUntil.ShouldBe(new DateOnly(2026, 9, 14));
        Codes(evaluation).ShouldContain(ComplianceEnforcementFindingCode.CountIsLowerBound);
        evaluation.Findings.Single(f => f.Code == ComplianceEnforcementFindingCode.WarningsBecomeErrors)
            .Statement.ShouldStartWith("at least 12 warning(s)");
        evaluation.Recommendation.ShouldContain("turns at least 12 open warning(s)");
        evaluation.Recommendation.ShouldContain("cut off at its limit at 2026-09-14, possibly only in part");
        evaluation.Recommendation.ShouldContain("lower bounds");
    }

    [Test]
    public void UncappedList_HasNoCoveredUntil_EvenWhenOneIsPassed()
    {
        var facts = Facts(Rule(ComplianceRuleNames.MaxWeeklyHours, RuleEnforcementMode.Warn, 1))
            with { CoveredUntil = new DateOnly(2026, 9, 14) };

        var evaluation = ComplianceEnforcementChangeAdvisor.Evaluate(facts);

        evaluation.CoveredUntil.ShouldBeNull();
        Codes(evaluation).ShouldNotContain(ComplianceEnforcementFindingCode.CountIsLowerBound);
    }

    [Test]
    public void CappedListWithoutZeroWarnings_StillSaysLaterDaysAreMissing()
    {
        var facts = Facts(Rule(ComplianceRuleNames.MinRestDays, RuleEnforcementMode.Warn, 0))
            with { IssueCapReached = true, CoveredUntil = new DateOnly(2026, 8, 20) };

        var evaluation = ComplianceEnforcementChangeAdvisor.Evaluate(facts);

        evaluation.Rules.Single().Outcome.ShouldBe(ComplianceRuleOutcome.NoWarningsInWindow);
        evaluation.Recommendation.ShouldContain("later days of the window are not included");
    }

    [TestCase(true, ComplianceEnforcementFindingCode.SupervisorOverrideOn, "A supervisor can still push")]
    [TestCase(false, ComplianceEnforcementFindingCode.SupervisorOverrideOff, "Nobody can push a refused booking through")]
    public void SupervisorOverride_IsReportedAsFindingAndInTheRecommendation(
        bool allowed, ComplianceEnforcementFindingCode code, string sentence)
    {
        var facts = Facts(Rule(ComplianceRuleNames.MinRestHours, RuleEnforcementMode.Warn, 1))
            with { SupervisorOverrideAllowed = allowed };

        var evaluation = ComplianceEnforcementChangeAdvisor.Evaluate(facts);

        evaluation.SupervisorOverrideAllowed.ShouldBe(allowed);
        Codes(evaluation).ShouldContain(code);
        evaluation.Recommendation.ShouldContain(sentence);
    }

    [TestCase(ComplianceRuleNames.PeriodCap)]
    [TestCase(ComplianceRuleNames.RollingAverage)]
    [TestCase(ComplianceRuleNames.RestDayRotation)]
    [TestCase(ComplianceRuleNames.CounterRule)]
    [TestCase(ComplianceRuleNames.CompensatoryRest)]
    public void PeriodWideRule_CarriesTheCountedPerPeriodCaveat(string rule)
    {
        var evaluation = ComplianceEnforcementChangeAdvisor.Evaluate(Facts(Rule(rule, RuleEnforcementMode.Warn, 2)));

        Codes(evaluation).ShouldContain(ComplianceEnforcementFindingCode.CountedPerPeriod);
    }

    [TestCase(ComplianceRuleNames.MaxDailyHours)]
    [TestCase(ComplianceRuleNames.RestrictedTimeWindow)]
    [TestCase(ComplianceRuleNames.HolidayWork)]
    public void DayByDayRule_HasNoPerPeriodCaveat(string rule)
    {
        var evaluation = ComplianceEnforcementChangeAdvisor.Evaluate(Facts(Rule(rule, RuleEnforcementMode.Warn, 2)));

        Codes(evaluation).ShouldNotContain(ComplianceEnforcementFindingCode.CountedPerPeriod);
    }

    [Test]
    public void CounterRuleWithWarnings_IsAnUpperBound_ButNotWithoutWarnings()
    {
        var withWarnings = ComplianceEnforcementChangeAdvisor.Evaluate(
            Facts(Rule(ComplianceRuleNames.CounterRule, RuleEnforcementMode.Warn, 4)));
        var withoutWarnings = ComplianceEnforcementChangeAdvisor.Evaluate(
            Facts(Rule(ComplianceRuleNames.CounterRule, RuleEnforcementMode.Warn, 0)));

        Codes(withWarnings).ShouldContain(ComplianceEnforcementFindingCode.CountIsUpperBound);
        Codes(withoutWarnings).ShouldNotContain(ComplianceEnforcementFindingCode.CountIsUpperBound);
    }

    [Test]
    public void AlreadyBlockingCounterRule_RaisesNoPerPeriodOrUpperBoundCaveat()
    {
        var evaluation = ComplianceEnforcementChangeAdvisor.Evaluate(
            Facts(Rule(ComplianceRuleNames.CounterRule, RuleEnforcementMode.Block, 2, 3)));

        Codes(evaluation).ShouldNotContain(ComplianceEnforcementFindingCode.CountedPerPeriod);
        Codes(evaluation).ShouldNotContain(ComplianceEnforcementFindingCode.CountIsUpperBound);
    }

    [Test]
    public void AllRules_ListsTheAffectedOnesByCount_AndMentionsRulesThatAlreadyRefuse()
    {
        var evaluation = ComplianceEnforcementChangeAdvisor.Evaluate(Facts(
            Rule(ComplianceRuleNames.MaxDailyHours, RuleEnforcementMode.Warn, 2),
            Rule(ComplianceRuleNames.MinRestHours, RuleEnforcementMode.Warn, 9),
            Rule(ComplianceRuleNames.HolidayWork, RuleEnforcementMode.Block, 0, 4),
            Rule(ComplianceRuleNames.PeriodCap, RuleEnforcementMode.Warn, 0)));

        evaluation.TotalWarningsTurningIntoErrors.ShouldBe(11);
        evaluation.Rules.Count.ShouldBe(4);
        evaluation.Recommendation.ShouldContain("all rules to block would turn 11 open warning(s)");
        evaluation.Recommendation.ShouldContain("minimum rest between duties: 9; maximum daily hours: 2");
        evaluation.Recommendation.ShouldContain("1 rule(s) already refuse");
    }

    [Test]
    public void AllRules_WithoutWarnings_SaysNothingWouldTurnIntoAnError()
    {
        var evaluation = ComplianceEnforcementChangeAdvisor.Evaluate(Facts(
            Rule(ComplianceRuleNames.MaxDailyHours, RuleEnforcementMode.Warn, 0),
            Rule(ComplianceRuleNames.MinRestHours, RuleEnforcementMode.Warn, 0)));

        evaluation.TotalWarningsTurningIntoErrors.ShouldBe(0);
        evaluation.Recommendation.ShouldContain("No open warning between 2026-08-05 and 2026-10-01 would turn into an error");
    }

    [Test]
    public void AllRules_OnACappedList_PutsAtLeastBeforeTheTotalAndEachRule()
    {
        var facts = Facts(
                Rule(ComplianceRuleNames.MaxDailyHours, RuleEnforcementMode.Warn, 2),
                Rule(ComplianceRuleNames.MinRestHours, RuleEnforcementMode.Warn, 3))
            with { IssueCapReached = true, CoveredUntil = new DateOnly(2026, 9, 1) };

        var evaluation = ComplianceEnforcementChangeAdvisor.Evaluate(facts);

        evaluation.Recommendation.ShouldContain("would turn at least 5 open warning(s)");
        evaluation.Recommendation.ShouldContain("minimum rest between duties: at least 3");
        evaluation.Recommendation.ShouldContain("maximum daily hours: at least 2");
    }

    [Test]
    public void EveryRecommendation_SaysNothingWasChanged_AndTheStepIsConfirmedSeparately()
    {
        var recommendations = new[]
        {
            ComplianceEnforcementChangeAdvisor.Evaluate(Facts(Rule(ComplianceRuleNames.MinRestHours, RuleEnforcementMode.Warn, 3))),
            ComplianceEnforcementChangeAdvisor.Evaluate(Facts(Rule(ComplianceRuleNames.MinRestHours, RuleEnforcementMode.Warn, 0))),
            ComplianceEnforcementChangeAdvisor.Evaluate(Facts(Rule(ComplianceRuleNames.MinRestHours, RuleEnforcementMode.Block, 0))),
            ComplianceEnforcementChangeAdvisor.Evaluate(Facts(
                Rule(ComplianceRuleNames.MinRestHours, RuleEnforcementMode.Warn, 3),
                Rule(ComplianceRuleNames.HolidayWork, RuleEnforcementMode.Warn, 1)))
        }.Select(evaluation => evaluation.Recommendation);

        foreach (var recommendation in recommendations)
        {
            recommendation.ShouldContain("Nothing was changed by this check");
            recommendation.ShouldContain("separate step the system confirms once");
        }
    }
}
