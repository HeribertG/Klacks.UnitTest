// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for evaluate_employee_exit: the skill resolves the employee through the visibility guard, reads the
/// memberships with the recording skill's rule, counts only main-plan entries after the exit date (no scenario rows, no
/// container children), splits on-call from other absences, counts approved and closed entries, reads contracts and groups,
/// relays the advisor's evaluation and changes nothing. A hidden employee is answered like an unknown one and nothing else
/// is read.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Clients;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class EvaluateEmployeeExitSkillTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid OnCallAbsenceId = Guid.NewGuid();
    private static readonly Guid HolidayAbsenceId = Guid.NewGuid();
    private static readonly DateTime Today = new(2026, 3, 10);
    private static readonly DateTime MembershipStart = new(2024, 1, 1);
    private static readonly DateOnly Exit = new(2026, 3, 31);

    private IClientSearchRepository _search = null!;
    private IClientRepository _clients = null!;
    private IClientVisibilityGuard _visibility = null!;
    private IMembershipRepository _memberships = null!;
    private IWorkRepository _works = null!;
    private IBreakRepository _breaks = null!;
    private IAbsenceRepository _absences = null!;
    private IClientContractReadRepository _contracts = null!;
    private IGroupItemRepository _groupItems = null!;
    private ICompanyClock _clock = null!;
    private EvaluateEmployeeExitSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _search = Substitute.For<IClientSearchRepository>();
        _clients = Substitute.For<IClientRepository>();
        _visibility = Substitute.For<IClientVisibilityGuard>();
        _memberships = Substitute.For<IMembershipRepository>();
        _works = Substitute.For<IWorkRepository>();
        _breaks = Substitute.For<IBreakRepository>();
        _absences = Substitute.For<IAbsenceRepository>();
        _contracts = Substitute.For<IClientContractReadRepository>();
        _groupItems = Substitute.For<IGroupItemRepository>();
        _clock = Substitute.For<ICompanyClock>();

        _clients.Get(ClientId).Returns(new Client { Id = ClientId, FirstName = "Anna", Name = "Muster" });
        _visibility.IsVisibleAsync(ClientId, Arg.Any<CancellationToken>()).Returns(true);
        _clock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(Today);
        _memberships.ListByClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(new List<Membership> { Membership() });
        _works.GetByClientAndDateRangeAsync(ClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<Work>());
        _breaks.GetByClientAndDateRangeAsync(ClientId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<Break>());
        _absences.GetOnCallAbsenceIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { OnCallAbsenceId });
        _contracts.GetContractsForClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(new List<ClientContract>());
        _groupItems.GetVisibilityMembershipAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(new ClientVisibilityMembership(false, []));

        _skill = new EvaluateEmployeeExitSkill(
            _search, _clients, _visibility, _memberships, _works, _breaks, _absences, _contracts, _groupItems, _clock);
    }

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "tester",
        UserPermissions = []
    };

    private static Dictionary<string, object> Parameters(string exitDate = "2026-03-31") => new()
    {
        ["clientId"] = ClientId.ToString(),
        ["exitDate"] = exitDate
    };

    private static Membership Membership(DateTime? validUntil = null, DateTime? validFrom = null) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = ClientId,
        ValidFrom = validFrom ?? MembershipStart,
        ValidUntil = validUntil
    };

    private static Work WorkOn(DateOnly day, WorkLockLevel level = WorkLockLevel.None, Guid? token = null, Guid? parent = null) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = ClientId,
        CurrentDate = day,
        LockLevel = level,
        AnalyseToken = token,
        ParentWorkId = parent
    };

    private static Break BreakOn(DateOnly day, Guid absenceId, WorkLockLevel level = WorkLockLevel.None, Guid? parent = null) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = ClientId,
        CurrentDate = day,
        AbsenceId = absenceId,
        LockLevel = level,
        ParentWorkId = parent
    };

    private async Task<EmployeeExitEvaluation> Evaluate(string exitDate = "2026-03-31")
    {
        var result = await _skill.ExecuteAsync(Context(), Parameters(exitDate));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(result.Data.ShouldBeOfType<EmployeeExitEvaluation>().Recommendation);
        return (EmployeeExitEvaluation)result.Data!;
    }

    [Test]
    public async Task LoneMembership_NothingBooked_RelaysTheEvaluationWithoutFindings()
    {
        var evaluation = await Evaluate();

        evaluation.ClientId.ShouldBe(ClientId);
        evaluation.Facts.EmployeeName.ShouldBe("Anna Muster");
        evaluation.Facts.ExitDate.ShouldBe(Exit);
        evaluation.Facts.HorizonEnd.ShouldBe(Exit.AddDays(EmployeeExitLimits.HorizonDays));
        evaluation.Facts.Membership.ActiveCount.ShouldBe(1);
        evaluation.Findings.ShouldBeEmpty();
    }

    [Test]
    public async Task Entries_AreSearchedFromTheDayAfterTheExitUpToTheHorizon_AsUtcDays()
    {
        await Evaluate();

        var firstDay = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var lastDay = new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc).AddDays(EmployeeExitLimits.HorizonDays);
        await _works.Received(1).GetByClientAndDateRangeAsync(
            ClientId,
            Arg.Is<DateTime>(day => day == firstDay && day.Kind == DateTimeKind.Utc),
            Arg.Is<DateTime>(day => day == lastDay && day.Kind == DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
        await _breaks.Received(1).GetByClientAndDateRangeAsync(
            ClientId, Exit.AddDays(1), Exit.AddDays(EmployeeExitLimits.HorizonDays), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Works_CountOnlyTheMainPlanParents_NotScenarioRowsOrContainerChildren()
    {
        _works.GetByClientAndDateRangeAsync(ClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<Work>
            {
                WorkOn(new DateOnly(2026, 4, 2)),
                WorkOn(new DateOnly(2026, 4, 3)),
                WorkOn(new DateOnly(2026, 4, 4), token: Guid.NewGuid()),
                WorkOn(new DateOnly(2026, 4, 5), parent: Guid.NewGuid())
            });

        var plan = (await Evaluate()).Facts.Plan;

        plan.WorksAfterExit.ShouldBe(2);
        plan.FirstWorkDate.ShouldBe(new DateOnly(2026, 4, 2));
        plan.LastWorkDate.ShouldBe(new DateOnly(2026, 4, 3));
    }

    [Test]
    public async Task Works_OnDaysBeforeTodayAreCountedAsAlreadyPast()
    {
        _clock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(new DateTime(2026, 4, 3));
        _works.GetByClientAndDateRangeAsync(ClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<Work> { WorkOn(new DateOnly(2026, 4, 2)), WorkOn(new DateOnly(2026, 4, 3)), WorkOn(new DateOnly(2026, 4, 9)) });
        _memberships.ListByClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(new List<Membership> { Membership() });

        var plan = (await Evaluate()).Facts.Plan;

        plan.WorksAfterExit.ShouldBe(3);
        plan.WorksAlreadyPast.ShouldBe(1);
    }

    [Test]
    public async Task Breaks_AreSplitIntoOnCallAndOtherAbsences_ByDistinctDays_WithoutChildren()
    {
        _breaks.GetByClientAndDateRangeAsync(ClientId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<Break>
            {
                BreakOn(new DateOnly(2026, 4, 6), HolidayAbsenceId),
                BreakOn(new DateOnly(2026, 4, 6), HolidayAbsenceId),
                BreakOn(new DateOnly(2026, 4, 7), HolidayAbsenceId),
                BreakOn(new DateOnly(2026, 4, 8), OnCallAbsenceId),
                BreakOn(new DateOnly(2026, 4, 9), HolidayAbsenceId, parent: Guid.NewGuid())
            });

        var plan = (await Evaluate()).Facts.Plan;

        plan.AbsenceDaysAfterExit.ShouldBe(2);
        plan.OnCallDaysAfterExit.ShouldBe(1);
    }

    [Test]
    public async Task ApprovedAndClosedEntries_AreCountedAcrossWorkAndAbsences_ConfirmedIsNotASeal()
    {
        _works.GetByClientAndDateRangeAsync(ClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<Work>
            {
                WorkOn(new DateOnly(2026, 4, 2), WorkLockLevel.Closed),
                WorkOn(new DateOnly(2026, 4, 3), WorkLockLevel.Approved),
                WorkOn(new DateOnly(2026, 4, 4), WorkLockLevel.Confirmed),
                WorkOn(new DateOnly(2026, 4, 5), WorkLockLevel.Closed, parent: Guid.NewGuid())
            });
        _breaks.GetByClientAndDateRangeAsync(ClientId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<Break> { BreakOn(new DateOnly(2026, 4, 6), HolidayAbsenceId, WorkLockLevel.Closed) });

        var evaluation = await Evaluate();

        evaluation.Facts.Plan.ClosedEntriesAfterExit.ShouldBe(2);
        evaluation.Facts.Plan.ApprovedEntriesAfterExit.ShouldBe(1);
        evaluation.Findings[0].Code.ShouldBe(EmployeeExitFindingCode.SealedEntriesAfterExit);
    }

    [Test]
    public async Task Contracts_OnlyActiveAssignmentsCount_AndTheOneCoveringTheExitDecides()
    {
        _contracts.GetContractsForClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(new List<ClientContract>
        {
            Contract(new DateOnly(2024, 1, 1), null),
            Contract(new DateOnly(2023, 1, 1), new DateOnly(2023, 12, 31), active: false),
            Contract(new DateOnly(2022, 1, 1), new DateOnly(2022, 12, 31)),
            Contract(new DateOnly(2024, 1, 1), new DateOnly(2026, 3, 31))
        });

        var contracts = (await Evaluate()).Facts.Contracts;

        contracts.CoveredOnExitDate.ShouldBeTrue();
        contracts.RunningPastExit.ShouldBe(1);
        contracts.LatestEndBeforeExit.ShouldBeNull();
    }

    [Test]
    public async Task Contracts_WithoutOneCoveringTheExit_ReportTheLatestEndBefore()
    {
        _contracts.GetContractsForClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(new List<ClientContract>
        {
            Contract(new DateOnly(2022, 1, 1), new DateOnly(2022, 12, 31)),
            Contract(new DateOnly(2023, 1, 1), new DateOnly(2026, 2, 28)),
            Contract(new DateOnly(2026, 6, 1), null)
        });

        var contracts = (await Evaluate()).Facts.Contracts;

        contracts.CoveredOnExitDate.ShouldBeFalse();
        contracts.LatestEndBeforeExit.ShouldBe(new DateOnly(2026, 2, 28));
    }

    [Test]
    public async Task GroupEntries_AreCounted()
    {
        _groupItems.GetVisibilityMembershipAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(new ClientVisibilityMembership(true, [Guid.NewGuid(), Guid.NewGuid()]));

        (await Evaluate()).Facts.GroupCount.ShouldBe(2);
    }

    [Test]
    public async Task MembershipEndedBeforeToday_IsNotActive_SoTheExitIsBlocked()
    {
        _memberships.ListByClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(new List<Membership> { Membership(validUntil: new DateTime(2026, 1, 31)) });

        var evaluation = await Evaluate();

        evaluation.Findings[0].Code.ShouldBe(EmployeeExitFindingCode.NoActiveMembership);
        evaluation.Findings[0].IsBlocking.ShouldBeTrue();
    }

    [Test]
    public async Task OtherClientsMemberships_AreIgnored()
    {
        var foreign = Membership();
        foreign.ClientId = Guid.NewGuid();
        _memberships.ListByClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(new List<Membership> { foreign });

        (await Evaluate()).Facts.Membership.ActiveCount.ShouldBe(0);
    }

    [Test]
    public async Task SeveralActiveMemberships_ListTheRealOptions()
    {
        var first = Membership();
        var second = Membership(validUntil: new DateTime(2026, 12, 31), validFrom: new DateTime(2025, 1, 1));
        _memberships.ListByClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(new List<Membership> { first, second });

        var evaluation = await Evaluate();

        evaluation.Facts.Membership.Options.ShouldContain(first.Id.ToString());
        evaluation.Facts.Membership.Options.ShouldContain(second.Id.ToString());
        evaluation.Findings[0].Code.ShouldBe(EmployeeExitFindingCode.MultipleActiveMemberships);
    }

    [TestCase("not a date")]
    [TestCase("")]
    public async Task UnparseableExitDate_IsRefused_WithoutReadingAnything(string exitDate)
    {
        var result = await _skill.ExecuteAsync(Context(), Parameters(exitDate));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("exitDate");
        await _memberships.DidNotReceive().ListByClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HiddenEmployee_IsAnsweredLikeAnUnknownOne_AndNothingIsRead()
    {
        _visibility.IsVisibleAsync(ClientId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Client {ClientId} not found.");
        await _memberships.DidNotReceive().ListByClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _works.DidNotReceive().GetByClientAndDateRangeAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Skill_ChangesNothing()
    {
        _works.GetByClientAndDateRangeAsync(ClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<Work> { WorkOn(new DateOnly(2026, 4, 2)) });

        await Evaluate();

        await _memberships.DidNotReceive().Put(Arg.Any<Membership>());
        await _memberships.DidNotReceive().Add(Arg.Any<Membership>());
        await _works.DidNotReceive().Delete(Arg.Any<Guid>());
    }

    private static ClientContract Contract(DateOnly from, DateOnly? until, bool active = true) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = ClientId,
        ContractId = Guid.NewGuid(),
        FromDate = from,
        UntilDate = until,
        IsActive = active
    };
}
