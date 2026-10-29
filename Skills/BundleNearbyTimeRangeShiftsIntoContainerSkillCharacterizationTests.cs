// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for BundleNearbyTimeRangeShiftsIntoContainerSkill.ExecuteAsync, written before its long
/// body was split into private steps: every refusal text in its precedence order, the success text and serialized
/// result data, the container shift, autofill request, lock command and template that are handed on, and the
/// order of every collaborator call before and inside the transaction (including the lock release on failure).
/// Updated deliberately for Spitex P2 (2026-10-29): the autofill result now carries a real route (start base, visits,
/// end base; ContainerAutofillRouteFixture), the template items carry the placed visit windows and travel times, the
/// template carries the RouteInfo, the confirmation re-read echoes what was posted, and the result reports the stops
/// and the Route to the container page with a message that names visit and travel times.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands.ContainerTemplates;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.ContainerTemplates;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Services.RouteOptimization;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;
using BundleOutcome = Klacks.Api.Application.Skills.BundleNearbyTimeRangeShiftsIntoContainerSkill.BundleOutcome;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class BundleNearbyTimeRangeShiftsIntoContainerSkillCharacterizationTests
{
    private const string Location = "Bern";
    private const string ContainerName = "Mobile Dienste Bern";

    private static readonly Guid ContainerId = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid TaskShiftId1 = Guid.Parse("b0000000-0000-0000-0000-000000000002");
    private static readonly Guid TaskShiftId2 = Guid.Parse("b0000000-0000-0000-0000-000000000003");
    private static readonly Guid LockId = Guid.Parse("b0000000-0000-0000-0000-000000000004");
    private static readonly Guid DefaultMacroId = Guid.Parse("b0000000-0000-0000-0000-000000000005");
    private static readonly Guid GroupId = Guid.Parse("b0000000-0000-0000-0000-000000000006");

    private IShiftRepository _shiftRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IGroupVisibilityGuard _groupVisibilityGuard = null!;
    private IMediator _mediator = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IUserService _userService = null!;
    private IContainerAutofillService _containerAutofillService = null!;
    private ISettingsReader _settingsReader = null!;
    private ISettingsEncryptionService _encryptionService = null!;
    private IShiftVisibilityGuard _shiftVisibilityGuard = null!;
    private IDefaultShiftMacroResolver _defaultShiftMacroResolver = null!;
    private List<string> _calls = null!;
    private Shift? _addedShift;
    private ContainerAutofillRequest? _autofillRequest;
    private AcquireContainerLockCommand? _lockCommand;
    private PostContainerTemplatesCommand? _postCommand;
    private int _persistedWeekday;
    private List<Guid> _persistedShiftIds = null!;
    private Action<ContainerTemplateResource>? _storedChange;

    [SetUp]
    public void Setup()
    {
        _calls = [];
        _addedShift = null;
        _autofillRequest = null;
        _lockCommand = null;
        _postCommand = null;
        _persistedWeekday = 3;
        _persistedShiftIds = [TaskShiftId1, TaskShiftId2];
        _storedChange = null;

        _shiftRepository = Substitute.For<IShiftRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupVisibilityGuard = Substitute.For<IGroupVisibilityGuard>();
        _mediator = Substitute.For<IMediator>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _userService = Substitute.For<IUserService>();
        _containerAutofillService = Substitute.For<IContainerAutofillService>();
        _settingsReader = Substitute.For<ISettingsReader>();
        _encryptionService = Substitute.For<ISettingsEncryptionService>();
        _shiftVisibilityGuard = Substitute.For<IShiftVisibilityGuard>();
        _defaultShiftMacroResolver = Substitute.For<IDefaultShiftMacroResolver>();

        _settingsReader.GetSetting(Arg.Any<string>())
            .Returns(_ =>
            {
                _calls.Add("ors-key");
                return new Klacks.Api.Domain.Models.Settings.Settings { Type = "encrypted", Value = "cipher" };
            });
        _encryptionService.ProcessForReading(Arg.Any<string>(), Arg.Any<string>()).Returns("ors-key-value");
        _groupRepository.Get(Arg.Any<Guid>())
            .Returns(ci => { _calls.Add("group-get"); return new Group { Id = ci.Arg<Guid>(), Name = "Bern" }; });
        _groupVisibilityGuard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("group-visible"); return true; });
        _defaultShiftMacroResolver.ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("default-macro"); return (Guid?)DefaultMacroId; });
        _userService.GetInstanceId().Returns(_ => { _calls.Add("instance-id"); return "instance-1"; });
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<BundleOutcome>>>())
            .Returns(ci => { _calls.Add("transaction"); return ci.Arg<Func<Task<BundleOutcome>>>()(); });
        _shiftRepository.AddWithSealedOrderHandling(Arg.Any<Shift>())
            .Returns(ci =>
            {
                _calls.Add("add");
                _addedShift = ci.Arg<Shift>();
                return new Shift { Id = ContainerId, Name = _addedShift.Name, OriginalId = _addedShift.Id };
            });
        _unitOfWork.CompleteAsync().Returns(_ => { _calls.Add("complete"); return Task.CompletedTask; });
        _containerAutofillService.AutofillAsync(Arg.Any<ContainerAutofillRequest>())
            .Returns(ci =>
            {
                _calls.Add("autofill");
                _autofillRequest = ci.Arg<ContainerAutofillRequest>();
                return AutofillResult([TaskShiftId1, TaskShiftId2]);
            });
        _shiftVisibilityGuard.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _calls.Add("filter");
                return (IReadOnlySet<Guid>)ci.Arg<IReadOnlyCollection<Guid>>().ToHashSet();
            });
        _mediator.Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _calls.Add("acquire");
                _lockCommand = ci.Arg<AcquireContainerLockCommand>();
                return new ContainerLockResource { Id = LockId, Acquired = true };
            });
        _shiftRepository.Get(TaskShiftId1)
            .Returns(_ => { _calls.Add("task-get:1"); return TaskShift(TaskShiftId1, 1); });
        _shiftRepository.Get(TaskShiftId2)
            .Returns(_ => { _calls.Add("task-get:2"); return TaskShift(TaskShiftId2, 2); });
        _mediator.Send(Arg.Any<PostContainerTemplatesCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _calls.Add("post");
                _postCommand = ci.Arg<PostContainerTemplatesCommand>();
                return new List<ContainerTemplateResource>();
            });
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _calls.Add("confirm-read");
                return EchoPosted(_persistedWeekday, isHoliday: false);
            });
        _mediator.Send(Arg.Any<ReleaseContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("release"); return true; });
    }

    private BundleNearbyTimeRangeShiftsIntoContainerSkill Skill(bool openRouteServiceEnabled = true) => new(
        _shiftRepository,
        _groupRepository,
        _mediator,
        _unitOfWork,
        _userService,
        _containerAutofillService,
        _settingsReader,
        _encryptionService,
        openRouteServiceEnabled ? GeoServicesPolicyTestFactory.Default() : GeoServicesPolicyTestFactory.NoOutbound(),
        _groupVisibilityGuard,
        _shiftVisibilityGuard,
        _defaultShiftMacroResolver,
        new ScheduleMapper());

    private List<ContainerTemplateResource> EchoPosted(int weekday, bool isHoliday)
    {
        var posted = _postCommand!.Resources.Single();
        var stored = new ContainerTemplateResource
        {
            Id = Guid.NewGuid(),
            Weekday = weekday,
            IsHoliday = isHoliday,
            RouteInfo = posted.RouteInfo,
            ContainerTemplateItems = posted.ContainerTemplateItems
                .Where(i => _persistedShiftIds.Contains(i.ShiftId!.Value))
                .Select(i => new ContainerTemplateItemResource
                {
                    ShiftId = i.ShiftId,
                    TimeRangeStartItem = i.TimeRangeStartItem,
                    TimeRangeEndItem = i.TimeRangeEndItem,
                    TravelTimeBefore = i.TravelTimeBefore,
                    TravelTimeAfter = i.TravelTimeAfter
                }).ToList()
        };
        _storedChange?.Invoke(stored);
        return [stored];
    }

    private static Shift TaskShift(Guid id, int hour) => new()
    {
        Id = id,
        WorkTime = 0.75m,
        ShiftType = ShiftType.IsTask,
        IsTimeRange = true,
        StartShift = new TimeOnly(7 + hour, 0),
        EndShift = new TimeOnly(8 + hour, 0),
        BriefingTime = new TimeOnly(0, hour),
        DebriefingTime = new TimeOnly(0, hour + 1),
        TravelTimeBefore = new TimeOnly(0, hour + 2),
        TravelTimeAfter = new TimeOnly(0, hour + 3)
    };

    private static ContainerAutofillResult AutofillResult(List<Guid> selected) =>
        ContainerAutofillRouteFixture.Route(selected);

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static Dictionary<string, object> Params(params (string Key, object Value)[] overrides)
    {
        var parameters = new Dictionary<string, object>
        {
            ["location"] = Location,
            ["weekday"] = 3,
            ["fromTime"] = "08:00",
            ["untilTime"] = "18:00",
            ["fromDate"] = "2026-06-01"
        };
        foreach (var (key, value) in overrides)
        {
            parameters[key] = value;
        }

        return parameters;
    }

    private static readonly string[] FullSequence =
    [
        "default-macro", "instance-id", "transaction", "add", "complete", "autofill", "filter", "acquire",
        "task-get:1", "task-get:2", "post", "confirm-read", "release"
    ];

    [Test]
    [SetCulture("en-US")]
    public async Task Success_ExactMessageDataAndCallOrder()
    {
        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            $"Container '{ContainerName}' created for weekday 3 (transportMode=car). Its " +
            "template holds 2 of 3 nearby TimeRange " +
            "service(s) around 'Bern' in route order, each with a planned visit time and the travel time before " +
            "it, the last one also with the travel time back to the base, plus the route info; all of it is confirmed " +
            "in the database (verified). " +
            "Route: 7.5 km, 02:00 travel+work time, 08:00 of the time budget remaining. " +
            "Candidates were not limited by a hard distance radius — only by the time budget and route " +
            "optimization. The chat opens the container page, where the user can check the tour on the map and change " +
            "it. To fill another weekday, call this skill again with a different weekday.");
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"ContainerId\":\"" + ContainerId + "\",\"ContainerName\":\"" + ContainerName + "\",\"Weekday\":3," +
            "\"TransportMode\":\"car\",\"CandidatesFound\":3,\"ServicesBundled\":2,\"TotalDistanceKm\":7.5," +
            "\"EstimatedTravelAndWorkTime\":\"02:00\",\"RemainingBudget\":\"08:00\"," +
            "\"BundledShiftIds\":[\"" + TaskShiftId1 + "\",\"" + TaskShiftId2 + "\"]," +
            "\"Stops\":[" +
            "{\"Order\":1,\"ShiftId\":\"" + TaskShiftId1 + "\",\"Name\":\"Client 1\",\"Address\":\"Street 1, 3000 Bern\"," +
            "\"VisitStart\":\"08:11\",\"VisitEnd\":\"08:56\",\"TravelBefore\":\"00:10\"}," +
            "{\"Order\":2,\"ShiftId\":\"" + TaskShiftId2 + "\",\"Name\":\"Client 2\",\"Address\":\"Street 2, 3000 Bern\"," +
            "\"VisitStart\":\"09:10\",\"VisitEnd\":\"09:55\",\"TravelBefore\":\"00:10\"}]," +
            "\"TravelTimesEstimated\":false," +
            "\"Route\":\"/workplace/container-template/" + ContainerId + "\",\"Verified\":true}");
        _calls.ShouldBe(FullSequence);
    }

    [Test]
    [SetCulture("de-DE")]
    public async Task Success_FormatsTheDistanceWithTheCurrentCulture()
    {
        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Message!.ShouldContain("Route: 7,5 km, 02:00 travel+work time, 08:00 of the time budget");
    }

    [Test]
    public async Task Success_HandsOnTheExactShiftRequestLockAndTemplate()
    {
        await Skill().ExecuteAsync(Ctx(), Params(("groupIds", $"{GroupId}, junk"), ("untilDate", "2026-12-31")));

        var shift = _addedShift!;
        shift.Id.ShouldNotBe(Guid.Empty);
        shift.Name.ShouldBe(ContainerName);
        shift.Abbreviation.ShouldBe("MDB");
        shift.Description.ShouldBe(string.Empty);
        shift.ClientId.ShouldBeNull();
        shift.MacroId.ShouldBe(DefaultMacroId);
        shift.Status.ShouldBe(ShiftStatus.SealedOrder);
        shift.ShiftType.ShouldBe(ShiftType.IsContainer);
        shift.IsTimeRange.ShouldBeFalse();
        shift.StartShift.ShouldBe(new TimeOnly(8, 0));
        shift.EndShift.ShouldBe(new TimeOnly(18, 0));
        shift.FromDate.ShouldBe(new DateOnly(2026, 6, 1));
        shift.UntilDate.ShouldBe(new DateOnly(2026, 12, 31));
        shift.WorkTime.ShouldBe(10m);
        shift.SumEmployees.ShouldBe(1);
        shift.Quantity.ShouldBe(1);
        new[] { shift.IsMonday, shift.IsTuesday, shift.IsWednesday, shift.IsThursday, shift.IsFriday, shift.IsSaturday, shift.IsSunday }
            .ShouldBe([false, false, true, false, false, false, false]);
        shift.GroupItems.Single().GroupId.ShouldBe(GroupId);
        shift.GroupItems.Single().ValidFrom.ShouldBe(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        shift.CurrentUserCreated.ShouldBe("tester");
        _calls.Take(3).ShouldBe(["group-get", "group-visible", "default-macro"]);

        var request = _autofillRequest!;
        request.ContainerId.ShouldBe(ContainerId);
        request.Weekday.ShouldBe(3);
        request.IsHoliday.ShouldBeFalse();
        request.StartBase.ShouldBe(Location);
        request.EndBase.ShouldBe(Location);
        request.FromTime.ShouldBe(new TimeOnly(8, 0));
        request.UntilTime.ShouldBe(new TimeOnly(18, 0));
        request.TransportMode.ShouldBe(ContainerTransportMode.ByCar);
        request.TimeRangeTolerance.ShouldBe(0.5);
        request.TimeBlocks.ShouldBeNull();
        request.AdditionalAvailableWorkIds.ShouldBeNull();

        _lockCommand.ShouldBe(new AcquireContainerLockCommand("ContainerTemplate", ContainerId, "instance-1"));

        _postCommand!.ContainerId.ShouldBe(ContainerId);
        var template = _postCommand.Resources.Single();
        template.Id.ShouldBe(Guid.Empty);
        template.ContainerId.ShouldBe(ContainerId);
        template.FromTime.ShouldBe(new TimeOnly(8, 0));
        template.UntilTime.ShouldBe(new TimeOnly(18, 0));
        template.Weekday.ShouldBe(3);
        template.IsHoliday.ShouldBeFalse();
        template.IsWeekdayAndHoliday.ShouldBeFalse();
        template.StartBase.ShouldBe(Location);
        template.EndBase.ShouldBe(Location);
        template.TransportMode.ShouldBe(ContainerTransportMode.ByCar);
        template.ContainerTemplateItems.Count.ShouldBe(2);
        var item = template.ContainerTemplateItems[1];
        item.Id.ShouldBe(Guid.Empty);
        item.ShiftId.ShouldBe(TaskShiftId2);
        item.AbsenceId.ShouldBeNull();
        item.StartItem.ShouldBe(new TimeOnly(9, 0));
        item.EndItem.ShouldBe(new TimeOnly(10, 0));
        item.BriefingTime.ShouldBe(new TimeOnly(0, 2));
        item.DebriefingTime.ShouldBe(new TimeOnly(0, 3));
        item.TravelTimeBefore.ShouldBe(new TimeOnly(0, 10));
        item.TravelTimeAfter.ShouldBe(new TimeOnly(0, 10));
        item.TimeRangeStartItem.ShouldBe(new TimeOnly(9, 10));
        item.TimeRangeEndItem.ShouldBe(new TimeOnly(9, 55));
        item.TransportMode.ShouldBe(TransportMode.ByCar);
        var first = template.ContainerTemplateItems[0];
        first.ShiftId.ShouldBe(TaskShiftId1);
        first.TimeRangeStartItem.ShouldBe(new TimeOnly(8, 11));
        first.TimeRangeEndItem.ShouldBe(new TimeOnly(8, 56));
        first.TravelTimeBefore.ShouldBe(new TimeOnly(0, 10));
        first.TravelTimeAfter.ShouldBe(new TimeOnly(0, 4));
        var routeInfo = template.RouteInfo!;
        routeInfo.StartBase.ShouldBe(Location);
        routeInfo.EndBase.ShouldBe(Location);
        routeInfo.TotalDistanceKm.ShouldBe(7.5);
        routeInfo.OptimizedRoute.Select(l => l.ShiftId)
            .ShouldBe([Guid.Empty.ToString(), TaskShiftId1.ToString(), TaskShiftId2.ToString(), Guid.Empty.ToString()]);
    }

    [Test]
    public async Task IsoSunday_CustomName_Tolerance_NonCarMode_AndMissingInstanceId()
    {
        _persistedWeekday = 0;
        _userService.GetInstanceId().Returns((string?)null);
        _calls.Clear();

        var result = await Skill().ExecuteAsync(Ctx(), Params(
            ("weekday", 7),
            ("name", "Spitex Nord"),
            ("timeRangeTolerance", 0.8),
            ("transportMode", "Bicycle")));

        result.Success.ShouldBeTrue(result.Message);
        result.Message!.ShouldStartWith(
            "Container 'Spitex Nord' created for weekday 7 (transportMode=Bicycle). Its template holds 2 of 3");
        JsonSerializer.Serialize(result.Data).ShouldContain("\"Weekday\":7,\"TransportMode\":\"Bicycle\"");
        _addedShift!.Abbreviation.ShouldBe("SN");
        new[] { _addedShift.IsMonday, _addedShift.IsTuesday, _addedShift.IsWednesday, _addedShift.IsThursday, _addedShift.IsFriday, _addedShift.IsSaturday, _addedShift.IsSunday }
            .ShouldBe([false, false, false, false, false, false, true]);
        _autofillRequest!.Weekday.ShouldBe(0);
        _autofillRequest.TimeRangeTolerance.ShouldBe(0.8);
        _autofillRequest.TransportMode.ShouldBe(ContainerTransportMode.ByBicycle);
        _postCommand!.Resources.Single().Weekday.ShouldBe(0);
        _postCommand.Resources.Single().TransportMode.ShouldBe(ContainerTransportMode.ByBicycle);
        _lockCommand!.InstanceId.ShouldBe(string.Empty);
        _calls.First().ShouldBe("ors-key");
    }

    [Test]
    public async Task InputErrors_HaveTheirExactTexts_InPrecedenceOrder_BeforeAnyCall()
    {
        var skill = Skill();
        (await skill.ExecuteAsync(Ctx(), Params(("weekday", 0), ("fromTime", "x"))))
            .Message.ShouldBe("weekday must be between 1 (Monday) and 7 (Sunday).");
        (await skill.ExecuteAsync(Ctx(), Params(("fromTime", "x"), ("untilTime", "y"))))
            .Message.ShouldBe("Invalid fromTime format: x. Expected HH:mm");
        (await skill.ExecuteAsync(Ctx(), Params(("untilTime", "y"))))
            .Message.ShouldBe("Invalid untilTime format: y. Expected HH:mm");
        (await skill.ExecuteAsync(Ctx(), Params(("untilTime", "8:00"), ("fromDate", "x"))))
            .Message.ShouldBe("untilTime (8:00) must be after fromTime (08:00).");
        (await skill.ExecuteAsync(Ctx(), Params(("fromDate", "x"), ("untilDate", "y"))))
            .Message.ShouldBe("Invalid fromDate format: x. Expected YYYY-MM-DD (e.g. 2026-06-01).");
        (await skill.ExecuteAsync(Ctx(), Params(("untilDate", "y"), ("transportMode", "boat"))))
            .Message.ShouldBe("Invalid untilDate format: y. Expected YYYY-MM-DD.");
        (await skill.ExecuteAsync(Ctx(), Params(("untilDate", "2026-05-31"), ("transportMode", "boat"))))
            .Message.ShouldBe("untilDate (2026-05-31) must not be before fromDate (2026-06-01).");
        (await skill.ExecuteAsync(Ctx(), Params(("transportMode", "boat"), ("groupIds", GroupId.ToString()))))
            .Message.ShouldBe("Invalid transportMode 'boat'. Expected one of: car, bicycle, foot, mix.");
        _calls.ShouldBeEmpty();
    }

    [Test]
    public async Task BlankUntilDate_IsTreatedAsOpenEnded()
    {
        var result = await Skill().ExecuteAsync(Ctx(), Params(("untilDate", " ")));

        result.Success.ShouldBeTrue(result.Message);
        _addedShift!.UntilDate.ShouldBeNull();
    }

    [Test]
    public async Task DisabledOpenRouteService_IsRefusedBeforeReadingTheKeyOrTheGroups()
    {
        var result = await Skill(openRouteServiceEnabled: false)
            .ExecuteAsync(Ctx(), Params(("transportMode", "foot"), ("groupIds", GroupId.ToString())));

        result.Message.ShouldBe(
            $"Cannot use transportMode 'foot' — {GeoServiceMessages.OpenRouteServiceDisabled}, so only " +
            "transportMode=car is available. Call this skill again with transportMode=car.");
        _calls.ShouldBeEmpty();
    }

    [Test]
    public async Task MissingOpenRouteServiceKey_IsRefusedBeforeTheGroups()
    {
        _settingsReader.GetSetting(Arg.Any<string>())
            .Returns(_ => { _calls.Add("ors-key"); return (Klacks.Api.Domain.Models.Settings.Settings?)null; });

        var result = await Skill().ExecuteAsync(Ctx(), Params(("transportMode", "mix"), ("groupIds", GroupId.ToString())));

        result.Message.ShouldBe(
            "Cannot use transportMode 'mix' — no OpenRouteService API key is configured, so only " +
            "transportMode=car (driving times via OSRM/estimation) is available. Configure the OpenRouteService " +
            "API key in Settings first, or call this skill again with transportMode=car.");
        _calls.ShouldBe(["ors-key"]);
    }

    [Test]
    public async Task UnknownGroup_IsRefused_WithoutAskingTheGuard()
    {
        _groupRepository.Get(Arg.Any<Guid>()).Returns(_ => { _calls.Add("group-get"); return (Group?)null; });

        var result = await Skill().ExecuteAsync(Ctx(), Params(("groupIds", GroupId.ToString())));

        result.Message.ShouldBe($"Group with ID {GroupId} not found.");
        _calls.ShouldBe(["group-get"]);
    }

    [Test]
    public async Task NoCandidates_ExactText_NoFilterNoLock()
    {
        _containerAutofillService.AutofillAsync(Arg.Any<ContainerAutofillRequest>())
            .Returns(_ => { _calls.Add("autofill"); return AutofillResult([]); });

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "No matching mobile TimeRange services were found around 'Bern' within the given time budget — no container was created.");
        _calls.ShouldBe(["default-macro", "instance-id", "transaction", "add", "complete", "autofill"]);
    }

    [Test]
    public async Task LockNotAcquired_ExactText_NothingPostedNothingReleased()
    {
        _mediator.Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("acquire"); return new ContainerLockResource { Id = LockId, Acquired = false }; });

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Message.ShouldBe(
            "The newly created container could not be locked for writing its template — the operation was rolled back.");
        _calls.ShouldBe(["default-macro", "instance-id", "transaction", "add", "complete", "autofill", "filter", "acquire"]);
    }

    [Test]
    public async Task UnreadableTaskShift_ExactText_StopsTheLoop_AndReleasesTheLock()
    {
        _shiftRepository.Get(TaskShiftId1).Returns(_ => { _calls.Add("task-get:1"); return (Shift?)null; });
        _calls.Clear();

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Message.ShouldBe(
            $"Selected task shift {TaskShiftId1} could not be re-read while building the container template.");
        _calls.ShouldBe(["default-macro", "instance-id", "transaction", "add", "complete", "autofill", "filter", "acquire",
            "task-get:1", "release"]);
    }

    [Test]
    public async Task UnconfirmedWrite_ExactText_AndReleasesTheLock()
    {
        _persistedShiftIds = [TaskShiftId1];

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Message.ShouldBe(
            "Database verification failed: the container template for weekday 3 with its visit times, travel times " +
            "and route info could not be confirmed in the database after the write — the change was rolled back.");
        _calls.ShouldBe(FullSequence);
    }

    [Test]
    public async Task StoredWithoutRouteInfo_IsNotConfirmed()
    {
        _storedChange = stored => stored.RouteInfo = null;

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldStartWith("Database verification failed");
        _calls.ShouldBe(FullSequence);
    }

    [Test]
    public async Task StoredWithAnotherVisitTime_IsNotConfirmed()
    {
        _storedChange = stored => stored.ContainerTemplateItems[1].TimeRangeStartItem = new TimeOnly(9, 11);

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldStartWith("Database verification failed");
    }

    [Test]
    public async Task StoredWithAnotherTravelTime_IsNotConfirmed()
    {
        _storedChange = stored => stored.ContainerTemplateItems[0].TravelTimeAfter = new TimeOnly(0, 59);

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldStartWith("Database verification failed");
    }

    [Test]
    public async Task SelectedVisitMissingFromTheRoute_ExactText_NothingPosted_AndReleasesTheLock()
    {
        _containerAutofillService.AutofillAsync(Arg.Any<ContainerAutofillRequest>())
            .Returns(_ =>
            {
                _calls.Add("autofill");
                return ContainerAutofillRouteFixture.Route([TaskShiftId1]) with
                {
                    SelectedShiftIds = [TaskShiftId1, TaskShiftId2],
                    SelectedShiftCount = 2
                };
            });

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Message.ShouldBe(
            "Only 1 of 2 selected services could be placed on the route, so no visit times could be planned — no " +
            "container was created.");
        _calls.ShouldBe(["default-macro", "instance-id", "transaction", "add", "complete", "autofill", "filter", "acquire",
            "task-get:1", "task-get:2", "release"]);
    }

    [Test]
    [SetCulture("en-US")]
    public async Task EstimatedTravelTimes_AreNamedInTheMessageAndData()
    {
        _containerAutofillService.AutofillAsync(Arg.Any<ContainerAutofillRequest>())
            .Returns(_ => ContainerAutofillRouteFixture.Route([TaskShiftId1, TaskShiftId2], isEstimated: true));

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message!.ShouldContain(
            "08:00 of the time budget remaining. The travel times are straight-line estimates, because the routing " +
            "service could not be reached. Candidates");
        JsonSerializer.Serialize(result.Data).ShouldContain("\"TravelTimesEstimated\":true");
    }

    [Test]
    public async Task HolidayOnlyTemplateOfTheWeekday_StillConfirmsTheWrite()
    {
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => EchoPosted(3, isHoliday: true));

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
    }

    [Test]
    public async Task ThrowingRelease_IsSwallowed()
    {
        _mediator.Send(Arg.Any<ReleaseContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("release failed"));

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
    }

    [Test]
    public async Task NonVerificationFailureInsideTheTransaction_Propagates()
    {
        _containerAutofillService.AutofillAsync(Arg.Any<ContainerAutofillRequest>())
            .Returns<ContainerAutofillResult>(_ => throw new InvalidOperationException("routing down"));

        (await Should.ThrowAsync<InvalidOperationException>(() => Skill().ExecuteAsync(Ctx(), Params())))
            .Message.ShouldBe("routing down");
    }

    [Test]
    public async Task MissingRequiredParameters_ThrowInDeclarationOrder()
    {
        (await Should.ThrowAsync<ArgumentException>(() => Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>())))
            .Message.ShouldBe("Required parameter 'location' is missing");
        var noFromDate = Params();
        noFromDate.Remove("fromDate");
        (await Should.ThrowAsync<ArgumentException>(() => Skill().ExecuteAsync(Ctx(), noFromDate)))
            .Message.ShouldBe("Required parameter 'fromDate' is missing");
        _calls.ShouldBeEmpty();
    }
}
