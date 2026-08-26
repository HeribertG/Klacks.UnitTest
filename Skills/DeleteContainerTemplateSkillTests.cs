// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for DeleteContainerTemplateSkill, the registered inverse of create_container_template.
/// It has to follow the same acquire/write/release lock choreography as its counterpart, report the
/// container-wide blast radius honestly (the endpoint deletes EVERY weekday template, not one), confirm
/// the write from the endpoint's own response rather than a re-read, treat a container without any
/// template as a no-op that never takes the lock, and reject the same up-front cases before any
/// self-call is sent.
/// </summary>

using System.Net;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.Infrastructure.SelfApi;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class DeleteContainerTemplateSkillTests
{
    private const string LockAcquireRoute = "api/backend/ContainerLocks/Acquire";
    private const string ContainerLocksRoute = "api/backend/ContainerLocks";
    private const string ContainersRoute = "api/backend/Containers";
    private const int StorageTuesday = 2;
    private const int StorageSunday = 0;
    private const int IsoSunday = 7;

    private static readonly Guid ContainerId = Guid.NewGuid();
    private static readonly Guid LockId = Guid.NewGuid();

    private IShiftRepository _shiftRepository = null!;
    private IContainerTemplateRepository _containerTemplateRepository = null!;
    private FakeSelfApi _api = null!;
    private DeleteContainerTemplateSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _containerTemplateRepository = Substitute.For<IContainerTemplateRepository>();
        _api = new FakeSelfApi();

        _shiftRepository.Get(ContainerId).Returns(new Shift
        {
            Id = ContainerId,
            Name = "Mobile Dienste Bern",
            ShiftType = ShiftType.IsContainer,
            Status = ShiftStatus.OriginalShift
        });

        _containerTemplateRepository.GetTemplatesForContainer(ContainerId).Returns(ExistingTemplates());

        _api.Respond(HttpMethod.Post, LockAcquireRoute, new ContainerLockResource
        {
            Id = LockId,
            ResourceId = ContainerId,
            Acquired = true
        });

        _api.Respond(HttpMethod.Delete, $"{ContainersRoute}/{ContainerId}/templates", DeletedTemplates());
        _api.RespondToPrefix(HttpMethod.Delete, ContainerLocksRoute, true);

        _skill = new DeleteContainerTemplateSkill(
            _shiftRepository, _containerTemplateRepository, _api.Client, new SelfApiRouteResolver());
    }

    [TearDown]
    public void TearDown() => _api.Dispose();

    [Test]
    public async Task DeletesTheTemplates_BetweenAcquiringAndReleasingTheLock()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        _api.Calls.Count.ShouldBe(3);
        _api.Calls[0].Route.ShouldBe(LockAcquireRoute);
        _api.Calls[1].Method.ShouldBe(HttpMethod.Delete);
        _api.Calls[1].Route.ShouldBe($"{ContainersRoute}/{ContainerId}/templates");
        _api.Calls[2].Method.ShouldBe(HttpMethod.Delete);
        _api.Calls[2].Route.ShouldBe($"{ContainerLocksRoute}/{LockId}");
    }

    [Test]
    public async Task AcquiresTheLockWithTheEmptyInstanceIdTheSelfCallPathForces()
    {
        await _skill.ExecuteAsync(Ctx(), Params());

        var acquire = _api.BodyOf<AcquireContainerLockRequest>(0);

        acquire.ShouldNotBeNull();
        acquire.ResourceId.ShouldBe(ContainerId);
        acquire.ResourceType.ShouldBe("ContainerTemplate");
        acquire.InstanceId.ShouldBe(string.Empty);
    }

    [Test]
    public async Task SaysHowManyTemplatesAndTasksWereRemoved_SoTheBlastRadiusIsNotHidden()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Message.ShouldContain("all 2 weekday template(s)");
        result.Message.ShouldContain("1 configured task(s)");
    }

    [Test]
    public async Task ReportsSundayBackAsIsoSevenNotAsStorageZero()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params());

        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        json.ShouldContain($"\"Weekday\":{IsoSunday}");
        json.ShouldNotContain("\"Weekday\":0");
    }

    [Test]
    public async Task AContainerWithoutAnyTemplate_IsANoOpAndNeverTakesTheLock()
    {
        _containerTemplateRepository.GetTemplatesForContainer(ContainerId)
            .Returns(new List<ContainerTemplate>());

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("nothing was deleted");
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task RejectsAShiftThatIsNotAContainer()
    {
        _shiftRepository.Get(ContainerId).Returns(new Shift
        {
            Id = ContainerId,
            Name = "Frühdienst",
            ShiftType = ShiftType.IsTask,
            Status = ShiftStatus.OriginalShift
        });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("IsContainer");
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task RejectsAnOrderInsteadOfThePlannableContainerShift()
    {
        _shiftRepository.Get(ContainerId).Returns(new Shift
        {
            Id = ContainerId,
            Name = "Mobile Dienste Bern",
            ShiftType = ShiftType.IsContainer,
            Status = ShiftStatus.SealedOrder
        });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("OriginalShift");
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task PointsAtTheCallersOwnOpenContainerPageInsteadOfBlamingAnotherUser()
    {
        _api.Respond(HttpMethod.Post, LockAcquireRoute, new ContainerLockResource
        {
            Id = LockId,
            ResourceId = ContainerId,
            UserName = "tester",
            Acquired = false,
            IsSelfConflict = true
        });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("open for editing yourself");
        _api.Calls.Count.ShouldBe(1);
    }

    [Test]
    public async Task ReleasesTheLockWhenTheDeleteFails()
    {
        _api.RespondWithProblem(
            HttpMethod.Delete,
            $"{ContainersRoute}/{ContainerId}/templates",
            HttpStatusCode.Conflict,
            "Cannot delete: container template is not locked by this session.");

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        _api.Calls.Count.ShouldBe(3);
        _api.Calls[2].Method.ShouldBe(HttpMethod.Delete);
        _api.Calls[2].Route.ShouldBe($"{ContainerLocksRoute}/{LockId}");
    }

    [Test]
    public async Task ReportsAnUnconfirmedWriteWhenTheApiReturnsNoRemovedTemplate()
    {
        _api.Respond(
            HttpMethod.Delete,
            $"{ContainersRoute}/{ContainerId}/templates",
            new List<ContainerTemplateResource>());

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("could not be confirmed");
        _api.Calls[2].Method.ShouldBe(HttpMethod.Delete);
    }

    [Test]
    public async Task RejectsAnUnknownContainerWithoutCallingTheApi()
    {
        _shiftRepository.Get(ContainerId).Returns((Shift?)null);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
        _api.Calls.ShouldBeEmpty();
    }

    private static List<ContainerTemplate> ExistingTemplates() =>
    [
        new()
        {
            Id = Guid.NewGuid(),
            ContainerId = ContainerId,
            Weekday = StorageTuesday,
            FromTime = new TimeOnly(6, 0),
            UntilTime = new TimeOnly(22, 0),
            ContainerTemplateItems = [new ContainerTemplateItem()]
        },
        new()
        {
            Id = Guid.NewGuid(),
            ContainerId = ContainerId,
            Weekday = StorageSunday,
            FromTime = new TimeOnly(8, 0),
            UntilTime = new TimeOnly(18, 0),
            ContainerTemplateItems = []
        }
    ];

    private static List<ContainerTemplateResource> DeletedTemplates() =>
    [
        new() { Id = Guid.NewGuid(), ContainerId = ContainerId, Weekday = StorageTuesday },
        new() { Id = Guid.NewGuid(), ContainerId = ContainerId, Weekday = StorageSunday }
    ];

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
}
