// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for PeriodCloseReadinessAdvisor: each verdict branch, the order of the branches and each finding.
/// Default scenario: company day 2026-09-02, the August period, stored lag 1, due and clean.
/// </summary>

using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.Services.PeriodClosing;

namespace Klacks.UnitTest.Application.Services.PeriodClosing;

[TestFixture]
public class PeriodCloseReadinessAdvisorTests
{
    private static readonly DateOnly AugustStart = new(2026, 8, 1);
    private static readonly DateOnly AugustEnd = new(2026, 8, 31);
    private static readonly DateOnly DueToday = new(2026, 9, 2);

    private static PeriodCloseReadinessFacts Facts() => new(
        Guid.NewGuid(), "Bern", AugustStart, AugustEnd, DueToday, 1, PeriodCloseStop.None, false, 0, 0, 0, []);

    private static IEnumerable<PeriodCloseFindingCode> Codes(PeriodCloseReadinessEvaluation evaluation) =>
        evaluation.Findings.Select(finding => finding.Code);

    [Test]
    public void CleanDuePeriod_IsCloseNow_WithoutFindings()
    {
        var evaluation = PeriodCloseReadinessAdvisor.Evaluate(Facts());

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.CloseNow);
        evaluation.Findings.ShouldBeEmpty();
        evaluation.Recommendation.ShouldContain("ready to be closed now");
        evaluation.Recommendation.ShouldContain("Nothing was sealed");
    }

    [Test]
    public void OpenErrors_AreFixFirst_AndSayTheCloseIsRefusedUnlessTheUserDecides()
    {
        var evaluation = PeriodCloseReadinessAdvisor.Evaluate(Facts() with { ErrorCount = 3 });

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.FixErrorsFirst);
        evaluation.ErrorCount.ShouldBe(3);
        var finding = evaluation.Findings.Single(f => f.Code == PeriodCloseFindingCode.OpenErrors);
        finding.IsBlocking.ShouldBeTrue();
        finding.Statement.ShouldContain("refused");
        finding.Statement.ShouldContain("explicitly decides");
        evaluation.Recommendation.ShouldContain("fix the 3 open error(s)");
    }

    [Test]
    public void UnsealedServices_AreConfirmServicesFirst_ABlockingFindingThatNoAcknowledgementLifts()
    {
        var evaluation = PeriodCloseReadinessAdvisor.Evaluate(Facts() with { UnsealedWorkCount = 4 });

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.ConfirmServicesFirst);
        evaluation.UnsealedWorkCount.ShouldBe(4);
        var finding = evaluation.Findings.Single(f => f.Code == PeriodCloseFindingCode.UnsealedWorks);
        finding.IsBlocking.ShouldBeTrue();
        finding.Statement.ShouldContain("4 service(s)");
        finding.Statement.ShouldContain("no acknowledgement lifts that");
        evaluation.Recommendation.ShouldContain("4 service(s)");
        evaluation.Recommendation.ShouldContain("Do not close yet");
    }

    [Test]
    public void UnsealedServices_ComeBeforeOpenErrors_AsInTheAutomaticClose()
    {
        var evaluation = PeriodCloseReadinessAdvisor.Evaluate(Facts() with { UnsealedWorkCount = 2, ErrorCount = 3 });

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.ConfirmServicesFirst);
        Codes(evaluation).First().ShouldBe(PeriodCloseFindingCode.UnsealedWorks);
        Codes(evaluation).ShouldContain(PeriodCloseFindingCode.OpenErrors);
    }

    [Test]
    public void UnsealedServices_OfARunningPeriod_StayAWait_ButAreNamed()
    {
        var evaluation = PeriodCloseReadinessAdvisor.Evaluate(
            Facts() with { Today = new DateOnly(2026, 8, 20), UnsealedWorkCount = 5 });

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.WaitUntilDue);
        evaluation.Recommendation.ShouldContain("5 service(s) are not confirmed yet");
    }

    [Test]
    public void StandInServices_AreAssignFirst_ABlockingFindingThatNamesTheRemedies_AndOffersNoCloseAnyway()
    {
        var evaluation = PeriodCloseReadinessAdvisor.Evaluate(Facts() with { StandInWorkCount = 3 });

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.AssignStandInWorksFirst);
        evaluation.StandInWorkCount.ShouldBe(3);
        var finding = evaluation.Findings.Single(f => f.Code == PeriodCloseFindingCode.StandInWorks);
        finding.IsBlocking.ShouldBeTrue();
        finding.Statement.ShouldContain("3 service(s)");
        finding.Statement.ShouldContain("moved to real people");
        finding.Statement.ShouldContain("converted");
        finding.Statement.ShouldContain("no acknowledgement lifts that");
        evaluation.Recommendation.ShouldContain("Do not close yet");
        evaluation.Recommendation.ShouldContain("3 service(s)");
        evaluation.Recommendation.ShouldNotContain("seal anyway");
        evaluation.Recommendation.ShouldNotContain("ready to be closed");
    }

    [Test]
    public void UnsealedServices_ComeBeforeStandInServices_AsInTheCloseGuard_AndBothAreNamed()
    {
        var evaluation = PeriodCloseReadinessAdvisor.Evaluate(Facts() with { UnsealedWorkCount = 2, StandInWorkCount = 3 });

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.ConfirmServicesFirst);
        Codes(evaluation).Take(2).ShouldBe(new[] { PeriodCloseFindingCode.UnsealedWorks, PeriodCloseFindingCode.StandInWorks });
        evaluation.Recommendation.ShouldContain("3 service(s) lie on placeholders");
    }

    [Test]
    public void StandInServices_ComeBeforeOpenErrors()
    {
        var evaluation = PeriodCloseReadinessAdvisor.Evaluate(Facts() with { StandInWorkCount = 1, ErrorCount = 2 });

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.AssignStandInWorksFirst);
        Codes(evaluation).First().ShouldBe(PeriodCloseFindingCode.StandInWorks);
        Codes(evaluation).ShouldContain(PeriodCloseFindingCode.OpenErrors);
    }

    [Test]
    public void StandInServices_OfARunningPeriod_StayAWait_ButAreNamed()
    {
        var evaluation = PeriodCloseReadinessAdvisor.Evaluate(
            Facts() with { Today = new DateOnly(2026, 8, 20), StandInWorkCount = 4 });

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.WaitUntilDue);
        evaluation.Recommendation.ShouldContain("4 service(s) lie on placeholders");
    }

    [Test]
    public void Warnings_NeverStopTheClose()
    {
        var evaluation = PeriodCloseReadinessAdvisor.Evaluate(Facts() with { WarningCount = 4 });

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.CloseNow);
        var finding = evaluation.Findings.Single();
        finding.Code.ShouldBe(PeriodCloseFindingCode.Warnings);
        finding.IsBlocking.ShouldBeFalse();
        evaluation.Recommendation.ShouldContain("4 warning(s)");
    }

    [TestCase(2026, 8, 20)]
    [TestCase(2026, 8, 31)]
    public void RunningPeriod_IsWait_WithTheFirstDueDay(int year, int month, int day)
    {
        var evaluation = PeriodCloseReadinessAdvisor.Evaluate(Facts() with { Today = new DateOnly(year, month, day) });

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.WaitUntilDue);
        evaluation.EarliestCloseDate.ShouldBe(new DateOnly(2026, 9, 1));
        evaluation.Recommendation.ShouldContain("2026-09-01");
        evaluation.Recommendation.ShouldContain("automatic close");
        evaluation.Recommendation.ShouldContain("a person can still close the period by hand");
        evaluation.Findings.Single().IsBlocking.ShouldBeFalse();
    }

    [Test]
    public void LagNotReached_IsWait_EvenAfterThePeriodEnded()
    {
        var evaluation = PeriodCloseReadinessAdvisor.Evaluate(Facts() with { LagDays = 5 });

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.WaitUntilDue);
        evaluation.EarliestCloseDate.ShouldBe(new DateOnly(2026, 9, 5));
    }

    [Test]
    public void NotDue_GoesBeforeErrors_LikeTheAutomaticClose()
    {
        var evaluation = PeriodCloseReadinessAdvisor.Evaluate(Facts() with { LagDays = 5, ErrorCount = 2 });

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.WaitUntilDue);
        Codes(evaluation).ShouldBe([PeriodCloseFindingCode.NotDueYet, PeriodCloseFindingCode.OpenErrors]);
        evaluation.Recommendation.ShouldContain("2 error(s) are already open");
    }

    [Test]
    public void LastDaySealed_IsNothingToSeal()
    {
        var evaluation = PeriodCloseReadinessAdvisor.Evaluate(Facts() with
        {
            Stop = PeriodCloseStop.LastDaySealed,
            SealedDayCount = 31
        });

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.NothingToSeal);
        Codes(evaluation).ShouldBe([PeriodCloseFindingCode.AlreadySealed]);
    }

    [Test]
    public void LastDaySealed_WithUnsealedEarlierDays_NamesThemInsteadOfHidingThem()
    {
        var evaluation = PeriodCloseReadinessAdvisor.Evaluate(Facts() with
        {
            Stop = PeriodCloseStop.LastDaySealed,
            SealedDayCount = 1,
            UnsealedDaysWithWork = [AugustStart, AugustStart.AddDays(1)]
        });

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.NothingToSeal);
        Codes(evaluation).ShouldContain(PeriodCloseFindingCode.UnsealedDaysWithWork);
        evaluation.Recommendation.ShouldContain("2 earlier day(s)");
    }

    [Test]
    public void NoDirectWork_WithWorkOnChildGroups_IsTheParentTrap()
    {
        var evaluation = PeriodCloseReadinessAdvisor.Evaluate(Facts() with
        {
            Stop = PeriodCloseStop.NoDirectWork,
            HasWorkOnChildGroups = true
        });

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.NothingToSeal);
        Codes(evaluation).ShouldBe([PeriodCloseFindingCode.WorkOnlyOnChildGroups]);
        evaluation.Recommendation.ShouldContain("child groups");
    }

    [Test]
    public void NoDirectWork_WithoutAnyWork_IsNothingToSeal()
    {
        var evaluation = PeriodCloseReadinessAdvisor.Evaluate(Facts() with { Stop = PeriodCloseStop.NoDirectWork });

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.NothingToSeal);
        Codes(evaluation).ShouldBe([PeriodCloseFindingCode.NoWorkInPeriod]);
    }

    [TestCase(PeriodCloseStop.LastDaySealed)]
    [TestCase(PeriodCloseStop.NoDirectWork)]
    public void NothingToSeal_IsThePolicyOfTheAutomaticClose_NotARefusalOfAPerson(PeriodCloseStop stop)
    {
        var evaluation = PeriodCloseReadinessAdvisor.Evaluate(Facts() with { Stop = stop });

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.NothingToSeal);
        evaluation.Findings.ShouldNotContain(finding => finding.IsBlocking);
        evaluation.Recommendation.ShouldContain("a person can still close the period by hand");
    }

    [Test]
    public void Reopened_DoesNotStopAPerson_ButIsNamed()
    {
        var evaluation = PeriodCloseReadinessAdvisor.Evaluate(Facts() with { Stop = PeriodCloseStop.ReopenedBefore });

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.CloseNow);
        var finding = evaluation.Findings.Single();
        finding.Code.ShouldBe(PeriodCloseFindingCode.ReopenedBefore);
        finding.IsBlocking.ShouldBeFalse();
    }

    [Test]
    public void PartiallySealed_DoesNotStopAPerson_ButIsNamed()
    {
        var evaluation = PeriodCloseReadinessAdvisor.Evaluate(Facts() with { SealedDayCount = 5 });

        evaluation.Verdict.ShouldBe(PeriodCloseVerdict.CloseNow);
        Codes(evaluation).ShouldBe([PeriodCloseFindingCode.PartiallySealed]);
    }

    [Test]
    public void NoStoredLag_DueFromTheDayAfterThePeriodEnd()
    {
        var after = PeriodCloseReadinessAdvisor.Evaluate(Facts() with { LagDays = null, Today = new DateOnly(2026, 9, 1) });
        var onLastDay = PeriodCloseReadinessAdvisor.Evaluate(Facts() with { LagDays = null, Today = AugustEnd });

        after.Verdict.ShouldBe(PeriodCloseVerdict.CloseNow);
        Codes(after).ShouldBe([PeriodCloseFindingCode.NoCloseLagStored]);
        onLastDay.Verdict.ShouldBe(PeriodCloseVerdict.WaitUntilDue);
    }

    [Test]
    public void UnsealedDays_AreCountedInFull_ButListedOnlyUpToTheCap()
    {
        var days = Enumerable.Range(0, PeriodCloseReadinessAdvisor.MaxListedUnsealedDays + 9)
            .Select(offset => AugustStart.AddDays(offset))
            .ToList();

        var evaluation = PeriodCloseReadinessAdvisor.Evaluate(Facts() with { UnsealedDaysWithWork = days });

        evaluation.UnsealedDayCount.ShouldBe(days.Count);
        evaluation.UnsealedDays.Count.ShouldBe(PeriodCloseReadinessAdvisor.MaxListedUnsealedDays);
        Codes(evaluation).ShouldBe([PeriodCloseFindingCode.UnsealedDaysWithWork]);
    }
}
