// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for BundleNearbyTimeRangeShiftsIntoContainerSkill: a container shift is created, autofilled
/// via IContainerAutofillService and its weekday template is written and re-read to confirm the write,
/// all inside one transaction that rolls back completely when nothing is found or the write cannot be
/// confirmed; the OpenRouteService key gate blocks non-car transport modes up front, before any write.
/// </summary>

using Klacks.Api.Application.Commands.ContainerTemplates;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries.ContainerTemplates;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Services.RouteOptimization;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class BundleNearbyTimeRangeShiftsIntoContainerSkillTests
{
    private const int StorageWeekday = 3;
    private const int IsoWeekday = 3;
    private const string Location = "Bern";

    private IShiftRepository _shiftRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IMediator _mediator = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IUserService _userService = null!;
    private IContainerAutofillService _containerAutofillService = null!;
    private ISettingsReader _settingsReader = null!;
    private ISettingsEncryptionService _encryptionService = null!;
    private BundleNearbyTimeRangeShiftsIntoContainerSkill _skill = null!;

    private static readonly Guid ContainerId = Guid.NewGuid();
    private static readonly Guid TaskShiftId1 = Guid.NewGuid();
    private static readonly Guid TaskShiftId2 = Guid.NewGuid();
    private static readonly Guid LockId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _mediator = Substitute.For<IMediator>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _userService = Substitute.For<IUserService>();
        _containerAutofillService = Substitute.For<IContainerAutofillService>();
        _settingsReader = Substitute.For<ISettingsReader>();
        _encryptionService = Substitute.For<ISettingsEncryptionService>();

        _skill = new BundleNearbyTimeRangeShiftsIntoContainerSkill(
            _shiftRepository,
            _groupRepository,
            _mediator,
            _unitOfWork,
            _userService,
            _containerAutofillService,
            _settingsReader,
            _encryptionService);

        _userService.GetInstanceId().Returns("instance-1");

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<BundleNearbyTimeRangeShiftsIntoContainerSkill.BundleOutcome>>>())
            .Returns(ci => ci.Arg<Func<Task<BundleNearbyTimeRangeShiftsIntoContainerSkill.BundleOutcome>>>()());

        _shiftRepository.AddWithSealedOrderHandling(Arg.Any<Shift>())
            .Returns(ci =>
            {
                var submitted = ci.Arg<Shift>();
                return new Shift
                {
                    Id = ContainerId,
                    Name = submitted.Name,
                    Abbreviation = submitted.Abbreviation,
                    Status = ShiftStatus.OriginalShift,
                    ShiftType = ShiftType.IsContainer,
                    OriginalId = submitted.Id
                };
            });

        _shiftRepository.Get(TaskShiftId1).Returns(TaskShift(TaskShiftId1, new TimeOnly(8, 0), new TimeOnly(9, 0)));
        _shiftRepository.Get(TaskShiftId2).Returns(TaskShift(TaskShiftId2, new TimeOnly(10, 0), new TimeOnly(11, 0)));

        _mediator.Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerLockResource { Id = LockId, Acquired = true });
        _mediator.Send(Arg.Any<ReleaseContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _mediator.Send(Arg.Any<PostContainerTemplatesCommand>(), Arg.Any<CancellationToken>())
            .Returns(new List<ContainerTemplateResource>());
    }

    private static Shift TaskShift(Guid id, TimeOnly start, TimeOnly end) => new()
    {
        Id = id,
        ShiftType = ShiftType.IsTask,
        IsTimeRange = true,
        Abbreviation = "T",
        StartShift = start,
        EndShift = end,
        BriefingTime = new TimeOnly(0, 5),
        DebriefingTime = new TimeOnly(0, 5),
        TravelTimeBefore = new TimeOnly(0, 10),
        TravelTimeAfter = new TimeOnly(0, 10)
    };

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static Dictionary<string, object> Params(string transportMode = "car") => new()
    {
        ["location"] = Location,
        ["weekday"] = IsoWeekday,
        ["fromTime"] = "08:00",
        ["untilTime"] = "18:00",
        ["fromDate"] = "2026-06-01",
        ["transportMode"] = transportMode
    };

    private static ContainerAutofillResult SuccessfulAutofillResult() => new(
        OptimizedRoute: new List<Location>(),
        SelectedShiftIds: new List<Guid> { TaskShiftId1, TaskShiftId2 },
        TotalDistanceKm: 12.5,
        EstimatedTravelTime: TimeSpan.FromHours(2),
        TotalWorkTime: TimeSpan.FromHours(2),
        RemainingTime: TimeSpan.FromHours(6),
        TotalAvailableShifts: 3,
        SelectedShiftCount: 2,
        DistanceMatrix: new double[0, 0],
        DurationMatrix: new double[0, 0],
        TravelTimeFromStartBase: TimeSpan.Zero,
        RouteIndices: new List<int>(),
        DistanceFromStartBaseKm: 0,
        DistanceToEndBaseKm: 0,
        TravelTimeToEndBase: TimeSpan.Zero,
        FullRouteIndices: new List<int>());

    private static ContainerAutofillResult EmptyAutofillResult() => new(
        OptimizedRoute: new List<Location>(),
        SelectedShiftIds: new List<Guid>(),
        TotalDistanceKm: 0,
        EstimatedTravelTime: TimeSpan.Zero,
        TotalWorkTime: TimeSpan.Zero,
        RemainingTime: TimeSpan.Zero,
        TotalAvailableShifts: 0,
        SelectedShiftCount: 0,
        DistanceMatrix: new double[0, 0],
        DurationMatrix: new double[0, 0],
        TravelTimeFromStartBase: TimeSpan.Zero,
        RouteIndices: new List<int>(),
        DistanceFromStartBaseKm: 0,
        DistanceToEndBaseKm: 0,
        TravelTimeToEndBase: TimeSpan.Zero,
        FullRouteIndices: new List<int>());

    private static List<ContainerTemplateResource> PersistedTemplate(IEnumerable<Guid> shiftIds) => new()
    {
        new ContainerTemplateResource
        {
            Id = Guid.NewGuid(),
            ContainerId = ContainerId,
            Weekday = StorageWeekday,
            IsHoliday = false,
            IsWeekdayAndHoliday = false,
            FromTime = new TimeOnly(8, 0),
            UntilTime = new TimeOnly(18, 0),
            ContainerTemplateItems = shiftIds.Select(id => new ContainerTemplateItemResource { ShiftId = id }).ToList()
        }
    };

    [Test]
    public async Task CreatesContainerAndTemplate_AndReportsVerified_WhenAutofillFindsCandidatesAndWriteIsConfirmed()
    {
        _containerAutofillService.AutofillAsync(Arg.Any<ContainerAutofillRequest>())
            .Returns(SuccessfulAutofillResult());
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(PersistedTemplate(new[] { TaskShiftId1, TaskShiftId2 }));

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("verified");
        await _shiftRepository.Received(1).AddWithSealedOrderHandling(
            Arg.Is<Shift>(s => s.ShiftType == ShiftType.IsContainer && s.Status == ShiftStatus.SealedOrder));
        await _mediator.Received(1).Send(
            Arg.Is<PostContainerTemplatesCommand>(c =>
                c.ContainerId == ContainerId
                && c.Resources.Single().ContainerTemplateItems.Count == 2
                && c.Resources.Single().ContainerTemplateItems.Any(i => i.ShiftId == TaskShiftId1)
                && c.Resources.Single().ContainerTemplateItems.Any(i => i.ShiftId == TaskShiftId2)),
            Arg.Any<CancellationToken>());
        await _mediator.Received(1).Send(
            Arg.Is<ReleaseContainerLockCommand>(c => c.LockId == LockId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsError_AndRollsBack_WhenTemplateWriteCannotBeConfirmed()
    {
        _containerAutofillService.AutofillAsync(Arg.Any<ContainerAutofillRequest>())
            .Returns(SuccessfulAutofillResult());
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(PersistedTemplate(new[] { TaskShiftId1 }));

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("could not be confirmed");
        await _mediator.Received(1).Send(
            Arg.Is<ReleaseContainerLockCommand>(c => c.LockId == LockId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsError_WithoutWritingAnything_WhenOrsKeyIsMissingAndTransportModeIsNotCar()
    {
        _settingsReader.GetSetting(Klacks.Api.Application.Constants.Settings.OPENROUTESERVICE_API_KEY)
            .Returns((Klacks.Api.Domain.Models.Settings.Settings?)null);

        foreach (var transportMode in new[] { "foot", "bicycle", "mix" })
        {
            var result = await _skill.ExecuteAsync(Ctx(), Params(transportMode));

            result.Success.ShouldBeFalse();
            result.Message.ShouldContain("OpenRouteService");
        }

        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
        await _unitOfWork.DidNotReceive().ExecuteInTransactionAsync(
            Arg.Any<Func<Task<BundleNearbyTimeRangeShiftsIntoContainerSkill.BundleOutcome>>>());
    }

    [Test]
    public async Task Succeeds_WithoutOrsKey_WhenTransportModeIsCar()
    {
        _settingsReader.GetSetting(Klacks.Api.Application.Constants.Settings.OPENROUTESERVICE_API_KEY)
            .Returns((Klacks.Api.Domain.Models.Settings.Settings?)null);
        _containerAutofillService.AutofillAsync(Arg.Any<ContainerAutofillRequest>())
            .Returns(SuccessfulAutofillResult());
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(PersistedTemplate(new[] { TaskShiftId1, TaskShiftId2 }));

        var result = await _skill.ExecuteAsync(Ctx(), Params("car"));

        result.Success.ShouldBeTrue();
        await _shiftRepository.Received(1).AddWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task ReturnsError_AndRollsBack_WhenNoCandidatesAreFound()
    {
        _containerAutofillService.AutofillAsync(Arg.Any<ContainerAutofillRequest>())
            .Returns(EmptyAutofillResult());

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("No matching mobile TimeRange services");
        await _mediator.DidNotReceive().Send(Arg.Any<PostContainerTemplatesCommand>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>());
    }
}
