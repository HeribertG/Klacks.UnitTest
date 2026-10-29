// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for BundleNearbyTimeRangeShiftsIntoContainerSkill: a container shift is created, autofilled
/// via IContainerAutofillService and its weekday template is written and re-read to confirm the write,
/// all inside one transaction that rolls back completely when nothing is found or the write cannot be
/// confirmed; the OpenRouteService key gate blocks non-car transport modes up front, before any write. Autofill picks a
/// restricted caller may not see are dropped before the write and never reported - neither in the items nor in the
/// stored route info or the stops, and the travel times run past the dropped stop; when none is left the answer is the
/// same as when nothing matched. A successful bundle returns the Route to the container page. The group filter accepts
/// a group name (exact name wins, ambiguous and unknown names are refused with the candidates), refuses a groupIds value
/// that is no GUID and a group without linked services, and the message names the filter or says that none was applied
/// and states plainly when the travel times are only straight-line estimates.
/// </summary>

using Klacks.Api.Application.Commands.ContainerTemplates;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.ContainerTemplates;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Services.RouteOptimization;
using Klacks.Api.Infrastructure.Mediator;
using System.Text.Json;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class BundleNearbyTimeRangeShiftsIntoContainerSkillTests
{
    private const int StorageWeekday = 3;
    private const int IsoWeekday = 3;
    private const string Location = "Bern";

    private IShiftRepository _shiftRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IGroupVisibilityGuard _groupVisibilityGuard = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private IMediator _mediator = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IUserService _userService = null!;
    private IContainerAutofillService _containerAutofillService = null!;
    private ISettingsReader _settingsReader = null!;
    private ISettingsEncryptionService _encryptionService = null!;
    private IShiftVisibilityGuard _shiftVisibilityGuard = null!;
    private IDefaultShiftMacroResolver _defaultShiftMacroResolver = null!;
    private BundleNearbyTimeRangeShiftsIntoContainerSkill _skill = null!;
    private PostContainerTemplatesCommand? _posted;

    private static readonly Guid DefaultMacroId = Guid.NewGuid();

    private static readonly Guid ContainerId = Guid.NewGuid();
    private static readonly Guid TaskShiftId1 = Guid.NewGuid();
    private static readonly Guid TaskShiftId2 = Guid.NewGuid();
    private static readonly Guid LockId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupVisibilityGuard = TestGroupWriteVisibility.UnrestrictedGroups();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _groupItemRepository.GetShiftIdsByGroupIds(Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([TaskShiftId1, TaskShiftId2]);
        _mediator = Substitute.For<IMediator>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _userService = Substitute.For<IUserService>();
        _containerAutofillService = Substitute.For<IContainerAutofillService>();
        _settingsReader = Substitute.For<ISettingsReader>();
        _encryptionService = Substitute.For<ISettingsEncryptionService>();
        _shiftVisibilityGuard = ShiftVisibilityGuards.AllowAll();
        _defaultShiftMacroResolver = Substitute.For<IDefaultShiftMacroResolver>();
        _defaultShiftMacroResolver.ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>()).Returns(DefaultMacroId);

        _skill = SkillWith(_shiftVisibilityGuard);

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
        _posted = null;
        _mediator.Send(Arg.Any<PostContainerTemplatesCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _posted = ci.Arg<PostContainerTemplatesCommand>();
                return new List<ContainerTemplateResource>();
            });
    }

    private BundleNearbyTimeRangeShiftsIntoContainerSkill SkillWith(IShiftVisibilityGuard shiftVisibilityGuard) => new(
        _shiftRepository,
        _groupRepository,
        _mediator,
        _unitOfWork,
        _userService,
        _containerAutofillService,
        _settingsReader,
        _encryptionService,
        GeoServicesPolicyTestFactory.Default(),
        _groupVisibilityGuard,
        _groupItemRepository,
        shiftVisibilityGuard,
        _defaultShiftMacroResolver,
        new ScheduleMapper());

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

    private static ContainerAutofillResult SuccessfulAutofillResult() =>
        ContainerAutofillRouteFixture.Route([TaskShiftId1, TaskShiftId2]);

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

    private List<ContainerTemplateResource> PersistedTemplate(IEnumerable<Guid> shiftIds)
    {
        var posted = _posted!.Resources.Single();
        var kept = shiftIds.ToHashSet();
        return
        [
            new ContainerTemplateResource
            {
                Id = Guid.NewGuid(),
                ContainerId = ContainerId,
                Weekday = StorageWeekday,
                IsHoliday = false,
                IsWeekdayAndHoliday = false,
                FromTime = new TimeOnly(8, 0),
                UntilTime = new TimeOnly(18, 0),
                RouteInfo = posted.RouteInfo,
                ContainerTemplateItems = posted.ContainerTemplateItems.Where(i => kept.Contains(i.ShiftId!.Value)).ToList()
            }
        ];
    }

    [Test]
    public async Task CreatesContainerAndTemplate_AndReportsVerified_WhenAutofillFindsCandidatesAndWriteIsConfirmed()
    {
        _containerAutofillService.AutofillAsync(Arg.Any<ContainerAutofillRequest>())
            .Returns(SuccessfulAutofillResult());
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => PersistedTemplate(new[] { TaskShiftId1, TaskShiftId2 }));

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
    public async Task CreatesContainer_WithDefaultShiftMacro_LikeAContainerCreatedInTheEditor()
    {
        _containerAutofillService.AutofillAsync(Arg.Any<ContainerAutofillRequest>())
            .Returns(SuccessfulAutofillResult());
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => PersistedTemplate(new[] { TaskShiftId1, TaskShiftId2 }));

        await _skill.ExecuteAsync(Ctx(), Params());

        await _shiftRepository.Received(1).AddWithSealedOrderHandling(
            Arg.Is<Shift>(s => s.ShiftType == ShiftType.IsContainer && s.MacroId == DefaultMacroId));
    }

    [Test]
    public async Task ReturnsError_AndRollsBack_WhenTemplateWriteCannotBeConfirmed()
    {
        _containerAutofillService.AutofillAsync(Arg.Any<ContainerAutofillRequest>())
            .Returns(SuccessfulAutofillResult());
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => PersistedTemplate(new[] { TaskShiftId1 }));

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
    public async Task ReturnsError_WithoutReadingTheKey_WhenOpenRouteServiceIsDisabledAndTransportModeIsNotCar()
    {
        var skill = new BundleNearbyTimeRangeShiftsIntoContainerSkill(
            _shiftRepository,
            _groupRepository,
            _mediator,
            _unitOfWork,
            _userService,
            _containerAutofillService,
            _settingsReader,
            _encryptionService,
            GeoServicesPolicyTestFactory.NoOutbound(),
            _groupVisibilityGuard,
            _groupItemRepository,
            _shiftVisibilityGuard,
            _defaultShiftMacroResolver,
            new ScheduleMapper());

        var result = await skill.ExecuteAsync(Ctx(), Params("bicycle"));

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain(Klacks.Api.Application.Constants.GeoServiceMessages.OpenRouteServiceDisabled);
        await _settingsReader.DidNotReceive().GetSetting(Klacks.Api.Application.Constants.Settings.OPENROUTESERVICE_API_KEY);
        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task Succeeds_WithoutOrsKey_WhenTransportModeIsCar()
    {
        _settingsReader.GetSetting(Klacks.Api.Application.Constants.Settings.OPENROUTESERVICE_API_KEY)
            .Returns((Klacks.Api.Domain.Models.Settings.Settings?)null);
        _containerAutofillService.AutofillAsync(Arg.Any<ContainerAutofillRequest>())
            .Returns(SuccessfulAutofillResult());
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => PersistedTemplate(new[] { TaskShiftId1, TaskShiftId2 }));

        var result = await _skill.ExecuteAsync(Ctx(), Params("car"));

        result.Success.ShouldBeTrue();
        await _shiftRepository.Received(1).AddWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task HiddenGroup_IsAnsweredLikeMissingGroup_AndNothingIsWritten()
    {
        var hiddenGroupId = Guid.NewGuid();
        _groupRepository.Get(hiddenGroupId).Returns(new Group { Id = hiddenGroupId, Name = "Hidden" });
        _groupVisibilityGuard.IsGroupVisibleAsync(hiddenGroupId, Arg.Any<CancellationToken>()).Returns(false);
        var parameters = Params();
        parameters["groupIds"] = hiddenGroupId.ToString();

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Group with ID {hiddenGroupId} not found.");
        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
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

    [Test]
    public async Task RestrictedCaller_HiddenAutofillPick_IsNeitherWrittenNorReported()
    {
        _containerAutofillService.AutofillAsync(Arg.Any<ContainerAutofillRequest>())
            .Returns(SuccessfulAutofillResult());
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => PersistedTemplate(new[] { TaskShiftId1 }));

        var result = await SkillWith(ShiftVisibilityGuards.Hiding(TaskShiftId2)).ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<PostContainerTemplatesCommand>(c =>
                c.Resources.Single().ContainerTemplateItems.Count == 1
                && c.Resources.Single().ContainerTemplateItems.Single().ShiftId == TaskShiftId1),
            Arg.Any<CancellationToken>());
        await _shiftRepository.DidNotReceive().Get(TaskShiftId2);
        result.Message.ShouldNotContain(TaskShiftId2.ToString());
        result.Message.ShouldContain("holds 1 of");
        JsonSerializer.Serialize(result.Data).ShouldNotContain(TaskShiftId2.ToString());

        var template = _posted!.Resources.Single();
        var routeInfoJson = JsonSerializer.Serialize(template.RouteInfo);
        routeInfoJson.ShouldNotContain(TaskShiftId2.ToString());
        routeInfoJson.ShouldNotContain(ContainerAutofillRouteFixture.VisitName(2));
        routeInfoJson.ShouldNotContain(ContainerAutofillRouteFixture.VisitAddress(2));
        template.RouteInfo!.TotalDistanceKm.ShouldBe(2.5 * ContainerAutofillRouteFixture.LegKm);
        template.ContainerTemplateItems.Single().TravelTimeAfter.ShouldBe(new TimeOnly(0, 15));
        JsonSerializer.Serialize(result.Data).ShouldNotContain(ContainerAutofillRouteFixture.VisitName(2));
    }

    [Test]
    public async Task RestrictedCaller_AllAutofillPicksHidden_IsAnsweredLikeNoMatch_AndNothingIsWritten()
    {
        _containerAutofillService.AutofillAsync(Arg.Any<ContainerAutofillRequest>())
            .Returns(EmptyAutofillResult());
        var noMatch = await _skill.ExecuteAsync(Ctx(), Params());
        _mediator.ClearReceivedCalls();
        _containerAutofillService.AutofillAsync(Arg.Any<ContainerAutofillRequest>())
            .Returns(SuccessfulAutofillResult());

        var result = await SkillWith(ShiftVisibilityGuards.HideAll()).ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(noMatch.Message);
        result.Message.ShouldNotContain(TaskShiftId1.ToString());
        result.Message.ShouldNotContain(TaskShiftId2.ToString());
        await _mediator.DidNotReceive().Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<PostContainerTemplatesCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnrestrictedCaller_KeepsEveryAutofillPick()
    {
        _containerAutofillService.AutofillAsync(Arg.Any<ContainerAutofillRequest>())
            .Returns(SuccessfulAutofillResult());
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => PersistedTemplate(new[] { TaskShiftId1, TaskShiftId2 }));

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        await _shiftVisibilityGuard.Received(1).FilterVisibleAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2), Arg.Any<CancellationToken>());
        result.Message.ShouldContain("holds 2 of");
    }

    [Test]
    public async Task Success_ReturnsTheRouteToTheContainerPage_AndTheStopsInRouteOrder()
    {
        _containerAutofillService.AutofillAsync(Arg.Any<ContainerAutofillRequest>())
            .Returns(SuccessfulAutofillResult());
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => PersistedTemplate(new[] { TaskShiftId1, TaskShiftId2 }));

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
        data.RootElement.GetProperty("Route").GetString().ShouldBe($"/workplace/container-template/{ContainerId}");
        data.RootElement.GetProperty("Stops").EnumerateArray()
            .Select(stop => stop.GetProperty("ShiftId").GetGuid())
            .ShouldBe([TaskShiftId1, TaskShiftId2]);
        result.Message.ShouldNotContain("Klacksy");
    }

    private Group StubGroup(string name)
    {
        var group = new Group { Id = Guid.NewGuid(), Name = name };
        _groupRepository.Get(group.Id).Returns(group);
        return group;
    }

    private void StubSuccessfulBundle(ContainerAutofillResult? autofillResult = null)
    {
        _containerAutofillService.AutofillAsync(Arg.Any<ContainerAutofillRequest>())
            .Returns(autofillResult ?? SuccessfulAutofillResult());
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => PersistedTemplate(new[] { TaskShiftId1, TaskShiftId2 }));
    }

    [Test]
    public async Task GroupName_ExactNameWins_AndBecomesTheContainersGroupFilter()
    {
        var winterthur = StubGroup("Spitex Winterthur");
        var winterthurSeen = StubGroup("Spitex Winterthur Seen");
        _groupRepository.List().Returns([winterthur, winterthurSeen]);
        StubSuccessfulBundle();
        var parameters = Params();
        parameters["groupName"] = "Spitex Winterthur";

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("Only services of the group 'Spitex Winterthur' were considered.");
        await _shiftRepository.Received(1).AddWithSealedOrderHandling(
            Arg.Is<Shift>(s => s.GroupItems.Count == 1 && s.GroupItems.Single().GroupId == winterthur.Id));
        await _groupItemRepository.Received(1).GetShiftIdsByGroupIds(
            Arg.Is<List<Guid>>(ids => ids.SequenceEqual(new[] { winterthur.Id })), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GroupName_Ambiguous_ListsTheCandidates_AndNothingIsWritten()
    {
        var first = StubGroup("Spitex Nord");
        var second = StubGroup("Spitex Nordost");
        _groupRepository.List().Returns([first, second]);
        var parameters = Params();
        parameters["groupName"] = "Spitex No";

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("ambiguous");
        result.Message.ShouldContain("Spitex Nord");
        result.Message.ShouldContain("Spitex Nordost");
        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task GroupName_Unknown_ListsTheAvailableGroups_AndNothingIsWritten()
    {
        var winterthur = StubGroup("Spitex Winterthur");
        _groupRepository.List().Returns([winterthur]);
        var parameters = Params();
        parameters["groupName"] = "Pflegeheim Basel";

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
        result.Message.ShouldContain("Spitex Winterthur");
        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task GroupName_OfAGroupHiddenFromTheCaller_IsNotFound()
    {
        var visible = StubGroup("Spitex Zürich");
        var hidden = StubGroup("Spitex Winterthur");
        _groupRepository.List().Returns([visible, hidden]);
        _groupVisibilityGuard.GetScopeAsync(Arg.Any<CancellationToken>())
            .Returns(GroupVisibilityScope.Restricted([visible.Id], [visible.Id]));
        var parameters = Params();
        parameters["groupName"] = "Spitex Winterthur";

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
        result.Message.ShouldNotContain(hidden.Id.ToString());
        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task GroupIds_WithANameInsteadOfAGuid_IsRefused_InsteadOfSilentlyDroppingTheFilter()
    {
        var parameters = Params();
        parameters["groupIds"] = "Spitex Winterthur";

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("groupName");
        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task GroupWithoutLinkedServices_IsRefused_InsteadOfFallingBackToAllGroups()
    {
        var empty = StubGroup("Spitex Leer");
        _groupRepository.List().Returns([empty]);
        _groupItemRepository.GetShiftIdsByGroupIds(Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());
        var parameters = Params();
        parameters["groupName"] = "Spitex Leer";

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("no services linked");
        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task GroupNameAndGroupIdsOfTheSameGroup_AreLinkedOnce()
    {
        var winterthur = StubGroup("Spitex Winterthur");
        _groupRepository.List().Returns([winterthur]);
        StubSuccessfulBundle();
        var parameters = Params();
        parameters["groupName"] = "Spitex Winterthur";
        parameters["groupIds"] = winterthur.Id.ToString();

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue(result.Message);
        await _shiftRepository.Received(1).AddWithSealedOrderHandling(Arg.Is<Shift>(s => s.GroupItems.Count == 1));
    }

    [Test]
    public async Task WithoutGroup_TheMessageSaysThatNoGroupFilterWasApplied()
    {
        StubSuccessfulBundle();

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("No group filter was applied");
        await _groupItemRepository.DidNotReceive().GetShiftIdsByGroupIds(Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EstimatedTravelTimes_AreReportedPlainly_WithTheGroupNameHint_WhenNoGroupWasGiven()
    {
        StubSuccessfulBundle(SuccessfulAutofillResult() with { IsEstimated = true });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("straight-line estimates");
        result.Message.ShouldContain("size limit");
        result.Message.ShouldContain("only estimates");
        result.Message.ShouldContain("With groupName");
    }

    [Test]
    public async Task ExactTravelTimes_HaveNoEstimationNote()
    {
        StubSuccessfulBundle();

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldNotContain("straight-line estimates");
    }
}
