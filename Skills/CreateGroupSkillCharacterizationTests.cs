// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for CreateGroupSkill.ExecuteAsync, written before its long body was split into parent,
/// calendar and payment-interval steps: every refusal text and which check wins when several inputs are bad, the
/// success text for root and child groups with and without the first-group visibility advisory, the serialized result
/// data, and that the visibility count is taken before the group is posted.
/// </summary>

using System.Net;
using System.Text.Json;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;

using Klacks.Api.Application.DTOs.Associations;

using Klacks.Api.Application.Mappers;

using Klacks.UnitTest.Infrastructure.SelfApi;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CreateGroupSkillCharacterizationTests
{
    private const string GroupsRoute = "api/backend/Groups";

    private IGroupRepository _groupRepository = null!;
    private ICalendarSelectionRepository _calendarSelectionRepository = null!;
    private IGroupVisibilityPreservationService _visibilityPreservation = null!;
    private FakeSelfApi _api = null!;
    private Guid _persistedId;
    private int _usersKeepingFullVisibility;
    private int? _apiCallsWhenCounted;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _calendarSelectionRepository = Substitute.For<ICalendarSelectionRepository>();
        _visibilityPreservation = Substitute.For<IGroupVisibilityPreservationService>();
        _api = new FakeSelfApi();
        _persistedId = Guid.NewGuid();
        _api.Respond(HttpMethod.Post, GroupsRoute, new GroupResource { Id = _persistedId });
        _usersKeepingFullVisibility = 0;
        _apiCallsWhenCounted = null;
        _visibilityPreservation.CountUsersKeepingFullVisibilityAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _apiCallsWhenCounted = _api.Calls.Count;
                return _usersKeepingFullVisibility;
            });
    }

    [TearDown]
    public void TearDown() => _api.Dispose();

    private CreateGroupSkill Skill(IGroupScopeGuard guard) =>
        new(_groupRepository, guard, _calendarSelectionRepository, new GroupMapper(), _api.Client,
            new FixedCompanyClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)), _visibilityPreservation);

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanCreateGroups", "CanEditSettings", "CanViewSettings" },
        AccessToken = new BearerToken("caller-jwt")
    };

    private static Dictionary<string, object> Params(params (string Key, object Value)[] extra)
    {
        var parameters = new Dictionary<string, object> { ["name"] = "Bern" };
        foreach (var (key, value) in extra)
        {
            parameters[key] = value;
        }

        return parameters;
    }

    private static JsonElement Data(SkillResult result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result.Data)).RootElement;

    [Test]
    public async Task InvalidParentId_ReturnsExactText()
    {
        var result = await Skill(TestGroupScopeGuard.Unrestricted())
            .ExecuteAsync(Ctx(), Params(("parentId", "nope"), ("calendarId", "also-bad")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Invalid parentId UUID: nope");
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task UnknownParent_ReturnsExactText()
    {
        var parentId = Guid.NewGuid();
        _groupRepository.Get(parentId).Returns((Group?)null);

        var result = await Skill(TestGroupScopeGuard.Unrestricted())
            .ExecuteAsync(Ctx(), Params(("parentId", parentId.ToString())));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Parent group '{parentId}' not found.");
    }

    [Test]
    public async Task ParentOutsideScope_ReturnsTheScopeText()
    {
        var parentId = Guid.NewGuid();
        var access = GroupScopeAccess.Restricted(new[] { Guid.NewGuid() }, new[] { "Verkauf" });
        _groupRepository.Get(parentId).Returns(new Group { Id = parentId, Name = "Logistik", Root = Guid.NewGuid() });

        var result = await Skill(TestGroupScopeGuard.For(access))
            .ExecuteAsync(Ctx(), Params(("parentId", parentId.ToString())));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(access.BuildOutOfScopeError("Logistik"));
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task RootCreation_ForScopedUser_ReturnsTheRootCreationText_BeforeTheCalendarCheck()
    {
        var access = GroupScopeAccess.Restricted(new[] { Guid.NewGuid() }, new[] { "Verkauf" });

        var result = await Skill(TestGroupScopeGuard.For(access))
            .ExecuteAsync(Ctx(), Params(("calendarId", "also-bad")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(access.BuildRootCreationError());
    }

    [Test]
    public async Task InvalidCalendarId_ReturnsExactText_BeforeThePaymentIntervalCheck()
    {
        var result = await Skill(TestGroupScopeGuard.Unrestricted())
            .ExecuteAsync(Ctx(), Params(("calendarId", "bad"), ("paymentInterval", "Fortnightly")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Invalid calendarId UUID: bad");
        await _calendarSelectionRepository.DidNotReceive().Exists(Arg.Any<Guid>());
    }

    [Test]
    public async Task UnknownCalendar_ReturnsExactText()
    {
        var calendarId = Guid.NewGuid();
        _calendarSelectionRepository.Exists(calendarId).Returns(false);

        var result = await Skill(TestGroupScopeGuard.Unrestricted())
            .ExecuteAsync(Ctx(), Params(("calendarId", calendarId.ToString())));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Calendar selection '{calendarId}' not found. Use list_calendars to see available IDs.");
    }

    [Test]
    public async Task InvalidPaymentInterval_ReturnsExactText_BeforeTheDateChecks()
    {
        var result = await Skill(TestGroupScopeGuard.Unrestricted())
            .ExecuteAsync(Ctx(), Params(("paymentInterval", "Fortnightly"), ("validFrom", "irgendwann")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Invalid paymentInterval 'Fortnightly'. Allowed: Weekly, Biweekly, Monthly, Individual.");
    }

    [Test]
    public async Task BothDatesInvalid_TheValidFromTextWins()
    {
        var result = await Skill(TestGroupScopeGuard.Unrestricted())
            .ExecuteAsync(Ctx(), Params(("validFrom", "irgendwann"), ("validUntil", "nie")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(SkillDateParser.InvalidDateMessageFor("validFrom", "irgendwann"));
    }

    [Test]
    public async Task InvalidValidUntil_ReturnsItsText()
    {
        var result = await Skill(TestGroupScopeGuard.Unrestricted())
            .ExecuteAsync(Ctx(), Params(("validUntil", "nie")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(SkillDateParser.InvalidDateMessageFor("validUntil", "nie"));
        _apiCallsWhenCounted.ShouldBeNull();
    }

    [Test]
    public async Task RootGroup_SuccessTextAndData_DefaultMonthlyAndToday()
    {
        var result = await Skill(TestGroupScopeGuard.Unrestricted()).ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe("Group 'Bern' was created at root level.");
        var data = Data(result);
        data.EnumerateObject().Select(p => p.Name).ShouldBe(new[]
        {
            "GroupId", "Name", "ParentId", "ValidFrom", "ValidUntil", "PaymentInterval", "CalendarSelectionId",
            "UsersKeepingFullVisibilityCount"
        });
        data.GetProperty("GroupId").GetGuid().ShouldBe(_persistedId);
        data.GetProperty("PaymentInterval").GetString().ShouldBe("Monthly");
        data.GetProperty("UsersKeepingFullVisibilityCount").GetInt32().ShouldBe(0);
        var sent = _api.BodyOf<GroupResource>()!;
        sent.ValidFrom.ShouldBe(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        sent.PaymentInterval.ShouldBe(PaymentInterval.Monthly);
        _apiCallsWhenCounted.ShouldBe(0);
    }

    [Test]
    public async Task ChildGroup_WithAdvisory_SuccessText()
    {
        _usersKeepingFullVisibility = 3;
        var parentId = Guid.NewGuid();
        var calendarId = Guid.NewGuid();
        _groupRepository.Get(parentId).Returns(new Group { Id = parentId, Name = "CH" });
        _calendarSelectionRepository.Exists(calendarId).Returns(true);

        var result = await Skill(TestGroupScopeGuard.Unrestricted()).ExecuteAsync(Ctx(), Params(
            ("parentId", parentId.ToString()),
            ("calendarId", calendarId.ToString()),
            ("paymentInterval", "weekly"),
            ("validFrom", "2026-02-01"),
            ("validUntil", "2026-12-31")));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            $"Group 'Bern' was created under parent {parentId}. " + FirstGroupVisibilityAdvisory.For(3));
        var data = Data(result);
        data.GetProperty("ParentId").GetGuid().ShouldBe(parentId);
        data.GetProperty("CalendarSelectionId").GetGuid().ShouldBe(calendarId);
        data.GetProperty("PaymentInterval").GetString().ShouldBe("Weekly");
        data.GetProperty("UsersKeepingFullVisibilityCount").GetInt32().ShouldBe(3);
        var sent = _api.BodyOf<GroupResource>()!;
        sent.Parent.ShouldBe(parentId);
        sent.ValidUntil.ShouldBe(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task ApiFailure_IsPassedThroughVerbatim()
    {
        _api.RespondWithProblem(HttpMethod.Post, GroupsRoute, HttpStatusCode.Conflict, "boom");

        var result = await Skill(TestGroupScopeGuard.Unrestricted()).ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("boom");
    }

    [Test]
    public async Task ApiWithoutBody_ReturnsExactText()
    {
        _api.Respond(HttpMethod.Post, GroupsRoute, body: null);

        var result = await Skill(TestGroupScopeGuard.Unrestricted()).ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Creating the group 'Bern' returned no result — the operation may have failed.");
    }
}
