// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Parity between evaluate_employee_exit and end_client_membership over the same memberships and exit date: whenever the
/// advice says the exit is blocked (no active membership, several, exit before the start) the recording skill refuses it,
/// and whenever the advice does not block, the recording skill succeeds. Both read the memberships through
/// MembershipExitRules, so they cannot drift apart; this test compares their results.
/// </summary>

using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Clients;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.Infrastructure.SelfApi;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class EmployeeExitActParityTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateTime Today = new(2026, 3, 10);

    private IMembershipRepository _memberships = null!;
    private FakeSelfApi _api = null!;
    private EndClientMembershipSkill _act = null!;
    private EvaluateEmployeeExitSkill _advice = null!;

    [SetUp]
    public void SetUp()
    {
        _memberships = Substitute.For<IMembershipRepository>();
        _api = new FakeSelfApi();
        _api.Respond(HttpMethod.Put, "api/backend/Memberships", new MembershipResource());

        var clients = Substitute.For<IClientRepository>();
        clients.Exists(ClientId).Returns(true);
        clients.Get(ClientId).Returns(new Client { Id = ClientId, FirstName = "Anna", Name = "Muster" });
        var visibility = Substitute.For<IClientVisibilityGuard>();
        visibility.IsVisibleAsync(ClientId, Arg.Any<CancellationToken>()).Returns(true);
        var clock = Substitute.For<ICompanyClock>();
        clock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(Today);

        var works = Substitute.For<IWorkRepository>();
        works.GetByClientAndDateRangeAsync(ClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<Work>());
        var breaks = Substitute.For<IBreakRepository>();
        breaks.GetByClientAndDateRangeAsync(ClientId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<Break>());
        var absences = Substitute.For<IAbsenceRepository>();
        absences.GetOnCallAbsenceIdsAsync(Arg.Any<CancellationToken>()).Returns(new HashSet<Guid>());
        var contracts = Substitute.For<IClientContractReadRepository>();
        contracts.GetContractsForClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(new List<ClientContract>());
        var groupItems = Substitute.For<IGroupItemRepository>();

        _act = new EndClientMembershipSkill(
            _memberships, clients, visibility, _api.Client, new SelfApiRouteResolver(), clock);
        _advice = new EvaluateEmployeeExitSkill(
            Substitute.For<IClientSearchRepository>(), clients, visibility, _memberships, works, breaks, absences,
            contracts, groupItems, clock);
    }

    [TearDown]
    public void TearDown() => _api.Dispose();

    private static Membership Membership(DateTime from, DateTime? until = null) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = ClientId,
        ValidFrom = from,
        ValidUntil = until
    };

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "tester",
        UserPermissions = [],
        AccessToken = new BearerToken("caller-jwt")
    };

    private static Dictionary<string, object> Parameters(string exitDate) => new()
    {
        ["clientId"] = ClientId.ToString(),
        ["exitDate"] = exitDate
    };

    private static IEnumerable<TestCaseData> Scenarios()
    {
        var start = new DateTime(2024, 1, 1);
        yield return new TestCaseData(new List<Membership>(), "2026-03-31").SetName("NoMembershipAtAll");
        yield return new TestCaseData(new List<Membership> { Membership(start, new DateTime(2026, 1, 31)) }, "2026-03-31")
            .SetName("OnlyAnEndedMembership");
        yield return new TestCaseData(new List<Membership> { Membership(start) }, "2026-03-31").SetName("OneOpenMembership");
        yield return new TestCaseData(new List<Membership> { Membership(start, new DateTime(2026, 12, 31)) }, "2026-03-31")
            .SetName("OneMembershipEndingLater");
        yield return new TestCaseData(new List<Membership> { Membership(start, new DateTime(2026, 3, 10)) }, "2026-03-31")
            .SetName("OneMembershipEndingToday");
        yield return new TestCaseData(new List<Membership> { Membership(start), Membership(new DateTime(2025, 1, 1)) }, "2026-03-31")
            .SetName("TwoActiveMemberships");
        yield return new TestCaseData(
                new List<Membership> { Membership(new DateTime(2023, 1, 1), new DateTime(2023, 12, 31)), Membership(start) }, "2026-03-31")
            .SetName("OneActiveOneHistorical");
        yield return new TestCaseData(new List<Membership> { Membership(start) }, "2023-12-31").SetName("ExitBeforeTheStart");
        yield return new TestCaseData(new List<Membership> { Membership(start) }, "2024-01-01").SetName("ExitOnTheStartDay");
    }

    [TestCaseSource(nameof(Scenarios))]
    public async Task AdviceBlocksExactlyWhenTheRecordingIsRefused(List<Membership> memberships, string exitDate)
    {
        _memberships.ListByClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(memberships);

        var advice = await _advice.ExecuteAsync(Context(), Parameters(exitDate));
        var act = await _act.ExecuteAsync(Context(), Parameters(exitDate));

        advice.Success.ShouldBeTrue(advice.Message);
        var evaluation = advice.Data.ShouldBeOfType<EmployeeExitEvaluation>();
        evaluation.Findings.Any(finding => finding.IsBlocking).ShouldBe(!act.Success, act.Message);
    }
}
