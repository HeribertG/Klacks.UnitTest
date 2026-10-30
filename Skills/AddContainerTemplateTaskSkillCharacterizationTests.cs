// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for AddContainerTemplateTaskSkill.ExecuteAsync, written before its long body was split
/// into private steps: every refusal text in its precedence order (and how far the calls get before it), the
/// in-lock outcomes (template gone, ContainerLockedException, unverified write) that still release the lock, the
/// exact success text and serialized data, the posted item, the position rules, the ISO Sunday mapping and the
/// caller's cancellation token on the write and the release.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands.ContainerTemplates;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries.ContainerTemplates;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddContainerTemplateTaskSkillCharacterizationTests
{
    private const int StorageTuesday = 2;
    private const int StorageWednesday = 3;
    private const int StorageSunday = 0;

    private static readonly Guid ContainerId = Guid.Parse("a1000000-0000-0000-0000-000000000001");
    private static readonly Guid TaskShiftId = Guid.Parse("a1000000-0000-0000-0000-000000000002");
    private static readonly Guid LockId = Guid.Parse("a1000000-0000-0000-0000-000000000003");
    private static readonly Guid ExistingShiftA = Guid.Parse("a1000000-0000-0000-0000-000000000004");
    private static readonly Guid ExistingShiftB = Guid.Parse("a1000000-0000-0000-0000-000000000005");

    private IShiftRepository _shiftRepository = null!;
    private IShiftVisibilityGuard _shiftVisibilityGuard = null!;
    private IMediator _mediator = null!;
    private IUserService _userService = null!;
    private IContainerAvailableTasksService _availableTasksService = null!;
    private AddContainerTemplateTaskSkill _skill = null!;
    private List<string> _calls = null!;
    private Shift? _container;
    private Shift? _taskShift;
    private List<Shift> _availableTasks = null!;
    private List<ContainerTemplateResource> _precheckTemplates = null!;
    private List<ContainerTemplateResource> _lockedTemplates = null!;
    private List<ContainerTemplateResource>? _putResult;
    private PutContainerTemplatesCommand? _putCommand;
    private int _templateQueries;

    [SetUp]
    public void Setup()
    {
        _calls = [];
        _templateQueries = 0;
        _putResult = null;
        _putCommand = null;
        _container = new Shift { Id = ContainerId, ShiftType = ShiftType.IsContainer };
        _taskShift = new Shift
        {
            Id = TaskShiftId,
            ShiftType = ShiftType.IsTask,
            Abbreviation = "T1",
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            BriefingTime = new TimeOnly(0, 5),
            DebriefingTime = new TimeOnly(0, 6),
            TravelTimeBefore = new TimeOnly(0, 10),
            TravelTimeAfter = new TimeOnly(0, 11)
        };
        _availableTasks = [_taskShift];
        _precheckTemplates = [Template(StorageTuesday)];
        _lockedTemplates = _precheckTemplates;

        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftRepository.Get(Arg.Any<Guid>()).Returns(call =>
        {
            var id = call.ArgAt<Guid>(0);
            _calls.Add(id == ContainerId ? "shift-get:container" : "shift-get:task");
            return id == ContainerId ? _container : _taskShift;
        });

        _shiftVisibilityGuard = Substitute.For<IShiftVisibilityGuard>();
        _shiftVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _calls.Add(call.ArgAt<Guid>(0) == ContainerId ? "visible:container" : "visible:task");
            return true;
        });

        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _templateQueries++;
            _calls.Add("templates-query");
            return _templateQueries == 1 ? _precheckTemplates : _lockedTemplates;
        });
        _mediator.Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _calls.Add("acquire");
            return new ContainerLockResource { Id = LockId, Acquired = true };
        });
        _mediator.Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _calls.Add("put");
            _putCommand = call.ArgAt<PutContainerTemplatesCommand>(0);
            return _putResult ?? _putCommand.Resources;
        });
        _mediator.Send(Arg.Any<ReleaseContainerLockCommand>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _calls.Add("release");
            return true;
        });

        _userService = Substitute.For<IUserService>();
        _userService.GetInstanceId().Returns("instance-1");

        _availableTasksService = Substitute.For<IContainerAvailableTasksService>();
        _availableTasksService.GetAvailableTasksAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<TimeOnly>(), Arg.Any<TimeOnly>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _calls.Add("available-tasks");
                return _availableTasks;
            });

        _skill = new AddContainerTemplateTaskSkill(
            _shiftRepository, _mediator, _userService, _availableTasksService, _shiftVisibilityGuard);
    }

    private static ContainerTemplateResource Template(int weekday, params Guid[] itemShiftIds) => new()
    {
        Id = Guid.NewGuid(),
        ContainerId = ContainerId,
        Weekday = weekday,
        IsHoliday = false,
        IsWeekdayAndHoliday = false,
        FromTime = new TimeOnly(6, 0),
        UntilTime = new TimeOnly(22, 0),
        ContainerTemplateItems = itemShiftIds.Select(id => new ContainerTemplateItemResource { ShiftId = id }).ToList()
    };

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static Dictionary<string, object> Params(int weekday = StorageTuesday, int? position = null)
    {
        var parameters = new Dictionary<string, object>
        {
            ["containerId"] = ContainerId.ToString(),
            ["weekday"] = weekday,
            ["taskShiftId"] = TaskShiftId.ToString()
        };
        if (position.HasValue)
        {
            parameters["position"] = position.Value;
        }

        return parameters;
    }

    [Test]
    public async Task Success_ExactMessageDataPostedItemAndCallOrder()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            "Task 'T1' added to the container template for weekday 2 and confirmed in the database (verified).");
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"ContainerId\":\"" + ContainerId + "\",\"Weekday\":2,\"TaskShiftId\":\"" + TaskShiftId + "\"," +
            "\"TaskAbbreviation\":\"T1\",\"Position\":1,\"Verified\":true}");
        _calls.ShouldBe([
            "shift-get:container", "visible:container", "shift-get:task", "visible:task",
            "templates-query", "available-tasks", "acquire", "templates-query", "put", "release"]);

        var item = _putCommand!.Resources.Single().ContainerTemplateItems.Single();
        item.Id.ShouldBe(Guid.Empty);
        item.ShiftId.ShouldBe(TaskShiftId);
        item.AbsenceId.ShouldBeNull();
        item.StartItem.ShouldBe(new TimeOnly(8, 0));
        item.EndItem.ShouldBe(new TimeOnly(16, 0));
        item.BriefingTime.ShouldBe(new TimeOnly(0, 5));
        item.DebriefingTime.ShouldBe(new TimeOnly(0, 6));
        item.TravelTimeBefore.ShouldBe(new TimeOnly(0, 10));
        item.TravelTimeAfter.ShouldBe(new TimeOnly(0, 11));
        item.TimeRangeStartItem.ShouldBeNull();
        item.TimeRangeEndItem.ShouldBeNull();
        item.TransportMode.ShouldBe(TransportMode.ByCar);
        _putCommand.ContainerId.ShouldBe(ContainerId);
    }

    [Test]
    public async Task Success_AcquiresTheLockWithTheInstanceId_PassesTheCallersTokenToTheWrite_AndReleasesWithoutIt()
    {
        using var cts = new CancellationTokenSource();

        var result = await _skill.ExecuteAsync(Ctx(), Params(), cts.Token);

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<AcquireContainerLockCommand>(c =>
                c.ResourceType == "ContainerTemplate" && c.ResourceId == ContainerId && c.InstanceId == "instance-1"),
            cts.Token);
        await _mediator.Received(1).Send(Arg.Any<PutContainerTemplatesCommand>(), cts.Token);
        await _mediator.Received(1).Send(Arg.Is<ReleaseContainerLockCommand>(c => c.LockId == LockId), CancellationToken.None);
        await _availableTasksService.Received(1).GetAvailableTasksAsync(
            ContainerId, StorageTuesday, new TimeOnly(6, 0), new TimeOnly(22, 0),
            Arg.Any<string?>(), Arg.Any<Guid?>(),
            Arg.Is<bool?>(v => v == false), Arg.Is<bool?>(v => v == false),
            Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<IReadOnlyCollection<Guid>?>(), cts.Token);
    }

    [Test]
    public async Task Success_WithoutInstanceId_AcquiresWithAnEmptyInstanceId()
    {
        _userService.GetInstanceId().Returns((string?)null);

        await _skill.ExecuteAsync(Ctx(), Params());

        await _mediator.Received(1).Send(
            Arg.Is<AcquireContainerLockCommand>(c => c.InstanceId == string.Empty), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Success_SubmitsTheCompleteTemplateList_LeavingOtherWeekdaysUntouched()
    {
        var other = Template(StorageWednesday, ExistingShiftA);
        _lockedTemplates = [Template(StorageTuesday), other];

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        _putCommand!.Resources.Count.ShouldBe(2);
        _putCommand.Resources[1].ShouldBeSameAs(other);
        other.ContainerTemplateItems.Select(i => i.ShiftId).ShouldBe([ExistingShiftA]);
        _putCommand.Resources[0].ContainerTemplateItems.Select(i => i.ShiftId).ShouldBe([TaskShiftId]);
    }

    [TestCase(null, 2, 3)]
    [TestCase(1, 0, 1)]
    [TestCase(2, 1, 2)]
    [TestCase(3, 2, 3)]
    [TestCase(4, 2, 3)]
    [TestCase(0, 2, 3)]
    [TestCase(-1, 2, 3)]
    public async Task Position_InsertsAtTheOneBasedIndex_OrAppendsWhenOmittedOrOutOfRange(
        int? position, int expectedIndex, int expectedReportedPosition)
    {
        _lockedTemplates = [Template(StorageTuesday, ExistingShiftA, ExistingShiftB)];

        var result = await _skill.ExecuteAsync(Ctx(), Params(position: position));

        result.Success.ShouldBeTrue(result.Message);
        var shiftIds = _putCommand!.Resources.Single().ContainerTemplateItems.Select(i => i.ShiftId).ToList();
        shiftIds.Count.ShouldBe(3);
        shiftIds[expectedIndex].ShouldBe(TaskShiftId);
        JsonSerializer.Serialize(result.Data).ShouldContain($"\"Position\":{expectedReportedPosition},");
    }

    [Test]
    public async Task IsoSunday_IsStoredAsWeekdayZero_AndReportedAsSeven()
    {
        _precheckTemplates = [Template(StorageSunday)];
        _lockedTemplates = _precheckTemplates;

        var result = await _skill.ExecuteAsync(Ctx(), Params(weekday: 7));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            "Task 'T1' added to the container template for weekday 7 and confirmed in the database (verified).");
        JsonSerializer.Serialize(result.Data).ShouldContain("\"Weekday\":7,");
        await _availableTasksService.Received(1).GetAvailableTasksAsync(
            ContainerId, Arg.Is<int>(w => w == StorageSunday), Arg.Any<TimeOnly>(), Arg.Any<TimeOnly>(),
            Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<bool?>(), Arg.Any<bool?>(),
            Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>());
    }

    [TestCase(0)]
    [TestCase(8)]
    public async Task WeekdayOutOfRange_IsRejectedBeforeAnyLookup(int weekday)
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(weekday: weekday));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("weekday must be between 1 (Monday) and 7 (Sunday).");
        _calls.ShouldBeEmpty();
    }

    [Test]
    public async Task MissingContainer_IsRejectedWithoutConsultingTheVisibilityGuard()
    {
        _container = null;

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Shift/container not found: {ContainerId}");
        _calls.ShouldBe(["shift-get:container"]);
    }

    [Test]
    public async Task NonContainerShift_IsRejectedBeforeTheTaskShiftIsLoaded()
    {
        _container!.ShiftType = ShiftType.IsTask;

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Shift {ContainerId} is not a container shift (ShiftType must be IsContainer).");
        _calls.ShouldBe(["shift-get:container", "visible:container"]);
    }

    [Test]
    public async Task MissingTaskShift_IsRejectedBeforeAnyTemplateQuery()
    {
        _taskShift = null;

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Task shift not found: {TaskShiftId}");
        _calls.ShouldBe(["shift-get:container", "visible:container", "shift-get:task"]);
    }

    [Test]
    public async Task NoTemplateForTheWeekday_IsRejectedBeforeTheEligibilityCheck()
    {
        _precheckTemplates = [Template(StorageWednesday)];

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "No container template configured for this weekday yet — configure the container template for this weekday first.");
        _calls.ShouldBe(["shift-get:container", "visible:container", "shift-get:task", "visible:task", "templates-query"]);
    }

    [Test]
    public async Task IneligibleTask_WithoutAnyAvailableTask_ListsNone_AndNeverTakesTheLock()
    {
        _availableTasks = [];

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            $"Task shift {TaskShiftId} is not currently eligible for this container on this weekday " +
            "(it may be sporadic, the wrong shift type, below the minimum status, a scenario copy, outside the " +
            "template's time window, not valid during the container's validity period, or already used elsewhere). " +
            "Currently available tasks: none.");
        _calls.ShouldNotContain("acquire");
        _calls.ShouldNotContain("put");
    }

    [Test]
    public async Task IneligibleTask_ListsAtMostTenAvailableAbbreviations()
    {
        _availableTasks = Enumerable.Range(1, 11)
            .Select(i => new Shift { Id = Guid.NewGuid(), Abbreviation = $"A{i}" })
            .ToList();

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldEndWith("Currently available tasks: A1, A2, A3, A4, A5, A6, A7, A8, A9, A10.");
        _calls.ShouldNotContain("acquire");
    }

    [Test]
    public async Task LockNotAcquired_IsRejectedWithoutAnyWriteOrRelease()
    {
        _mediator.Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerLockResource { Id = LockId, Acquired = false });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("The container is currently being edited by another user.");
        _calls.ShouldNotContain("put");
        _calls.ShouldNotContain("release");
        _templateQueries.ShouldBe(1);
    }

    [Test]
    public async Task TemplateGoneUnderTheLock_IsRejected_AndTheLockIsReleased()
    {
        _lockedTemplates = [Template(StorageWednesday)];

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "No container template configured for this weekday yet — configure the container template for this weekday first.");
        _calls.ShouldBe([
            "shift-get:container", "visible:container", "shift-get:task", "visible:task",
            "templates-query", "available-tasks", "acquire", "templates-query", "release"]);
    }

    [Test]
    public async Task ContainerLockedOnWrite_ReturnsTheExceptionMessage_AndTheLockIsReleased()
    {
        _mediator.Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>())
            .Returns<List<ContainerTemplateResource>>(_ =>
            {
                _calls.Add("put");
                throw new ContainerLockedException("Container template is locked by someone else.");
            });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Container template is locked by someone else.");
        _calls.TakeLast(2).ShouldBe(["put", "release"]);
    }

    [Test]
    public async Task OtherWriteFailure_PropagatesAfterTheLockIsReleased()
    {
        _mediator.Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>())
            .Returns<List<ContainerTemplateResource>>(_ => throw new InvalidOperationException("boom"));

        await Should.ThrowAsync<InvalidOperationException>(() => _skill.ExecuteAsync(Ctx(), Params()));

        _calls.Last().ShouldBe("release");
    }

    [Test]
    public async Task WriteWithoutTheTaskInTheMatchingTemplate_IsReportedUnverified_AndTheLockIsReleased()
    {
        _putResult = [Template(StorageTuesday, ExistingShiftA)];

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            $"Verification failed: task shift {TaskShiftId} could not be confirmed in the container template after the write.");
        _calls.Last().ShouldBe("release");
    }

    [Test]
    public async Task WriteResultWithoutAMatchingTemplateSlot_IsReportedUnverified()
    {
        var holidaySlot = Template(StorageTuesday, TaskShiftId);
        holidaySlot.IsHoliday = true;
        _putResult = [holidaySlot];

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldStartWith("Verification failed:");
    }

    [Test]
    public async Task ReleaseFailure_IsSwallowed_AndTheSuccessStands()
    {
        _mediator.Send(Arg.Any<ReleaseContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("release failed"));

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
    }
}
