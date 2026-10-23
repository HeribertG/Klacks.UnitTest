// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for DeleteContainerTemplateSkill.ExecuteAsync, written before its long body was split
/// into private steps: every refusal text in its precedence order, the lock outcomes, the exact success and no-op
/// texts with their serialized data, that the shift repository, visibility guard and template repository are
/// consulted in this order (and no further than the first refusal), and that the templates route is resolved
/// only after the lock was taken, so a failing resolver still releases it.
/// </summary>

using System.Net;
using System.Text.Json;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.Infrastructure.SelfApi;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class DeleteContainerTemplateSkillCharacterizationTests
{
    private const string LockAcquireRoute = "api/backend/ContainerLocks/Acquire";
    private const string ContainerLocksRoute = "api/backend/ContainerLocks";
    private const string ContainerName = "Mobile Dienste Bern";
    private const string PermissionDenied = "Permission denied: your role is not allowed to perform this action.";

    private static readonly Guid ContainerId = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid LockId = Guid.Parse("d0000000-0000-0000-0000-000000000002");
    private static readonly Guid TuesdayTemplateId = Guid.Parse("d0000000-0000-0000-0000-000000000003");
    private static readonly Guid SundayTemplateId = Guid.Parse("d0000000-0000-0000-0000-000000000004");
    private static readonly string TemplatesRoute = $"api/backend/Containers/{ContainerId}/templates";

    private IShiftRepository _shiftRepository = null!;
    private IContainerTemplateRepository _containerTemplateRepository = null!;
    private IShiftVisibilityGuard _shiftVisibilityGuard = null!;
    private FakeSelfApi _api = null!;
    private DeleteContainerTemplateSkill _skill = null!;
    private List<string> _calls = null!;
    private Shift? _container;
    private List<ContainerTemplate> _existing = null!;

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
        _existing = ExistingTemplates();

        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftRepository.Get(Arg.Any<Guid>()).Returns(_ => { _calls.Add("shift-get"); return _container; });
        _shiftVisibilityGuard = Substitute.For<IShiftVisibilityGuard>();
        _shiftVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("shift-visible"); return true; });
        _containerTemplateRepository = Substitute.For<IContainerTemplateRepository>();
        _containerTemplateRepository.GetTemplatesForContainer(Arg.Any<Guid>())
            .Returns(_ => { _calls.Add("templates-get"); return _existing; });

        _api = new FakeSelfApi();
        _api.Respond(HttpMethod.Post, LockAcquireRoute, new ContainerLockResource { Id = LockId, Acquired = true });
        _api.Respond(HttpMethod.Delete, TemplatesRoute, Removed(3));
        _api.RespondToPrefix(HttpMethod.Delete, ContainerLocksRoute, true);

        _skill = new DeleteContainerTemplateSkill(
            _shiftRepository, _containerTemplateRepository, _api.Client, new SelfApiRouteResolver(), _shiftVisibilityGuard);
    }

    [TearDown]
    public void TearDown() => _api.Dispose();

    private static List<ContainerTemplate> ExistingTemplates() =>
    [
        new()
        {
            Id = SundayTemplateId,
            ContainerId = ContainerId,
            Weekday = 0,
            FromTime = new TimeOnly(8, 0),
            UntilTime = new TimeOnly(18, 30),
            IsHoliday = true,
            IsWeekdayAndHoliday = false,
            StartBase = "Bern",
            EndBase = null,
            ContainerTemplateItems = []
        },
        new()
        {
            Id = TuesdayTemplateId,
            ContainerId = ContainerId,
            Weekday = 2,
            FromTime = new TimeOnly(6, 0),
            UntilTime = new TimeOnly(22, 0),
            IsHoliday = false,
            IsWeekdayAndHoliday = true,
            StartBase = "Thun",
            EndBase = "Spiez",
            ContainerTemplateItems = [new ContainerTemplateItem(), new ContainerTemplateItem()]
        }
    ];

    private static List<ContainerTemplateResource> Removed(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new ContainerTemplateResource { Id = Guid.NewGuid(), ContainerId = ContainerId, Weekday = i })
            .ToList();

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" },
        AccessToken = new BearerToken("caller-jwt")
    };

    private static Dictionary<string, object> Params() => new()
    {
        ["containerId"] = ContainerId.ToString()
    };

    [Test]
    public async Task Success_ExactMessageDataAndCallOrder()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            $"Deleted all 3 weekday template(s) of container '{ContainerName}' " +
            "(2 configured task(s) removed with them). Re-create them with " +
            "create_container_template and add_container_template_task.");
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"ContainerId\":\"" + ContainerId + "\",\"ContainerName\":\"" + ContainerName + "\"," +
            "\"DeletedTemplateCount\":3,\"DeletedItemCount\":2,\"DeletedTemplates\":[" +
            "{\"ContainerTemplateId\":\"" + TuesdayTemplateId + "\",\"Weekday\":2,\"FromTime\":\"06:00\"," +
            "\"UntilTime\":\"22:00\",\"IsHoliday\":false,\"IsWeekdayAndHoliday\":true,\"StartBase\":\"Thun\"," +
            "\"EndBase\":\"Spiez\",\"ItemCount\":2}," +
            "{\"ContainerTemplateId\":\"" + SundayTemplateId + "\",\"Weekday\":7,\"FromTime\":\"08:00\"," +
            "\"UntilTime\":\"18:30\",\"IsHoliday\":true,\"IsWeekdayAndHoliday\":false,\"StartBase\":\"Bern\"," +
            "\"EndBase\":null,\"ItemCount\":0}]," +
            "\"Verified\":true}");
        _calls.ShouldBe(["shift-get", "shift-visible", "templates-get"]);
        _api.Calls.Select(c => $"{c.Method} {c.Route} {c.SkillName}").ShouldBe([
            $"POST {LockAcquireRoute} delete_container_template",
            $"DELETE {TemplatesRoute} delete_container_template",
            $"DELETE {ContainerLocksRoute}/{LockId} delete_container_template"]);
    }

    [Test]
    public async Task NoTemplates_ExactNoOpMessageAndData_WithoutAnySelfCall()
    {
        _existing = [];

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe($"Container '{ContainerName}' has no weekday templates; nothing was deleted.");
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"ContainerId\":\"" + ContainerId + "\",\"ContainerName\":\"" + ContainerName + "\",\"DeletedTemplateCount\":0}");
        _calls.ShouldBe(["shift-get", "shift-visible", "templates-get"]);
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task MissingContainer_IsRefused_WithoutAskingTheGuard()
    {
        _container = null;

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Shift/container not found: {ContainerId}");
        _calls.ShouldBe(["shift-get"]);
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task NonContainerShift_IsRefused_BeforeTheTemplatesAreLoaded()
    {
        _container!.ShiftType = ShiftType.IsTask;

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Shift {ContainerId} is not a container shift (ShiftType must be IsContainer).");
        _calls.ShouldBe(["shift-get", "shift-visible"]);
    }

    [Test]
    public async Task OrderStatus_IsRefusedWithTheStatusName_BeforeTheTemplatesAreLoaded()
    {
        _container!.Status = ShiftStatus.SealedOrder;

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            $"Container '{ContainerName}' has status SealedOrder; templates belong to the plannable " +
            "container shift (status OriginalShift). Pass the plannable shift's id, not the order's — " +
            "get_shift_details or search_shifts with status=container return it.");
        _calls.ShouldBe(["shift-get", "shift-visible"]);
    }

    [Test]
    public async Task FailedLockRequest_ReturnsTheSelfApiError()
    {
        _api.Respond(HttpMethod.Post, LockAcquireRoute, null, HttpStatusCode.Forbidden);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(PermissionDenied);
        _api.Calls.Count.ShouldBe(1);
    }

    [Test]
    public async Task EmptyLockResponse_IsReportedAsNothingDeleted()
    {
        _api.Respond(HttpMethod.Post, LockAcquireRoute, null);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("The container edit lock could not be acquired — nothing was deleted.");
        _api.Calls.Count.ShouldBe(1);
    }

    [Test]
    public async Task ForeignLock_NamesTheHolder_AndReleasesNothing()
    {
        _api.Respond(HttpMethod.Post, LockAcquireRoute,
            new ContainerLockResource { Id = LockId, Acquired = false, UserName = "Anna" });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Container '{ContainerName}' is currently being edited by Anna.");
        _api.Calls.Count.ShouldBe(1);
    }

    [Test]
    public async Task SelfConflict_ExactText_AndReleasesNothing()
    {
        _api.Respond(HttpMethod.Post, LockAcquireRoute,
            new ContainerLockResource { Id = LockId, Acquired = false, IsSelfConflict = true, UserName = "tester" });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            $"You have container '{ContainerName}' open for editing yourself, and an assistant-driven " +
            "write cannot share that editing session. Close the container page and try again, or delete " +
            "the weekday templates directly on the page you already have open.");
        _api.Calls.Count.ShouldBe(1);
    }

    [Test]
    public async Task FailedDelete_ReturnsTheSelfApiError_AndReleasesTheLock()
    {
        _api.Respond(HttpMethod.Delete, TemplatesRoute, null, HttpStatusCode.Forbidden);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(PermissionDenied);
        _api.Calls.Select(c => c.Method).ShouldBe([HttpMethod.Post, HttpMethod.Delete, HttpMethod.Delete]);
    }

    [Test]
    public async Task DeleteWithoutBody_IsReportedUnconfirmed_AndReleasesTheLock()
    {
        _api.Respond(HttpMethod.Delete, TemplatesRoute, null);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            $"The delete of the weekday templates of container '{ContainerName}' returned no removed " +
            "template, so the write could not be confirmed.");
        _api.Calls.Count.ShouldBe(3);
        _api.Calls[2].Route.ShouldBe($"{ContainerLocksRoute}/{LockId}");
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
    public async Task TemplatesRoute_IsResolvedAfterTheLock_SoAFailingResolverStillReleasesIt()
    {
        var routes = Substitute.For<ISelfApiRouteResolver>();
        routes.Resolve(Arg.Any<Type>()).Returns<string>(_ =>
        {
            _calls.Add("route");
            throw new InvalidOperationException("no route");
        });
        var skill = new DeleteContainerTemplateSkill(
            _shiftRepository, _containerTemplateRepository, _api.Client, routes, _shiftVisibilityGuard);

        (await Should.ThrowAsync<InvalidOperationException>(() => skill.ExecuteAsync(Ctx(), Params())))
            .Message.ShouldBe("no route");
        _calls.ShouldBe(["shift-get", "shift-visible", "templates-get", "route"]);
        _api.Calls.Select(c => $"{c.Method} {c.Route}").ShouldBe([
            $"POST {LockAcquireRoute}",
            $"DELETE {ContainerLocksRoute}/{LockId}"]);
    }

    [Test]
    public async Task MissingContainerId_Throws_BeforeAnyLookup()
    {
        (await Should.ThrowAsync<ArgumentException>(() => _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>())))
            .Message.ShouldBe("Required parameter 'containerId' is missing");
        _calls.ShouldBeEmpty();
    }
}
