// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// diagnose_planning_period without an employee: a group (by name or id) gives the summary the schedule shows for it - how many
/// employees are dark (outside the employment), how many blue (outside the group membership), when the late memberships start
/// (most common and earliest), and the sealed days with their employee counts. The employees are the ones the schedule lists for
/// the group, the memberships looked at are those of the group's subtree, and everything is read in a few batched queries however
/// many employees there are. A group outside the caller's scope is answered like an unknown one, an empty group says so, and the
/// result states the facts and tells the model not to add causes it was not given.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class DiagnosePlanningPeriodGroupSummaryTests
{
    private static readonly Guid BernId = Guid.NewGuid();
    private static readonly Guid SubId = Guid.NewGuid();
    private static readonly Guid ForeignId = Guid.NewGuid();
    private static readonly DateTime Today = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OldEntry = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private IGroupRepository _groups = null!;
    private IGroupPlanningAgentRepository _agents = null!;
    private IGroupItemRepository _groupItems = null!;
    private ISealedDayRepository _seals = null!;
    private Group _bern = null!;
    private Group _bernSub = null!;
    private Group _foreign = null!;
    private Guid[] _clientIds = null!;

    [SetUp]
    public void SetUp()
    {
        _bern = new Group { Id = BernId, Name = "Bern", Root = BernId, Lft = 1, Rgt = 4 };
        _bernSub = new Group { Id = SubId, Name = "Bern Nord", Root = BernId, Lft = 2, Rgt = 3 };
        _foreign = new Group { Id = ForeignId, Name = "Geheim", Root = ForeignId, Lft = 1, Rgt = 2 };
        _groups = Substitute.For<IGroupRepository>();
        _groups.List().Returns(new List<Group> { _bern, _bernSub, _foreign });

        _clientIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        _agents = Substitute.For<IGroupPlanningAgentRepository>();
        _agents.GetAgentEmploymentsAsync(BernId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(_clientIds.Select(id => new PlanningAgentEmployment(id, new MembershipWindow(DateOnly.FromDateTime(OldEntry), null))).ToList());

        _groupItems = Substitute.For<IGroupItemRepository>();
        _groupItems.GetValidityRangesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, (DateTime? ValidFrom, DateTime? ValidUntil)>
            {
                [_clientIds[0]] = (Utc(2026, 10, 20), null),
                [_clientIds[1]] = (Utc(2026, 10, 20), null),
                [_clientIds[2]] = (Utc(2026, 10, 8), null),
                [_clientIds[3]] = (Utc(2024, 3, 1), null),
            });

        _seals = Substitute.For<ISealedDayRepository>();
        _seals.GetLockedClientDaysAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<(Guid ClientId, DateOnly Date)>());
    }

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private DiagnosePlanningPeriodSkill Skill(GroupScopeAccess? scope = null)
    {
        var clock = Substitute.For<ICompanyClock>();
        clock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(Today);
        return new DiagnosePlanningPeriodSkill(
            Substitute.For<IClientSearchRepository>(), Substitute.For<IClientRepository>(), SkillClientVisibility.AllVisible(),
            _groups, TestGroupScopeGuard.For(scope ?? GroupScopeAccess.Unrestricted()), _seals, clock, _agents, _groupItems);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewSchedule" }
    };

    private static Dictionary<string, object> Params(string? group = "Bern", string date = "2026-10-01", string? until = "2026-10-19")
    {
        var parameters = new Dictionary<string, object> { ["date"] = date };
        if (until != null)
        {
            parameters["untilDate"] = until;
        }

        if (group != null)
        {
            parameters["groupName"] = group;
        }

        return parameters;
    }

    private static JsonElement Json(SkillResult result) =>
        JsonSerializer.SerializeToElement(result.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Test]
    public async Task WithoutAnEmployeeAndWithoutAGroup_BothAreAskedFor()
    {
        var result = await Skill().ExecuteAsync(Ctx(), Params(group: null));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("employee");
        result.Message.ShouldContain("group");
        result.Message.ShouldContain("selectedGroupId");
    }

    [Test]
    public async Task TheSummaryCountsDarkAndBlueEmployees_AndNamesTheMostCommonAndTheEarliestStart()
    {
        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        var data = Json(result);
        data.GetProperty("group").GetString().ShouldBe("Bern");
        data.GetProperty("employeesShown").GetInt32().ShouldBe(4);
        data.GetProperty("outsideEmployment").GetInt32().ShouldBe(0);
        data.GetProperty("outsideGroupPeriod").GetInt32().ShouldBe(3);
        var late = data.GetProperty("lateGroupStart");
        late.GetProperty("employees").GetInt32().ShouldBe(3);
        late.GetProperty("mostCommon").GetString().ShouldBe("2026-10-20");
        late.GetProperty("mostCommonEmployees").GetInt32().ShouldBe(2);
        late.GetProperty("earliest").GetString().ShouldBe("2026-10-08");
        result.Message.ShouldContain("Bern, 2026-10-01 to 2026-10-19");
        result.Message.ShouldContain("3 of 4 employee(s)");
        result.Message.ShouldContain("blue and locked");
        result.Message.ShouldContain("most common start is 2026-10-20 (2 employee(s))");
        result.Message.ShouldContain("earliest is 2026-10-08");
        result.Message.ShouldContain("Adjust the validity dates");
    }

    [Test]
    public async Task DarkEmployees_AreCountedFromTheEmploymentWindow()
    {
        _agents.GetAgentEmploymentsAsync(BernId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<PlanningAgentEmployment>
            {
                new(_clientIds[0], new MembershipWindow(new DateOnly(2026, 10, 12), null)),
                new(_clientIds[3], new MembershipWindow(new DateOnly(2020, 1, 1), null)),
            });

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        Json(result).GetProperty("outsideEmployment").GetInt32().ShouldBe(1);
        result.Message.ShouldContain("1 of 2 employee(s)");
        result.Message.ShouldContain("dark");
        result.Message.ShouldContain("Check or correct the entry and exit date");
    }

    [Test]
    public async Task SealedDays_AreListedWithTheirEmployeeCounts()
    {
        _seals.GetLockedClientDaysAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<(Guid ClientId, DateOnly Date)>
            {
                (_clientIds[0], new DateOnly(2026, 10, 5)),
                (_clientIds[1], new DateOnly(2026, 10, 5)),
            });

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        var sealedDays = Json(result).GetProperty("sealedDays");
        sealedDays.GetArrayLength().ShouldBe(1);
        sealedDays[0].GetProperty("date").GetString().ShouldBe("2026-10-05");
        sealedDays[0].GetProperty("employees").GetInt32().ShouldBe(2);
        result.Message.ShouldContain("2026-10-05 (2 employee(s))");
    }

    [Test]
    public async Task TheGroupCanBeGivenById_AndOnlyTheMembershipsOfExactlyThatGroupAreRead()
    {
        var parameters = Params(group: null);
        parameters["groupId"] = BernId.ToString();

        var result = await Skill().ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue();
        await _groupItems.Received(1).GetValidityRangesAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 4),
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(BernId)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnEmployeeWhoOnlyBelongsToASubgroup_IsListedButNeverBlue_LikeInTheGrid()
    {
        _groupItems.GetValidityRangesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, (DateTime? ValidFrom, DateTime? ValidUntil)>
            {
                [_clientIds[0]] = (Utc(2026, 10, 20), null),
            });

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        var data = Json(result);
        data.GetProperty("employeesShown").GetInt32().ShouldBe(4);
        data.GetProperty("outsideGroupPeriod").GetInt32().ShouldBe(1);
        data.GetProperty("lateGroupStart").GetProperty("employees").GetInt32().ShouldBe(1);
    }

    [Test]
    public async Task EverythingIsReadInBatches_WhateverTheNumberOfEmployees()
    {
        var many = Enumerable.Range(0, 3000).Select(_ => Guid.NewGuid()).ToList();
        _agents.GetAgentEmploymentsAsync(BernId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(many.Select(id => new PlanningAgentEmployment(id, new MembershipWindow(DateOnly.FromDateTime(OldEntry), null))).ToList());

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        Json(result).GetProperty("employeesShown").GetInt32().ShouldBe(3000);
        await _agents.Received(1).GetAgentEmploymentsAsync(BernId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await _groupItems.Received(1).GetValidityRangesAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
        await _seals.Received(1).GetLockedClientDaysAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AGroupWithoutEmployeesOnThoseDays_SaysSo_AndReadsNoMemberships()
    {
        _agents.GetAgentEmploymentsAsync(BernId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<PlanningAgentEmployment>());

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("no employee");
        Json(result).GetProperty("employeesShown").GetInt32().ShouldBe(0);
        await _groupItems.DidNotReceive().GetValidityRangesAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AGroupOutsideTheCallersScope_IsAnsweredLikeAnUnknownOne_AndNeverRead()
    {
        var scope = GroupScopeAccess.Restricted([BernId], ["Bern"]);

        var hidden = await Skill(scope).ExecuteAsync(Ctx(), Params(group: "Geheim"));
        var unknown = await Skill(scope).ExecuteAsync(Ctx(), Params(group: "Nirgendwo"));

        hidden.Success.ShouldBeFalse();
        unknown.Success.ShouldBeFalse();
        hidden.Message.ShouldNotContain("Geheim, ");
        await _agents.DidNotReceive().GetAgentEmploymentsAsync(
            ForeignId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AGroupIdOutsideTheCallersScope_IsRefused()
    {
        var parameters = Params(group: null);
        parameters["groupId"] = ForeignId.ToString();

        var result = await Skill(GroupScopeAccess.Restricted([BernId], ["Bern"])).ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldNotContain("Geheim");
    }

    [Test]
    public async Task TheMessageStatesTheFacts_AndTellsTheModelNotToAddOtherCauses()
    {
        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Message.ShouldContain("user's language");
        result.Message.ShouldContain("do not add other causes");
    }

    [Test]
    public async Task TheRangeRulesApplyToTheGroupSummaryToo()
    {
        (await Skill().ExecuteAsync(Ctx(), Params(date: "2026-10-05", until: "2026-10-01"))).Success.ShouldBeFalse();
        (await Skill().ExecuteAsync(Ctx(), Params(date: "2026-10-01", until: "2026-11-01"))).Success.ShouldBeFalse();
    }
}
