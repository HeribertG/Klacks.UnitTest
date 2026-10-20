// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// diagnose_planning_period: the date is required and a range is limited to 31 days in the right order; a hidden employee gets
/// exactly the answer of an unknown one; a day before the employment start is reported as dark, a day outside a group
/// membership as blue and locked with the group's dates, sealed days by their kind; a named group limits the memberships to
/// its subtree and an employee without membership in it is told so; memberships in groups outside the caller's scope are only
/// counted and never named; a clear day says what was and was not checked; the data carries no free-text member names and
/// no enum names.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class DiagnosePlanningPeriodSkillTests
{
    private static readonly Guid BernId = Guid.NewGuid();
    private static readonly Guid SubId = Guid.NewGuid();
    private static readonly Guid ForeignId = Guid.NewGuid();
    private static readonly DateTime Today = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    private IClientSearchRepository _search = null!;
    private IClientRepository _clients = null!;
    private IGroupRepository _groups = null!;
    private ISealedDayRepository _seals = null!;
    private ICompanyClock _clock = null!;
    private Client _anna = null!;
    private Group _bern = null!;
    private Group _bernSub = null!;
    private Group _foreign = null!;

    [SetUp]
    public void SetUp()
    {
        _bern = new Group { Id = BernId, Name = "Bern", Root = BernId, Lft = 1, Rgt = 4 };
        _bernSub = new Group { Id = SubId, Name = "Bern Nord", Root = BernId, Lft = 2, Rgt = 3 };
        _foreign = new Group { Id = ForeignId, Name = "Geheim", Root = ForeignId, Lft = 1, Rgt = 2 };
        _anna = new Client
        {
            Id = Guid.NewGuid(),
            FirstName = "Anna",
            Name = "Muster",
            Membership = new Membership { ValidFrom = Utc(2024, 2, 1) },
            GroupItems = new List<GroupItem>
            {
                new() { GroupId = BernId, Group = _bern, ValidFrom = Utc(2026, 10, 20) }
            }
        };

        _search = Substitute.For<IClientSearchRepository>();
        _search.SearchAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                Items = [new ClientSearchItem { Id = _anna.Id, FirstName = "Anna", LastName = "Muster", IdNumber = 7 }],
                TotalCount = 1
            });
        _clients = Substitute.For<IClientRepository>();
        _clients.Get(_anna.Id).Returns(_anna);
        _groups = Substitute.For<IGroupRepository>();
        _groups.List().Returns(new List<Group> { _bern, _bernSub, _foreign });
        _seals = Substitute.For<ISealedDayRepository>();
        _seals.GetLockedPairsAsync(Arg.Any<IReadOnlyCollection<(DateOnly, Guid)>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<(DateOnly Date, Guid ClientId)>());
        _seals.GetLockedPairsAsync(Arg.Any<IReadOnlyCollection<(DateOnly, Guid)>>(), Arg.Any<WorkLockLevel>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<(DateOnly Date, Guid ClientId)>());
        _clock = Substitute.For<ICompanyClock>();
        _clock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(Today);
    }

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private DiagnosePlanningPeriodSkill Skill(IClientVisibilityGuard? guard = null, GroupScopeAccess? scope = null) => new(
        _search, _clients, guard ?? SkillClientVisibility.AllVisible(), _groups,
        TestGroupScopeGuard.For(scope ?? GroupScopeAccess.Unrestricted()), _seals, _clock,
        Substitute.For<IGroupPlanningAgentRepository>(), Substitute.For<IGroupItemRepository>());

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewSchedule" }
    };

    private static Dictionary<string, object> Params(string date = "2026-10-05", string? until = null, string? group = null)
    {
        var parameters = new Dictionary<string, object> { ["firstName"] = "Anna", ["lastName"] = "Muster", ["date"] = date };
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

    [Test]
    public async Task MissingDate_IsAskedFor()
    {
        var parameters = Params();
        parameters.Remove("date");

        var result = await Skill().ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("'date'");
    }

    [Test]
    public async Task MissingEmployee_IsAskedFor()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["date"] = "2026-10-05" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("employee");
    }

    [Test]
    public async Task RangeBeforeTheDate_IsRefused()
    {
        var result = await Skill().ExecuteAsync(Ctx(), Params("2026-10-05", "2026-10-01"));

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task RangeOver31Days_IsRefused_AndExactly31IsAccepted()
    {
        (await Skill().ExecuteAsync(Ctx(), Params("2026-10-01", "2026-11-01"))).Success.ShouldBeFalse();
        (await Skill().ExecuteAsync(Ctx(), Params("2026-10-01", "2026-10-31"))).Success.ShouldBeTrue();
    }

    [Test]
    public async Task HiddenEmployee_IsAnsweredLikeAnUnknownOne()
    {
        var hidden = await Skill(SkillClientVisibility.Hiding(_anna.Id)).ExecuteAsync(Ctx(), Params());
        _search.SearchAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult { Items = [], TotalCount = 0 });
        var unknown = await Skill().ExecuteAsync(Ctx(), Params());

        hidden.Success.ShouldBeFalse();
        unknown.Success.ShouldBeFalse();
        hidden.Message.ShouldBe(unknown.Message);
    }

    [Test]
    public async Task DayBeforeTheEmploymentStart_IsReportedAsDark()
    {
        _anna.Membership = new Membership { ValidFrom = Utc(2026, 10, 12) };
        _anna.GroupItems = new List<GroupItem>();

        var result = await Skill().ExecuteAsync(Ctx(), Params("2026-10-05"));

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("outside the employment of Anna Muster (2026-10-12 to open)");
        result.Message.ShouldContain("dark");
        result.Message.ShouldContain("Check or correct the entry and exit date");
        Json(result).GetProperty("findings")[0].GetProperty("scheduleShows").GetString().ShouldBe("dark");
    }

    [Test]
    public async Task ADayOutsideBothTheEmploymentAndTheGroup_IsShownBlue_BecauseTheBlueMarkingCoversTheDark()
    {
        _anna.Membership = new Membership { ValidFrom = Utc(2026, 10, 12) };

        var result = await Skill().ExecuteAsync(Ctx(), Params("2026-10-05"));

        Json(result).GetProperty("findings")[0].GetProperty("scheduleShows").GetString().ShouldBe("blue");
        result.Message.ShouldContain("outside the employment of Anna Muster (2026-10-12 to open); nothing can be planned on it");
        result.Message.ShouldContain("the schedule shows it blue");
        result.Message.ShouldNotContain("shows the day dark");
    }

    [Test]
    public async Task DayBeforeTheGroupMembership_IsReportedAsBlueAndLocked_WithTheGroupDates()
    {
        var result = await Skill().ExecuteAsync(Ctx(), Params("2026-10-05"));

        result.Message.ShouldContain("outside the period in which Anna Muster belongs to group 'Bern' (2026-10-20 to open)");
        result.Message.ShouldContain("blue and locked");
        var finding = Json(result).GetProperty("findings")[0];
        finding.GetProperty("scheduleShows").GetString().ShouldBe("blue");
        finding.GetProperty("outsideGroupPeriod").GetBoolean().ShouldBeTrue();
        finding.GetProperty("outsideSingleGroups")[0].GetString().ShouldBe("Bern");
        Json(result).GetProperty("employment").GetProperty("from").GetString().ShouldBe("2024-02-01");
    }

    [Test]
    public async Task ARange_MergesConsecutiveDaysIntoOneSpan()
    {
        var result = await Skill().ExecuteAsync(Ctx(), Params("2026-10-18", "2026-10-21"));

        result.Message.ShouldContain("Not plannable on 2 of 4 day(s)");
        result.Message.ShouldContain("2026-10-18 to 2026-10-19 is outside the period");
        Json(result).GetProperty("daysWithFindings").GetInt32().ShouldBe(2);
    }

    [Test]
    public async Task ADayInsideAllPeriods_SaysWhatWasAndWasNotChecked()
    {
        var result = await Skill().ExecuteAsync(Ctx(), Params("2026-10-22"));

        result.Message.ShouldContain("Nothing in the employment period, the group membership periods or the day seals stops planning");
        result.Message.ShouldContain("were not part of this check");
        Json(result).GetProperty("daysWithFindings").GetInt32().ShouldBe(0);
    }

    [Test]
    public async Task SealedDays_AreNamedByTheirKind()
    {
        var day = new DateOnly(2026, 10, 22);
        _seals.GetLockedPairsAsync(Arg.Any<IReadOnlyCollection<(DateOnly, Guid)>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<(DateOnly Date, Guid ClientId)> { (day, _anna.Id) });
        _seals.GetLockedPairsAsync(Arg.Any<IReadOnlyCollection<(DateOnly, Guid)>>(), WorkLockLevel.Closed, Arg.Any<CancellationToken>())
            .Returns(new HashSet<(DateOnly Date, Guid ClientId)> { (day, _anna.Id) });

        var result = await Skill().ExecuteAsync(Ctx(), Params("2026-10-22"));

        result.Message.ShouldContain("lies in a closed period for Anna Muster");
        Json(result).GetProperty("findings")[0].GetProperty("seal").GetString().ShouldBe("periodClosed");
    }

    [Test]
    public async Task ADayApproval_IsNotCalledAClosedPeriod()
    {
        var day = new DateOnly(2026, 10, 22);
        _seals.GetLockedPairsAsync(Arg.Any<IReadOnlyCollection<(DateOnly, Guid)>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<(DateOnly Date, Guid ClientId)> { (day, _anna.Id) });

        var result = await Skill().ExecuteAsync(Ctx(), Params("2026-10-22"));

        result.Message.ShouldContain("has been approved (day approval)");
        result.Message.ShouldNotContain("closed period");
    }

    [Test]
    public async Task NamedGroup_LimitsToItsSubtree_AndTellsAnOutsiderHesNoMember()
    {
        var member = await Skill().ExecuteAsync(Ctx(), Params("2026-10-05", group: "Bern"));
        member.Message.ShouldContain("belongs to group 'Bern'");

        _anna.GroupItems = new List<GroupItem>
        {
            new() { GroupId = SubId, Group = _bernSub, ValidFrom = Utc(2026, 10, 20) }
        };
        var viaSubgroup = await Skill().ExecuteAsync(Ctx(), Params("2026-10-05", group: "Bern"));
        viaSubgroup.Message.ShouldContain("group 'Bern Nord'");

        _anna.GroupItems = new List<GroupItem>();
        var outsider = await Skill().ExecuteAsync(Ctx(), Params("2026-10-05", group: "Bern"));
        outsider.Message.ShouldContain("has no membership in group 'Bern' at all");
        Json(outsider).GetProperty("notMemberOfRequestedGroup").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task UnknownGroupName_ListsTheRealGroups()
    {
        var result = await Skill().ExecuteAsync(Ctx(), Params("2026-10-05", group: "Nirgendwo"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Bern");
    }

    [Test]
    public async Task MembershipsOutsideTheCallersScope_AreCountedButNeverNamed()
    {
        _anna.GroupItems = new List<GroupItem>
        {
            new() { GroupId = BernId, Group = _bern, ValidFrom = Utc(2026, 10, 20) },
            new() { GroupId = ForeignId, Group = _foreign, ValidFrom = Utc(2026, 10, 20) }
        };
        var scope = GroupScopeAccess.Restricted([BernId], ["Bern"]);

        var result = await Skill(scope: scope).ExecuteAsync(Ctx(), Params("2026-10-05"));

        result.Message.ShouldNotContain("Geheim");
        JsonSerializer.Serialize(result.Data).ShouldNotContain("Geheim");
        result.Message.ShouldContain("1 further group membership(s) lie outside the caller's group scope");
        Json(result).GetProperty("hiddenGroupMembershipCount").GetInt32().ShouldBe(1);
    }

    [Test]
    public async Task DeletedAndScenarioAndShiftItems_AreIgnored()
    {
        _anna.GroupItems = new List<GroupItem>
        {
            new() { GroupId = BernId, Group = _bern, ValidFrom = Utc(2030, 1, 1), IsDeleted = true },
            new() { GroupId = BernId, Group = _bern, ValidFrom = Utc(2030, 1, 1), AnalyseToken = Guid.NewGuid() },
            new() { GroupId = BernId, Group = _bern, ValidFrom = Utc(2030, 1, 1), ShiftId = Guid.NewGuid() }
        };

        var result = await Skill().ExecuteAsync(Ctx(), Params("2026-10-05"));

        Json(result).GetProperty("groups").GetArrayLength().ShouldBe(0);
        Json(result).GetProperty("daysWithFindings").GetInt32().ShouldBe(0);
    }

    [Test]
    public async Task WithoutAMembershipRecord_TheMessageSaysSo_AndNothingIsBlocked()
    {
        _anna.Membership = null;

        var result = await Skill().ExecuteAsync(Ctx(), Params("2026-10-22"));

        result.Message.ShouldContain("has no employment record");
        Json(result).GetProperty("hasEmploymentRecord").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public async Task TheDataCarriesNoEnumNames_AndTheMessageRelaysInTheUsersLanguage()
    {
        var result = await Skill().ExecuteAsync(Ctx(), Params("2026-10-05"));

        var data = JsonSerializer.Serialize(result.Data);
        data.ShouldNotContain("DayApproval");
        data.ShouldNotContain("PeriodClosed");
        result.Message.ShouldContain("user's language");
        result.Message.ShouldContain("do not add other causes");
    }

    private static JsonElement Json(SkillResult result) =>
        JsonSerializer.SerializeToElement(result.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Test]
    public async Task ADayInTheGapBetweenTwoGroups_IsNotBlue_BecauseTheGridUsesTheCombinedRange()
    {
        _anna.GroupItems = new List<GroupItem>
        {
            new() { GroupId = BernId, Group = _bern, ValidFrom = Utc(2026, 1, 1), ValidUntil = Utc(2026, 6, 30) },
            new() { GroupId = SubId, Group = _bernSub, ValidFrom = Utc(2026, 9, 1) }
        };

        var result = await Skill().ExecuteAsync(Ctx(), Params("2026-07-15"));

        result.Message.ShouldContain("Nothing in the employment period");
        Json(result).GetProperty("daysWithFindings").GetInt32().ShouldBe(0);
        Json(result).GetProperty("groups").GetArrayLength().ShouldBe(2);
    }

    [Test]
    public async Task ADayBeforeAllGroups_NamesTheCombinedRangeAndTheSinglePeriods()
    {
        _anna.GroupItems = new List<GroupItem>
        {
            new() { GroupId = BernId, Group = _bern, ValidFrom = Utc(2026, 11, 1) },
            new() { GroupId = SubId, Group = _bernSub, ValidFrom = Utc(2026, 12, 1) }
        };

        var result = await Skill().ExecuteAsync(Ctx(), Params("2026-10-05"));

        result.Message.ShouldContain("combined membership period");
        result.Message.ShouldContain("Bern");
        result.Message.ShouldContain("Bern Nord");
        result.Message.ShouldContain("2026-11-01 to open");
        result.Message.ShouldContain("2026-12-01 to open");
        Json(result).GetProperty("findings")[0].GetProperty("scheduleShows").GetString().ShouldBe("blue");
    }
}
