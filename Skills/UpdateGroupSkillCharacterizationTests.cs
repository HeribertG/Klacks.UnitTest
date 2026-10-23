// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for UpdateGroupSkill.ExecuteAsync, written before its long body was split into per-field
/// steps: every refusal text, the no-op and success texts with the order of the changed fields, the serialized result
/// data, that fields earlier in the sequence are already applied to the loaded entity when a later field is rejected,
/// when the calendar repository is consulted, and that an API failure is passed through verbatim.
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
public class UpdateGroupSkillCharacterizationTests
{
    private const string GroupsRoute = "api/backend/Groups";

    private static readonly DateTime OriginalFrom = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private IGroupRepository _groupRepository = null!;
    private ICalendarSelectionRepository _calendarSelectionRepository = null!;
    private FakeSelfApi _api = null!;
    private UpdateGroupSkill _skill = null!;
    private Guid _groupId;
    private Group _group = null!;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _calendarSelectionRepository = Substitute.For<ICalendarSelectionRepository>();
        _api = new FakeSelfApi();
        _api.Respond(HttpMethod.Put, GroupsRoute, new GroupResource());

        _groupId = Guid.NewGuid();
        _group = new Group { Id = _groupId, Name = "Bern", Description = "Old", ValidFrom = OriginalFrom };
        _groupRepository.Get(_groupId).Returns(_group);

        _skill = new UpdateGroupSkill(
            _groupRepository,
            TestGroupScopeGuard.Unrestricted(),
            _calendarSelectionRepository,
            new GroupMapper(),
            _api.Client,
            new FixedCompanyClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [TearDown]
    public void TearDown() => _api.Dispose();

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanCreateGroups", "CanEditSettings", "CanViewSettings" },
        AccessToken = new BearerToken("caller-jwt")
    };

    private Task<SkillResult> RunAsync(params (string Key, object Value)[] extra)
    {
        var parameters = new Dictionary<string, object> { ["groupId"] = _groupId.ToString() };
        foreach (var (key, value) in extra)
        {
            parameters[key] = value;
        }

        return _skill.ExecuteAsync(Ctx(), parameters);
    }

    private static JsonElement Data(SkillResult result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result.Data)).RootElement;

    private static List<string> PropertyNames(SkillResult result) =>
        Data(result).EnumerateObject().Select(p => p.Name).ToList();

    [Test]
    public async Task UnknownGroup_ReturnsNotFoundText_WithoutConsultingTheScope()
    {
        var unknownId = Guid.NewGuid();
        _groupRepository.Get(unknownId).Returns((Group?)null);
        var guard = TestGroupScopeGuard.Unrestricted();
        var skill = new UpdateGroupSkill(
            _groupRepository, guard, _calendarSelectionRepository, new GroupMapper(), _api.Client,
            new FixedCompanyClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["groupId"] = unknownId.ToString() });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Group with ID '{unknownId}' not found.");
        await guard.DidNotReceive().GetAccessAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task NoFields_ReturnsNoOpText_AndEmptyChangedFields()
    {
        var result = await RunAsync();

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe("No fields supplied for update — group left unchanged.");
        PropertyNames(result).ShouldBe(new[] { "GroupId", "ChangedFields" });
        Data(result).GetProperty("ChangedFields").GetArrayLength().ShouldBe(0);
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task SameValues_AreNotCountedAsChanges()
    {
        var result = await RunAsync(("name", "Bern"), ("description", "Old"), ("validFrom", "2020-01-01"));

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe("No fields supplied for update — group left unchanged.");
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task InvalidValidFrom_IsRejected_AfterNameAndDescriptionWereAlreadyAppliedToTheEntity()
    {
        var result = await RunAsync(("name", "Bern City"), ("description", "New"), ("validFrom", "irgendwann"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(SkillDateParser.InvalidDateMessageFor("validFrom", "irgendwann"));
        _group.Name.ShouldBe("Bern City");
        _group.Description.ShouldBe("New");
        _group.ValidFrom.ShouldBe(OriginalFrom);
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task InvalidValidUntil_IsRejected_AfterValidFromWasAlreadyApplied()
    {
        var result = await RunAsync(("validFrom", "2026-02-01"), ("validUntil", "irgendwann"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(SkillDateParser.InvalidDateMessageFor("validUntil", "irgendwann"));
        _group.ValidFrom.ShouldBe(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task EmptyValidUntil_ClearsAnExistingEndDate()
    {
        _group.ValidUntil = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await RunAsync(("validUntil", ""));

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe("Group 'Bern' updated (validUntil)");
        _group.ValidUntil.ShouldBeNull();
    }

    [Test]
    public async Task InvalidPaymentInterval_ReturnsExactText()
    {
        var result = await RunAsync(("paymentInterval", "Fortnightly"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Invalid paymentInterval 'Fortnightly'. Allowed: Weekly, Biweekly, Monthly, Individual.");
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task InvalidCalendarId_ReturnsExactText_WithoutAskingTheRepository()
    {
        var result = await RunAsync(("calendarId", "not-a-guid"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Invalid calendarId UUID: not-a-guid");
        await _calendarSelectionRepository.DidNotReceive().Exists(Arg.Any<Guid>());
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task UnknownCalendar_ReturnsExactText()
    {
        var calendarId = Guid.NewGuid();
        _calendarSelectionRepository.Exists(calendarId).Returns(false);

        var result = await RunAsync(("calendarId", calendarId.ToString()));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Calendar selection '{calendarId}' not found. Use list_calendars to see available IDs.");
        await _calendarSelectionRepository.Received(1).Exists(calendarId);
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task EmptyCalendarId_ClearsTheCalendar_WithoutAskingTheRepository()
    {
        _group.CalendarSelectionId = Guid.NewGuid();

        var result = await RunAsync(("calendarId", ""));

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe("Group 'Bern' updated (calendarId)");
        _group.CalendarSelectionId.ShouldBeNull();
        await _calendarSelectionRepository.DidNotReceive().Exists(Arg.Any<Guid>());
    }

    [Test]
    public async Task AllFields_AreReportedInFieldOrder_WithResultData()
    {
        var calendarId = Guid.NewGuid();
        _calendarSelectionRepository.Exists(calendarId).Returns(true);

        var result = await RunAsync(
            ("calendarId", calendarId.ToString()),
            ("paymentInterval", "weekly"),
            ("validUntil", "2026-12-31"),
            ("validFrom", "2026-02-01"),
            ("description", "New"),
            ("name", "Bern City"));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            "Group 'Bern City' updated (name, description, validFrom, validUntil, paymentInterval, calendarId)");
        PropertyNames(result).ShouldBe(new[]
        {
            "GroupId", "ChangedFields", "Name", "ValidFrom", "ValidUntil", "PaymentInterval", "CalendarSelectionId"
        });
        var data = Data(result);
        data.GetProperty("PaymentInterval").GetString().ShouldBe("Weekly");
        data.GetProperty("Name").GetString().ShouldBe("Bern City");
        _group.PaymentInterval.ShouldBe(PaymentInterval.Weekly);
        _group.CalendarSelectionId.ShouldBe(calendarId);
        _group.ValidUntil.ShouldBe(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        _group.CurrentUserUpdated.ShouldBe("tester");
        _api.SingleCall.Method.ShouldBe(HttpMethod.Put);
        _api.BodyOf<GroupResource>()!.Name.ShouldBe("Bern City");
    }

    [Test]
    public async Task ApiFailure_IsPassedThroughVerbatim()
    {
        _api.RespondWithProblem(HttpMethod.Put, GroupsRoute, HttpStatusCode.Conflict, "boom");

        var result = await RunAsync(("name", "Bern City"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("boom");
    }
}
