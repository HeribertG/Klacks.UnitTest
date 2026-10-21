// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for CreateContainerTemplateSkill.ExecuteAsync, written before its long body was split
/// into private steps: every refusal text in its precedence order, the lock outcomes, the success text and
/// serialized result data, the posted template, and that the shift repository, visibility guard, template
/// repository and self-API are consulted in this order (and no further than the first refusal).
/// </summary>

using System.Net;
using System.Text.Json;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.Infrastructure.SelfApi;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CreateContainerTemplateSkillCharacterizationTests
{
    private const string LockAcquireRoute = "api/backend/ContainerLocks/Acquire";
    private const string ContainerLocksRoute = "api/backend/ContainerLocks";
    private const string ContainerName = "Mobile Dienste Bern";

    private static readonly Guid ContainerId = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid LockId = Guid.Parse("c0000000-0000-0000-0000-000000000002");
    private static readonly Guid CreatedTemplateId = Guid.Parse("c0000000-0000-0000-0000-000000000003");
    private static readonly string TemplatesRoute = $"api/backend/Containers/{ContainerId}/templates";

    private IShiftRepository _shiftRepository = null!;
    private IContainerTemplateRepository _containerTemplateRepository = null!;
    private IShiftVisibilityGuard _shiftVisibilityGuard = null!;
    private FakeSelfApi _api = null!;
    private CreateContainerTemplateSkill _skill = null!;
    private List<string> _calls = null!;
    private Shift? _container;

    [SetUp]
    public void Setup()
    {
        _calls = [];
        _container = new Shift
        {
            Id = ContainerId,
            Name = ContainerName,
            ShiftType = ShiftType.IsContainer,
            Status = ShiftStatus.OriginalShift
        };

        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftRepository.Get(Arg.Any<Guid>()).Returns(_ => { _calls.Add("shift-get"); return _container; });
        _shiftVisibilityGuard = Substitute.For<IShiftVisibilityGuard>();
        _shiftVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("shift-visible"); return true; });
        _containerTemplateRepository = Substitute.For<IContainerTemplateRepository>();
        _containerTemplateRepository.GetTemplatesForContainer(Arg.Any<Guid>())
            .Returns(_ => { _calls.Add("templates-get"); return new List<ContainerTemplate>(); });

        _api = new FakeSelfApi();
        _api.Respond(HttpMethod.Post, LockAcquireRoute, new ContainerLockResource { Id = LockId, Acquired = true });
        _api.Respond(HttpMethod.Post, TemplatesRoute, Created(2, isHoliday: false, isWeekdayAndHoliday: false));
        _api.RespondToPrefix(HttpMethod.Delete, ContainerLocksRoute, true);

        _skill = new CreateContainerTemplateSkill(
            _shiftRepository, _containerTemplateRepository, _api.Client, new SelfApiRouteResolver(), _shiftVisibilityGuard);
    }

    [TearDown]
    public void TearDown() => _api.Dispose();

    private static List<ContainerTemplateResource> Created(int weekday, bool isHoliday, bool isWeekdayAndHoliday, Guid? id = null) =>
    [
        new()
        {
            Id = id ?? CreatedTemplateId,
            ContainerId = ContainerId,
            Weekday = weekday,
            IsHoliday = isHoliday,
            IsWeekdayAndHoliday = isWeekdayAndHoliday,
            FromTime = new TimeOnly(6, 0),
            UntilTime = new TimeOnly(22, 0)
        }
    ];

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" },
        AccessToken = new BearerToken("caller-jwt")
    };

    private static Dictionary<string, object> Params(params (string Key, object Value)[] overrides)
    {
        var parameters = new Dictionary<string, object>
        {
            ["containerId"] = ContainerId.ToString(),
            ["weekday"] = 2,
            ["fromTime"] = "06:00",
            ["untilTime"] = "22:00"
        };
        foreach (var (key, value) in overrides)
        {
            parameters[key] = value;
        }

        return parameters;
    }

    [Test]
    public async Task Success_ExactMessageDataBodyAndCallOrder()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(("startBase", "Bern Bahnhof")));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            $"Weekday template for weekday 2 created on container '{ContainerName}' " +
            "(06:00-22:00). It is still empty — add task shifts to it with " +
            $"add_container_template_task using containerId=\"{ContainerId}\" and weekday=2.");
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"ContainerTemplateId\":\"" + CreatedTemplateId + "\",\"ContainerId\":\"" + ContainerId + "\"," +
            "\"ContainerName\":\"" + ContainerName + "\",\"Weekday\":2,\"FromTime\":\"06:00\",\"UntilTime\":\"22:00\"," +
            "\"IsHoliday\":false,\"IsWeekdayAndHoliday\":false,\"StartBase\":\"Bern Bahnhof\",\"EndBase\":\"Bern Bahnhof\"," +
            "\"TransportMode\":\"car\",\"Verified\":true}");
        _calls.ShouldBe(["shift-get", "shift-visible", "templates-get"]);
        _api.Calls.Select(c => $"{c.Method} {c.Route} {c.SkillName}").ShouldBe([
            $"POST {LockAcquireRoute} create_container_template",
            $"POST {TemplatesRoute} create_container_template",
            $"DELETE {ContainerLocksRoute}/{LockId} create_container_template"]);

        var posted = _api.BodyOf<List<ContainerTemplateResource>>(1)!.Single();
        posted.Id.ShouldBe(Guid.Empty);
        posted.ContainerId.ShouldBe(ContainerId);
        posted.Weekday.ShouldBe(2);
        posted.IsHoliday.ShouldBeFalse();
        posted.IsWeekdayAndHoliday.ShouldBeFalse();
        posted.StartBase.ShouldBe("Bern Bahnhof");
        posted.EndBase.ShouldBe("Bern Bahnhof");
        posted.TransportMode.ShouldBe(ContainerTransportMode.ByCar);
        posted.ContainerTemplateItems.ShouldBeEmpty();
    }

    [Test]
    public async Task HolidayVariant_WithOwnEndBaseAndRawTransportModeText()
    {
        _api.Respond(HttpMethod.Post, TemplatesRoute, Created(0, isHoliday: true, isWeekdayAndHoliday: true));

        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("weekday", 7),
            ("isHoliday", true),
            ("isWeekdayAndHoliday", true),
            ("startBase", "Bern"),
            ("endBase", "Thun"),
            ("transportMode", " Bicycle ")));

        result.Success.ShouldBeTrue(result.Message);
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"ContainerTemplateId\":\"" + CreatedTemplateId + "\",\"ContainerId\":\"" + ContainerId + "\"," +
            "\"ContainerName\":\"" + ContainerName + "\",\"Weekday\":7,\"FromTime\":\"06:00\",\"UntilTime\":\"22:00\"," +
            "\"IsHoliday\":true,\"IsWeekdayAndHoliday\":true,\"StartBase\":\"Bern\",\"EndBase\":\"Thun\"," +
            "\"TransportMode\":\" Bicycle \",\"Verified\":true}");
        var posted = _api.BodyOf<List<ContainerTemplateResource>>(1)!.Single();
        posted.Weekday.ShouldBe(0);
        posted.TransportMode.ShouldBe(ContainerTransportMode.ByBicycle);
        posted.EndBase.ShouldBe("Thun");
    }

    [Test]
    public async Task InputErrors_HaveTheirExactTexts_InPrecedenceOrder_BeforeAnyLookup()
    {
        (await _skill.ExecuteAsync(Ctx(), Params(("weekday", 0), ("fromTime", "x"))))
            .Message.ShouldBe("weekday must be between 1 (Monday) and 7 (Sunday).");
        (await _skill.ExecuteAsync(Ctx(), Params(("weekday", 8))))
            .Message.ShouldBe("weekday must be between 1 (Monday) and 7 (Sunday).");
        (await _skill.ExecuteAsync(Ctx(), Params(("fromTime", "x"), ("untilTime", "y"))))
            .Message.ShouldBe("Invalid fromTime 'x'. Expected format 'HH:mm'.");
        (await _skill.ExecuteAsync(Ctx(), Params(("untilTime", "y"), ("transportMode", "boat"))))
            .Message.ShouldBe("Invalid untilTime 'y'. Expected format 'HH:mm'.");
        (await _skill.ExecuteAsync(Ctx(), Params(("untilTime", "06:00"), ("transportMode", "boat"))))
            .Message.ShouldBe("untilTime (06:00) must be after fromTime (06:00).");
        (await _skill.ExecuteAsync(Ctx(), Params(("transportMode", "boat"))))
            .Message.ShouldBe("Invalid transportMode 'boat'. Expected one of: car, bicycle, foot, mix.");
        _calls.ShouldBeEmpty();
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task MissingContainer_IsRefused_WithoutAskingTheGuard()
    {
        _container = null;

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Message.ShouldBe($"Shift/container not found: {ContainerId}");
        _calls.ShouldBe(["shift-get"]);
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task NonContainerShift_IsRefused()
    {
        _container!.ShiftType = ShiftType.IsTask;

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Message.ShouldBe($"Shift {ContainerId} is not a container shift (ShiftType must be IsContainer).");
        _calls.ShouldBe(["shift-get", "shift-visible"]);
    }

    [Test]
    public async Task OrderStatus_IsRefusedWithTheStatusName()
    {
        _container!.Status = ShiftStatus.SealedOrder;

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Message.ShouldBe(
            $"Container '{ContainerName}' has status SealedOrder; a template belongs to the plannable " +
            "container shift (status OriginalShift). Pass the plannable shift's id, not the order's — " +
            "get_shift_details or search_shifts with status=container return it.");
        _calls.ShouldBe(["shift-get", "shift-visible"]);
    }

    [Test]
    public async Task DuplicateVariant_IsRefused_ButOtherFlagCombinationsAreNot()
    {
        _containerTemplateRepository.GetTemplatesForContainer(Arg.Any<Guid>())
            .Returns(new List<ContainerTemplate>
            {
                new() { Weekday = 0, IsHoliday = true, IsWeekdayAndHoliday = false }
            });

        var duplicate = await _skill.ExecuteAsync(Ctx(), Params(("weekday", 7), ("isHoliday", true)));
        duplicate.Message.ShouldBe(
            $"Container '{ContainerName}' already has a template for weekday 7 " +
            "(isHoliday=True, isWeekdayAndHoliday=False). " +
            "Use list_container_template to inspect it and add_container_template_task to fill it.");
        _api.Calls.ShouldBeEmpty();

        _api.Respond(HttpMethod.Post, TemplatesRoute, Created(0, isHoliday: true, isWeekdayAndHoliday: true));
        var other = await _skill.ExecuteAsync(Ctx(), Params(("weekday", 7), ("isHoliday", true), ("isWeekdayAndHoliday", true)));
        other.Success.ShouldBeTrue(other.Message);
    }

    [Test]
    public async Task FailedLockRequest_ReturnsTheSelfApiError()
    {
        _api.Respond(HttpMethod.Post, LockAcquireRoute, null, HttpStatusCode.Forbidden);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Message.ShouldBe("Permission denied: your role is not allowed to perform this action.");
        _api.Calls.Count.ShouldBe(1);
    }

    [Test]
    public async Task EmptyLockResponse_IsReportedAsNothingWritten()
    {
        _api.Respond(HttpMethod.Post, LockAcquireRoute, null);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Message.ShouldBe("The container edit lock could not be acquired — nothing was written.");
        _api.Calls.Count.ShouldBe(1);
    }

    [Test]
    public async Task ForeignLock_NamesTheHolder_AndReleasesNothing()
    {
        _api.Respond(HttpMethod.Post, LockAcquireRoute,
            new ContainerLockResource { Id = LockId, Acquired = false, UserName = "Anna" });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Message.ShouldBe($"Container '{ContainerName}' is currently being edited by Anna.");
        _api.Calls.Count.ShouldBe(1);
    }

    [Test]
    public async Task SelfConflict_PointsAtTheOwnOpenPage_AndReleasesNothing()
    {
        _api.Respond(HttpMethod.Post, LockAcquireRoute,
            new ContainerLockResource { Id = LockId, Acquired = false, IsSelfConflict = true, UserName = "tester" });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Message.ShouldBe(
            $"You have container '{ContainerName}' open for editing yourself, and an assistant-driven " +
            "write cannot share that editing session. Close the container page and try again, or create " +
            "the weekday template directly on the page you already have open.");
        _api.Calls.Count.ShouldBe(1);
    }

    [Test]
    public async Task FailedTemplatePost_ReturnsTheSelfApiError_AndReleasesTheLock()
    {
        _api.Respond(HttpMethod.Post, TemplatesRoute, null, HttpStatusCode.Forbidden);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Message.ShouldBe("Permission denied: your role is not allowed to perform this action.");
        _api.Calls.Select(c => c.Method).ShouldBe([HttpMethod.Post, HttpMethod.Post, HttpMethod.Delete]);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task UnmatchedOrUnsavedTemplateInTheResponse_IsReportedAsUnconfirmed_AndReleasesTheLock(bool emptyId)
    {
        _api.Respond(HttpMethod.Post, TemplatesRoute, emptyId
            ? Created(2, isHoliday: false, isWeekdayAndHoliday: false, id: Guid.Empty)
            : Created(2, isHoliday: true, isWeekdayAndHoliday: false));

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "The template for weekday 2 was posted but the API returned no persisted template, " +
            "so the write could not be confirmed.");
        _api.Calls.Select(c => c.Method).ShouldBe([HttpMethod.Post, HttpMethod.Post, HttpMethod.Delete]);
    }

    [Test]
    public async Task FailedRelease_DoesNotChangeTheSuccess()
    {
        _api.Respond(HttpMethod.Delete, $"{ContainerLocksRoute}/{LockId}", null, HttpStatusCode.InternalServerError);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        _api.Calls.Count.ShouldBe(3);
    }

    [Test]
    public async Task ThrowingRelease_IsSwallowed_AndDoesNotChangeTheSuccess()
    {
        _api.Dispose();
        _api = new FakeSelfApi();
        _api.Respond(HttpMethod.Post, LockAcquireRoute, new ContainerLockResource { Id = LockId, Acquired = true });
        _api.Respond(HttpMethod.Post, TemplatesRoute, Created(2, isHoliday: false, isWeekdayAndHoliday: false));
        var skill = new CreateContainerTemplateSkill(
            _shiftRepository, _containerTemplateRepository, _api.Client, new SelfApiRouteResolver(), _shiftVisibilityGuard);

        var result = await skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        _api.Calls.Count.ShouldBe(3);
        _api.Calls[2].Method.ShouldBe(HttpMethod.Delete);
    }

    [Test]
    public async Task MissingRequiredParameters_ThrowInDeclarationOrder()
    {
        (await Should.ThrowAsync<ArgumentException>(() => _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>())))
            .Message.ShouldBe("Required parameter 'containerId' is missing");
        var noWeekday = Params();
        noWeekday.Remove("weekday");
        noWeekday.Remove("fromTime");
        (await Should.ThrowAsync<ArgumentException>(() => _skill.ExecuteAsync(Ctx(), noWeekday)))
            .Message.ShouldBe("Required parameter 'weekday' is missing");
        _calls.ShouldBeEmpty();
    }
}
