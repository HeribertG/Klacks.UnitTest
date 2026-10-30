// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Services.RouteOptimization.ContainerAutofill;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ContainerBundlingAdvisorTests
{
    private const int Candidates = 4;
    private const int BreakMinutes = 30;

    [Test]
    public void AllCandidatesOnTheTour_AllFit()
    {
        var evaluation = ContainerBundlingAdvisor.Evaluate(Facts(route: Tour(visits: Candidates)));

        evaluation.Verdict.ShouldBe(ContainerBundlingVerdict.AllFit);
        evaluation.TravelTimes.ShouldBe(ContainerBundlingTravelTimes.Calculated);
        evaluation.Findings.ShouldAllBe(finding => !finding.IsBlocking);
        Codes(evaluation).ShouldContain(ContainerBundlingFindingCode.ServicesFit);
        Codes(evaluation).ShouldContain(ContainerBundlingFindingCode.TravelTime);
    }

    [Test]
    public void SomeServicesOut_PartlyFits_WithOneFindingPerReason()
    {
        var evaluation = ContainerBundlingAdvisor.Evaluate(Facts(
            route: Tour(visits: 2),
            leftOut:
            [
                LeftOut(ContainerBundlingLeftOutReason.TimeWindowConflict),
                LeftOut(ContainerBundlingLeftOutReason.NotLocated),
            ]));

        evaluation.Verdict.ShouldBe(ContainerBundlingVerdict.PartlyFits);
        evaluation.LeftOutCount.ShouldBe(2);
        Codes(evaluation).ShouldContain(ContainerBundlingFindingCode.TimeWindowConflicts);
        Codes(evaluation).ShouldContain(ContainerBundlingFindingCode.MissingCoordinates);
    }

    [Test]
    public void OneVisitOnly_IsNoBundle()
    {
        var evaluation = ContainerBundlingAdvisor.Evaluate(Facts(route: Tour(visits: 1)));

        evaluation.Verdict.ShouldBe(ContainerBundlingVerdict.SingleVisitOnly);
    }

    [Test]
    public void EstimatedTravelTimes_AreNeverReportedAsAResult()
    {
        var evaluation = ContainerBundlingAdvisor.Evaluate(Facts(
            estimated: true,
            route: Tour(visits: Candidates),
            leftOut:
            [
                LeftOut(ContainerBundlingLeftOutReason.NoRoomOnTour),
                LeftOut(ContainerBundlingLeftOutReason.NoAddress),
            ]));

        evaluation.Verdict.ShouldBe(ContainerBundlingVerdict.TravelTimesNotCalculated);
        evaluation.TravelTimes.ShouldBe(ContainerBundlingTravelTimes.NotCalculated);
        evaluation.Route.ShouldBeNull();
        evaluation.LeftOut.ShouldHaveSingleItem().Reason.ShouldBe(ContainerBundlingLeftOutReason.NoAddress);
        Codes(evaluation).ShouldNotContain(ContainerBundlingFindingCode.ServicesFit);
        Codes(evaluation).ShouldNotContain(ContainerBundlingFindingCode.BreakFits);
        Codes(evaluation).ShouldNotContain(ContainerBundlingFindingCode.NoRoomOnTour);
        evaluation.Recommendation.ShouldContain("not calculated");
    }

    [Test]
    public void EstimatedPlanThatSelectedNothing_IsNotCalledNothingFits()
    {
        var evaluation = ContainerBundlingAdvisor.Evaluate(Facts(
            outcome: ContainerAutofillPlanOutcome.NothingSelected, estimated: true));

        evaluation.Verdict.ShouldBe(ContainerBundlingVerdict.TravelTimesNotCalculated);
    }

    [Test]
    public void NothingSelectedOnRealTravelTimes_NothingFits()
    {
        var evaluation = ContainerBundlingAdvisor.Evaluate(Facts(outcome: ContainerAutofillPlanOutcome.NothingSelected));

        evaluation.Verdict.ShouldBe(ContainerBundlingVerdict.NothingFits);
        evaluation.TravelTimes.ShouldBe(ContainerBundlingTravelTimes.Calculated);
    }

    [TestCase(ContainerAutofillPlanOutcome.NoLocatableTasks, ContainerBundlingVerdict.NoLocatableServices)]
    [TestCase(ContainerAutofillPlanOutcome.BaseNotLocated, ContainerBundlingVerdict.BaseNotFound)]
    public void PlanThatNeverReachedTheMatrix_SaysNotCalculated(
        ContainerAutofillPlanOutcome outcome, ContainerBundlingVerdict expected)
    {
        var evaluation = ContainerBundlingAdvisor.Evaluate(Facts(outcome: outcome));

        evaluation.Verdict.ShouldBe(expected);
        evaluation.TravelTimes.ShouldBe(ContainerBundlingTravelTimes.NotCalculated);
        evaluation.Findings.First().IsBlocking.ShouldBeTrue();
    }

    [Test]
    public void NoServices_WinsOverTheOutcome()
    {
        var evaluation = ContainerBundlingAdvisor.Evaluate(Facts(
            outcome: ContainerAutofillPlanOutcome.NoTimeRangeTasks, candidates: 0));

        evaluation.Verdict.ShouldBe(ContainerBundlingVerdict.NoServices);
    }

    [Test]
    public void VisitsTheBundlingWouldRefuse_AreNotCalledAFit()
    {
        var evaluation = ContainerBundlingAdvisor.Evaluate(Facts(route: Tour(visits: Candidates), placementError: "refused"));

        evaluation.Verdict.ShouldBe(ContainerBundlingVerdict.VisitsCannotBePlaced);
        evaluation.Findings.First().Statement.ShouldContain("refused");
        Codes(evaluation).ShouldNotContain(ContainerBundlingFindingCode.ServicesFit);
    }

    [TestCase(BreakMinutes, ContainerBundlingFindingCode.BreakFits)]
    [TestCase(BreakMinutes - 1, ContainerBundlingFindingCode.BreakDoesNotFit)]
    public void Break_DependsOnTheFreeMinutes(int remainingMinutes, ContainerBundlingFindingCode expected)
    {
        var evaluation = ContainerBundlingAdvisor.Evaluate(Facts(route: Tour(visits: Candidates, remainingMinutes: remainingMinutes)));

        Codes(evaluation).ShouldContain(expected);
    }

    [Test]
    public void BreakThatDoesNotFit_ReachesTheRecommendation()
    {
        var evaluation = ContainerBundlingAdvisor.Evaluate(Facts(route: Tour(visits: Candidates, remainingMinutes: 0)));

        evaluation.Recommendation.ShouldContain("does not fit");
    }

    [Test]
    public void LongLeftOutList_IsCappedButTheCountStaysComplete()
    {
        var total = ContainerBundlingAdviceLimits.MaxListedLeftOut + 1;
        var evaluation = ContainerBundlingAdvisor.Evaluate(Facts(
            route: Tour(visits: 2),
            leftOut: Enumerable.Range(0, total).Select(_ => LeftOut(ContainerBundlingLeftOutReason.NoRoomOnTour)).ToList()));

        evaluation.LeftOut.Count.ShouldBe(ContainerBundlingAdviceLimits.MaxListedLeftOut);
        evaluation.LeftOutCount.ShouldBe(total);
    }

    [TestCase("CHF")]
    [TestCase("EUR")]
    [TestCase("cost")]
    [TestCase("salary")]
    public void Recommendation_MakesNoMoneyStatement(string word)
    {
        var evaluation = ContainerBundlingAdvisor.Evaluate(Facts(route: Tour(visits: Candidates)));

        evaluation.Recommendation.ShouldNotContain(word, Case.Insensitive);
        evaluation.Findings.ShouldAllBe(finding => !finding.Statement.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static List<ContainerBundlingFindingCode> Codes(ContainerBundlingEvaluation evaluation) =>
        evaluation.Findings.Select(finding => finding.Code).ToList();

    private static ContainerBundlingRouteFacts Tour(int visits, int remainingMinutes = 60) =>
        new([], visits, 40, 180, remainingMinutes, 12.5);

    private static ContainerBundlingLeftOut LeftOut(ContainerBundlingLeftOutReason reason) =>
        new(Guid.NewGuid(), "Client", reason);

    private static ContainerBundlingFacts Facts(
        ContainerAutofillPlanOutcome outcome = ContainerAutofillPlanOutcome.Routed,
        bool estimated = false,
        int candidates = Candidates,
        ContainerBundlingRouteFacts? route = null,
        string? placementError = null,
        IReadOnlyList<ContainerBundlingLeftOut>? leftOut = null) =>
        new(
            "Winterthur", 4, new TimeOnly(8, 0), new TimeOnly(17, 0), new DateOnly(2026, 11, 2), null, "car", [],
            BreakMinutes, outcome, estimated, candidates, route, placementError, leftOut ?? []);
}
