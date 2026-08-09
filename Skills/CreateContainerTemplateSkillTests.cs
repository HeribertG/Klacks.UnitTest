// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CreateContainerTemplateSkill: a weekday template is written over the REST API between
/// acquiring and releasing the container edit lock, the lock is acquired with the empty instance id the
/// self-call path forces, ISO Sunday is stored as weekday 0, and every up-front rejection (no container,
/// order instead of plannable shift, duplicate weekday variant, bad weekday, bad time span) happens
/// before any self-call is sent.
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
public class CreateContainerTemplateSkillTests
{
    private const string LockAcquireRoute = "api/backend/ContainerLocks/Acquire";
    private const string ContainerLocksRoute = "api/backend/ContainerLocks";
    private const string ContainersRoute = "api/backend/Containers";
    private const int IsoTuesday = 2;
    private const int IsoSunday = 7;
    private const int StorageSunday = 0;

    private static readonly Guid ContainerId = Guid.NewGuid();
    private static readonly Guid LockId = Guid.NewGuid();
    private static readonly Guid CreatedTemplateId = Guid.NewGuid();

    private IShiftRepository _shiftRepository = null!;
    private IContainerTemplateRepository _containerTemplateRepository = null!;
    private FakeSelfApi _api = null!;
    private CreateContainerTemplateSkill _skill = null!;

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

        _containerTemplateRepository.GetTemplatesForContainer(ContainerId)
            .Returns(new List<ContainerTemplate>());

        _api.Respond(HttpMethod.Post, LockAcquireRoute, new ContainerLockResource
        {
            Id = LockId,
            ResourceId = ContainerId,
            Acquired = true
        });

        RespondToTemplatesPost(IsoTuesday);
        _api.RespondToPrefix(HttpMethod.Delete, ContainerLocksRoute, true);

        _skill = new CreateContainerTemplateSkill(
            _shiftRepository, _containerTemplateRepository, _api.Client, new SelfApiRouteResolver());
    }

    [TearDown]
    public void TearDown() => _api.Dispose();

    [Test]
    public async Task CreatesTheTemplate_PostsItAndReleasesTheLockAfterwards()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        _api.Calls.Count.ShouldBe(3);
        _api.Calls[0].Route.ShouldBe(LockAcquireRoute);
        _api.Calls[1].Route.ShouldBe($"{ContainersRoute}/{ContainerId}/templates");
        _api.Calls[1].Method.ShouldBe(HttpMethod.Post);
        _api.Calls[2].Method.ShouldBe(HttpMethod.Delete);
        _api.Calls[2].Route.ShouldBe($"{ContainerLocksRoute}/{LockId}");
    }

    [Test]
    public async Task PostsAnEmptyTemplateCarryingTheRequestedTimeBudget()
    {
        await _skill.ExecuteAsync(Ctx(), Params());

        var posted = _api.BodyOf<List<ContainerTemplateResource>>(1);

        posted.ShouldNotBeNull();
        posted.Count.ShouldBe(1);
        posted[0].ContainerId.ShouldBe(ContainerId);
        posted[0].Weekday.ShouldBe(IsoTuesday);
        posted[0].FromTime.ShouldBe(new TimeOnly(6, 0));
        posted[0].UntilTime.ShouldBe(new TimeOnly(22, 0));
        posted[0].ContainerTemplateItems.ShouldBeEmpty();
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
    public async Task StoresIsoSundayAsWeekdayZero()
    {
        _api = RebuildApiForWeekday(StorageSunday);

        var parameters = Params();
        parameters["weekday"] = IsoSunday;

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue(result.Message);
        _api.BodyOf<List<ContainerTemplateResource>>(1)![0].Weekday.ShouldBe(StorageSunday);
    }

    [Test]
    public async Task RejectsADuplicateWeekdayVariantWithoutCallingTheApi()
    {
        _containerTemplateRepository.GetTemplatesForContainer(ContainerId).Returns(new List<ContainerTemplate>
        {
            new() { ContainerId = ContainerId, Weekday = IsoTuesday, IsHoliday = false, IsWeekdayAndHoliday = false }
        });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("already has a template");
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
    public async Task RejectsAWeekdayOutsideTheIsoRange()
    {
        var parameters = Params();
        parameters["weekday"] = 8;

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("weekday must be between 1");
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task RejectsAnUntilTimeThatIsNotAfterFromTime()
    {
        var parameters = Params();
        parameters["untilTime"] = "06:00";

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("must be after fromTime");
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task NamesTheForeignHolderOfTheLockWithoutPostingATemplate()
    {
        _api.Respond(HttpMethod.Post, LockAcquireRoute, new ContainerLockResource
        {
            Id = LockId,
            ResourceId = ContainerId,
            UserName = "petra",
            Acquired = false,
            IsSelfConflict = false
        });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("being edited by petra");
        _api.Calls.Count.ShouldBe(1);
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
        result.Message.ShouldNotContain("another user");
        _api.Calls.Count.ShouldBe(1);
    }

    [Test]
    public async Task ReleasesTheLockWhenThePostFails()
    {
        _api.RespondWithProblem(
            HttpMethod.Post,
            $"{ContainersRoute}/{ContainerId}/templates",
            HttpStatusCode.Conflict,
            "Cannot save: container template is not locked by this session.");

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        _api.Calls.Count.ShouldBe(3);
        _api.Calls[2].Method.ShouldBe(HttpMethod.Delete);
    }

    [Test]
    public async Task ReportsAnUnconfirmedWriteWhenTheApiReturnsNoPersistedTemplate()
    {
        _api.Respond(
            HttpMethod.Post,
            $"{ContainersRoute}/{ContainerId}/templates",
            new List<ContainerTemplateResource>());

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("could not be confirmed");
        _api.Calls[2].Method.ShouldBe(HttpMethod.Delete);
    }

    private FakeSelfApi RebuildApiForWeekday(int storageWeekday)
    {
        _api.Dispose();
        var api = new FakeSelfApi();
        api.Respond(HttpMethod.Post, LockAcquireRoute, new ContainerLockResource
        {
            Id = LockId,
            ResourceId = ContainerId,
            Acquired = true
        });
        api.Respond(HttpMethod.Post, $"{ContainersRoute}/{ContainerId}/templates", BuiltTemplates(storageWeekday));
        api.RespondToPrefix(HttpMethod.Delete, ContainerLocksRoute, true);

        _skill = new CreateContainerTemplateSkill(
            _shiftRepository, _containerTemplateRepository, api.Client, new SelfApiRouteResolver());

        return api;
    }

    private void RespondToTemplatesPost(int storageWeekday) =>
        _api.Respond(HttpMethod.Post, $"{ContainersRoute}/{ContainerId}/templates", BuiltTemplates(storageWeekday));

    private static List<ContainerTemplateResource> BuiltTemplates(int storageWeekday) =>
    [
        new()
        {
            Id = CreatedTemplateId,
            ContainerId = ContainerId,
            Weekday = storageWeekday,
            IsHoliday = false,
            IsWeekdayAndHoliday = false,
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

    private static Dictionary<string, object> Params() => new()
    {
        ["containerId"] = ContainerId.ToString(),
        ["weekday"] = IsoTuesday,
        ["fromTime"] = "06:00",
        ["untilTime"] = "22:00"
    };
}
