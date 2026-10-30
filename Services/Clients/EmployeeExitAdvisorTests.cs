// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins every rule of EmployeeExitAdvisor: the three cases the recording skill refuses (no, several or a not yet started
/// active membership) are blocking, a membership that already ends on the exit date changes nothing, what stays booked
/// after the exit is counted per kind (work, absences, on-call, approved and closed days), contracts that run past or
/// ended before the exit date and remaining group memberships are informational, the order puts blocking and sealed
/// entries first, and the recommendation never claims that anything was removed.
/// </summary>

using Klacks.Api.Application.DTOs.Clients;
using Klacks.Api.Application.Services.Clients;

namespace Klacks.UnitTest.Services.Clients;

[TestFixture]
public class EmployeeExitAdvisorTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateOnly Exit = new(2026, 3, 31);
    private static readonly DateOnly Horizon = new(2027, 4, 1);

    private static readonly EmployeeExitMembershipFacts LoneOpen =
        new(1, new DateOnly(2024, 1, 1), null, string.Empty);

    private static readonly EmployeeExitPlanFacts EmptyPlan = new(0, 0, null, null, 0, 0, 0, 0);

    private static readonly EmployeeExitContractFacts CoveredContract = new(0, true, null);

    private static EmployeeExitFacts Facts(
        EmployeeExitMembershipFacts? membership = null,
        EmployeeExitPlanFacts? plan = null,
        EmployeeExitContractFacts? contracts = null,
        int groups = 0) =>
        new("Anna Muster", Exit, Horizon, membership ?? LoneOpen, plan ?? EmptyPlan, contracts ?? CoveredContract, groups);

    private static EmployeeExitEvaluation Evaluate(EmployeeExitFacts facts) => EmployeeExitAdvisor.Evaluate(ClientId, facts);

    private static EmployeeExitFindingCode[] Codes(EmployeeExitEvaluation evaluation) =>
        evaluation.Findings.Select(finding => finding.Code).ToArray();

    [Test]
    public void LoneOpenMembership_NothingBooked_NoFindings_AndPlanningStopsTheNextDay()
    {
        var evaluation = Evaluate(Facts());

        evaluation.Findings.ShouldBeEmpty();
        evaluation.ClientId.ShouldBe(ClientId);
        evaluation.Recommendation.ShouldContain("planning stops from 2026-04-01");
        evaluation.Recommendation.ShouldContain("leaves nothing booked");
    }

    [Test]
    public void NoActiveMembership_IsBlocking_AndTheRecommendationSaysDoNotRecord()
    {
        var evaluation = Evaluate(Facts(membership: new EmployeeExitMembershipFacts(0, null, null, string.Empty)));

        Codes(evaluation).ShouldBe([EmployeeExitFindingCode.NoActiveMembership]);
        evaluation.Findings[0].IsBlocking.ShouldBeTrue();
        evaluation.Recommendation.ShouldStartWith("Do not record the exit of Anna Muster on 2026-03-31 as asked");
    }

    [Test]
    public void SeveralActiveMemberships_AreBlocking_AndTheRealOptionsAreInTheStatement()
    {
        const string options = "id=a, validFrom=2024-01-01, validUntil=open-ended; id=b, validFrom=2025-01-01, validUntil=2026-12-31";

        var evaluation = Evaluate(Facts(membership: new EmployeeExitMembershipFacts(2, null, null, options)));

        Codes(evaluation).ShouldBe([EmployeeExitFindingCode.MultipleActiveMemberships]);
        evaluation.Findings[0].IsBlocking.ShouldBeTrue();
        evaluation.Findings[0].Statement.ShouldContain(options);
    }

    [Test]
    public void ExitBeforeTheMembershipStart_IsBlocking()
    {
        var evaluation = Evaluate(Facts(membership: new EmployeeExitMembershipFacts(1, new DateOnly(2026, 4, 1), null, string.Empty)));

        Codes(evaluation).ShouldBe([EmployeeExitFindingCode.ExitBeforeMembershipStart]);
        evaluation.Findings[0].IsBlocking.ShouldBeTrue();
    }

    [Test]
    public void ExitOnTheMembershipStartDay_IsNotBlocking()
    {
        var evaluation = Evaluate(Facts(membership: new EmployeeExitMembershipFacts(1, Exit, null, string.Empty)));

        evaluation.Findings.ShouldBeEmpty();
    }

    [Test]
    public void MembershipThatAlreadyEndsOnTheExitDate_ChangesNothing()
    {
        var evaluation = Evaluate(Facts(membership: new EmployeeExitMembershipFacts(1, new DateOnly(2024, 1, 1), Exit, string.Empty)));

        Codes(evaluation).ShouldBe([EmployeeExitFindingCode.MembershipAlreadyEndsOnExitDate]);
        evaluation.Findings[0].IsBlocking.ShouldBeFalse();
        evaluation.Recommendation.ShouldContain("already ends on 2026-03-31");
        evaluation.Recommendation.ShouldContain("nothing would change");
    }

    [Test]
    public void MembershipEndingOnAnotherDay_IsNotTreatedAsUnchanged()
    {
        var evaluation = Evaluate(Facts(membership: new EmployeeExitMembershipFacts(1, new DateOnly(2024, 1, 1), new DateOnly(2026, 12, 31), string.Empty)));

        evaluation.Findings.ShouldBeEmpty();
    }

    [Test]
    public void WorkAfterTheExit_IsCounted_WithFirstLastAndHowManyAlreadyPassed()
    {
        var plan = EmptyPlan with { WorksAfterExit = 5, WorksAlreadyPast = 2, FirstWorkDate = new DateOnly(2026, 4, 2), LastWorkDate = new DateOnly(2026, 4, 30) };

        var evaluation = Evaluate(Facts(plan: plan));

        Codes(evaluation).ShouldBe([EmployeeExitFindingCode.FuturePlanAfterExit]);
        var statement = evaluation.Findings[0].Statement;
        statement.ShouldContain("5 work entry(ies)");
        statement.ShouldContain("first 2026-04-02, last 2026-04-30");
        statement.ShouldContain("2 of them on days that have already passed");
        statement.ShouldContain("between 2026-04-01 and 2027-04-01");
    }

    [Test]
    public void AbsencesAndOnCallAfterTheExit_AreSeparateFindings()
    {
        var plan = EmptyPlan with { AbsenceDaysAfterExit = 3, OnCallDaysAfterExit = 2 };

        var evaluation = Evaluate(Facts(plan: plan));

        Codes(evaluation).ShouldBe([EmployeeExitFindingCode.AbsencesAfterExit, EmployeeExitFindingCode.OnCallAfterExit]);
        evaluation.Findings[0].Statement.ShouldContain("3 day(s)");
        evaluation.Findings[1].Statement.ShouldContain("2 day(s)");
    }

    [Test]
    public void SealedEntries_AreListedFirstAmongTheNonBlockingFindings_AndNamedInTheRecommendation()
    {
        var plan = EmptyPlan with { WorksAfterExit = 4, WorksAlreadyPast = 0, FirstWorkDate = Exit.AddDays(1), LastWorkDate = Exit.AddDays(9), ClosedEntriesAfterExit = 2, ApprovedEntriesAfterExit = 1 };

        var evaluation = Evaluate(Facts(plan: plan));

        Codes(evaluation).ShouldBe([EmployeeExitFindingCode.SealedEntriesAfterExit, EmployeeExitFindingCode.FuturePlanAfterExit]);
        evaluation.Findings.ShouldAllBe(finding => !finding.IsBlocking);
        evaluation.Findings[0].Statement.ShouldContain("2 entry(ies) on closed periods and 1 on approved days");
        evaluation.Recommendation.ShouldContain("2 point(s) need a decision");
        evaluation.Recommendation.ShouldContain(evaluation.Findings[0].Statement);
    }

    [Test]
    public void ApprovedDaysOnly_AreStillASealedFinding()
    {
        var plan = EmptyPlan with { ApprovedEntriesAfterExit = 3 };

        Codes(Evaluate(Facts(plan: plan))).ShouldBe([EmployeeExitFindingCode.SealedEntriesAfterExit]);
    }

    [Test]
    public void ContractRunningPastTheExit_IsReported()
    {
        var evaluation = Evaluate(Facts(contracts: new EmployeeExitContractFacts(2, true, null)));

        Codes(evaluation).ShouldBe([EmployeeExitFindingCode.ContractRunsPastExit]);
        evaluation.Findings[0].Statement.ShouldContain("2 contract assignment(s)");
    }

    [Test]
    public void ContractThatEndedBeforeTheExit_IsReportedWithItsEnd()
    {
        var evaluation = Evaluate(Facts(contracts: new EmployeeExitContractFacts(0, false, new DateOnly(2026, 2, 28))));

        Codes(evaluation).ShouldBe([EmployeeExitFindingCode.ContractEndedBeforeExit]);
        evaluation.Findings[0].Statement.ShouldContain("ended on 2026-02-28");
    }

    [Test]
    public void NoContractAtAll_RaisesNothing()
    {
        Evaluate(Facts(contracts: new EmployeeExitContractFacts(0, false, null))).Findings.ShouldBeEmpty();
    }

    [Test]
    public void ContractEndingExactlyOnTheExit_RaisesNothing()
    {
        Evaluate(Facts(contracts: new EmployeeExitContractFacts(0, true, null))).Findings.ShouldBeEmpty();
    }

    [Test]
    public void GroupMemberships_AreInformationOnly_AndDoNotNeedADecision()
    {
        var evaluation = Evaluate(Facts(groups: 3));

        Codes(evaluation).ShouldBe([EmployeeExitFindingCode.GroupMembershipsRemain]);
        evaluation.Findings[0].IsBlocking.ShouldBeFalse();
        evaluation.Recommendation.ShouldContain("leaves nothing booked");
    }

    [Test]
    public void BlockingFinding_WinsOverEverythingElse_AndComesFirst()
    {
        var plan = EmptyPlan with { WorksAfterExit = 1, WorksAlreadyPast = 0, FirstWorkDate = Exit.AddDays(1), LastWorkDate = Exit.AddDays(1) };

        var evaluation = Evaluate(Facts(
            membership: new EmployeeExitMembershipFacts(0, null, null, string.Empty), plan: plan, groups: 1));

        evaluation.Findings[0].Code.ShouldBe(EmployeeExitFindingCode.NoActiveMembership);
        evaluation.Recommendation.ShouldStartWith("Do not record the exit");
        evaluation.Recommendation.ShouldNotContain("stops planning");
    }

    [Test]
    public void Recommendation_NeverClaimsAnythingWasChanged_AndStatesBookedEntriesStay()
    {
        var plan = EmptyPlan with { WorksAfterExit = 2, WorksAlreadyPast = 0, FirstWorkDate = Exit.AddDays(1), LastWorkDate = Exit.AddDays(2) };

        var evaluation = Evaluate(Facts(plan: plan));

        evaluation.Recommendation.ShouldContain("Nothing has been changed");
        evaluation.Recommendation.ShouldContain("are not removed or reassigned by the exit");
        evaluation.Recommendation.ShouldContain("only new bookings after that day are refused");
    }

    [Test]
    public void SameFacts_GiveTheSameEvaluation()
    {
        var plan = EmptyPlan with { WorksAfterExit = 2, WorksAlreadyPast = 1, FirstWorkDate = Exit.AddDays(1), LastWorkDate = Exit.AddDays(2), AbsenceDaysAfterExit = 1 };

        var first = Evaluate(Facts(plan: plan, groups: 2));
        var second = Evaluate(Facts(plan: plan, groups: 2));

        second.Recommendation.ShouldBe(first.Recommendation);
        Codes(second).ShouldBe(Codes(first));
    }
}
