// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// build_weekly_tours with the real planner, builder, clusterer and verifier over fakes for the database, the autofill
/// route and the geocoding: customers of two regions end up in two tours with ALL their weekday services in the same
/// tour; every autofill is restricted to the services of one tour on one weekday; the containers are active only on weekdays
/// that have services; a service that does not fit a day is reported per tour and weekday and never dropped; a tour that
/// places nothing, a template that does not hold what was placed, a service in the wrong tour and a service that still runs
/// on several weekdays fail the run (error, nothing reported as created).
/// </summary>

using Klacks.Api.Application.Commands.ContainerTemplates;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.ContainerTemplates;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.RouteOptimization;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class BuildWeeklyToursSkillTests
{
    private const string GroupName = "Spitex Winterthur";
    private const double BernLat = 46.95;
    private const double BernLon = 7.45;
    private const double WinterthurLat = 47.50;
    private const double WinterthurLon = 8.72;

    private static readonly Guid GroupId = Guid.NewGuid();

    private readonly List<Shift> _tasks = [];
    private readonly Dictionary<Guid, (double Lat, double Lon)> _locationByClient = [];
    private readonly Dictionary<Guid, List<ContainerTemplateResource>> _storedTemplates = [];
    private readonly List<Shift> _createdContainers = [];
    private readonly List<ContainerAutofillRequest> _autofillRequests = [];
    private readonly Dictionary<Guid, string> _containerNames = [];

    private IContainerAvailableTasksService _availableTasks = null!;
    private IShiftRepository _shiftRepository = null!;
    private IContainerAutofillService _autofill = null!;
    private IShiftLocationResolver _locations = null!;
    private IMediator _mediator = null!;
    private IUnitOfWork _unitOfWork = null!;
    private Func<ContainerAutofillRequest, IReadOnlyList<Guid>> _selectionFor = null!;
    private Func<List<ContainerTemplateResource>, List<ContainerTemplateResource>> _persistedView = null!;
    private BuildWeeklyToursSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _tasks.Clear();
        _locationByClient.Clear();
        _storedTemplates.Clear();
        _createdContainers.Clear();
        _autofillRequests.Clear();
        _containerNames.Clear();
        _selectionFor = request => request.RestrictToShiftIds!.ToList();
        _persistedView = templates => templates;

        var groupRepository = Substitute.For<IGroupRepository>();
        groupRepository.List().Returns(_ => new List<Group> { new() { Id = GroupId, Name = GroupName } });

        var groupItems = Substitute.For<IGroupItemRepository>();
        groupItems.GetShiftIdsByGroupIds(Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>()).Returns(_ => _tasks.Select(t => t.Id).ToList());

        _availableTasks = Substitute.For<IContainerAvailableTasksService>();
        _availableTasks.GetAvailableTasksOfGroupsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<ShiftValidityPeriod?>(), Arg.Any<int>(), Arg.Any<TimeOnly>(),
                Arg.Any<TimeOnly>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(_tasks.Where(t => RunsOn(t, ci.ArgAt<int>(2))).ToList()));

        _locations = Substitute.For<IShiftLocationResolver>();
        _locations.LocateAsync(Arg.Any<IReadOnlyList<Shift>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<ShiftLocation>>(ci.Arg<IReadOnlyList<Shift>>()
                .Where(s => s.ClientId.HasValue && _locationByClient.ContainsKey(s.ClientId.Value))
                .Select(s => new ShiftLocation(s.Id, _locationByClient[s.ClientId!.Value].Lat, _locationByClient[s.ClientId!.Value].Lon))
                .ToList()));

        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftRepository.AddWithSealedOrderHandling(Arg.Any<Shift>()).Returns(ci =>
        {
            var submitted = ci.Arg<Shift>();
            _createdContainers.Add(submitted);
            var stored = new Shift { Id = Guid.NewGuid(), Name = submitted.Name, ShiftType = ShiftType.IsContainer, Status = ShiftStatus.OriginalShift };
            _containerNames[stored.Id] = stored.Name;
            return stored;
        });
        _shiftRepository.Get(Arg.Any<Guid>()).Returns(ci => _tasks.FirstOrDefault(t => t.Id == ci.Arg<Guid>()));

        _autofill = Substitute.For<IContainerAutofillService>();
        _autofill.AutofillAsync(Arg.Any<ContainerAutofillRequest>()).Returns(ci =>
        {
            var request = ci.Arg<ContainerAutofillRequest>();
            _autofillRequests.Add(request);
            var selected = _selectionFor(request);
            return selected.Count == 0
                ? EmptyResult()
                : ContainerAutofillRouteFixture.Route(selected, request.RestrictToShiftIds!.Count);
        });

        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerLockResource { Id = Guid.NewGuid(), Acquired = true });
        _mediator.Send(Arg.Any<ReleaseContainerLockCommand>(), Arg.Any<CancellationToken>()).Returns(true);
        _mediator.Send(Arg.Any<PostContainerTemplatesCommand>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var command = ci.Arg<PostContainerTemplatesCommand>();
            _storedTemplates[command.ContainerId] = command.Resources.Select(r => Stored(r, command.ContainerId)).ToList();
            return new List<ContainerTemplateResource>();
        });
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>()).Returns(ci =>
            _persistedView(_storedTemplates.GetValueOrDefault(ci.Arg<GetContainerTemplatesQuery>().ContainerId) ?? []));

        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<BuildWeeklyToursSkill.ToursOutcome>>>())
            .Returns(ci => ci.Arg<Func<Task<BuildWeeklyToursSkill.ToursOutcome>>>()());

        var userService = Substitute.For<IUserService>();
        userService.GetInstanceId().Returns("instance-1");
        var macroResolver = Substitute.For<IDefaultShiftMacroResolver>();
        macroResolver.ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>()).Returns((Guid?)null);

        var visibility = ShiftVisibilityGuards.AllowAll();
        _skill = new BuildWeeklyToursSkill(
            groupRepository,
            TestGroupWriteVisibility.UnrestrictedGroups(),
            groupItems,
            GeoServicesPolicyTestFactory.Default(),
            Substitute.For<ISettingsReader>(),
            Substitute.For<ISettingsEncryptionService>(),
            macroResolver,
            _unitOfWork,
            userService,
            new WeeklyTourPlanner(_availableTasks, _locations, visibility),
            new WeeklyTourBuilder(
                _shiftRepository,
                _unitOfWork,
                _mediator,
                _autofill,
                new ContainerAutofillVisibilityFilter(visibility),
                new ContainerTemplateWriter(_mediator, new ScheduleMapper(), _shiftRepository)),
            new WeeklyTourPlacementVerifier(_mediator));
    }

    private static ContainerTemplateResource Stored(ContainerTemplateResource written, Guid containerId) => new()
    {
        Id = Guid.NewGuid(),
        ContainerId = containerId,
        Weekday = written.Weekday,
        IsHoliday = written.IsHoliday,
        IsWeekdayAndHoliday = written.IsWeekdayAndHoliday,
        FromTime = written.FromTime,
        UntilTime = written.UntilTime,
        RouteInfo = written.RouteInfo,
        ContainerTemplateItems = written.ContainerTemplateItems.ToList()
    };

    private static ContainerAutofillResult EmptyResult() => new(
        OptimizedRoute: [], SelectedShiftIds: [], TotalDistanceKm: 0, EstimatedTravelTime: TimeSpan.Zero, TotalWorkTime: TimeSpan.Zero,
        RemainingTime: TimeSpan.Zero, TotalAvailableShifts: 0, SelectedShiftCount: 0, DistanceMatrix: new double[0, 0],
        DurationMatrix: new double[0, 0], TravelTimeFromStartBase: TimeSpan.Zero, RouteIndices: [], DistanceFromStartBaseKm: 0,
        DistanceToEndBaseKm: 0, TravelTimeToEndBase: TimeSpan.Zero, FullRouteIndices: []);

    private static bool RunsOn(Shift shift, int storageWeekday) => storageWeekday switch
    {
        0 => shift.IsSunday,
        1 => shift.IsMonday,
        2 => shift.IsTuesday,
        3 => shift.IsWednesday,
        4 => shift.IsThursday,
        5 => shift.IsFriday,
        _ => shift.IsSaturday
    };

    private Guid GivenCustomer(string name, double lat, double lon, params int[] isoWeekdays)
    {
        var client = new Client { Id = Guid.NewGuid(), Name = name };
        _locationByClient[client.Id] = (lat, lon);
        foreach (var day in isoWeekdays)
        {
            _tasks.Add(Visit(client, day));
        }

        return client.Id;
    }

    private static Shift Visit(Client client, int isoWeekday) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"{client.Name} {isoWeekday}",
        Abbreviation = "V",
        ShiftType = ShiftType.IsTask,
        Status = ShiftStatus.SplitShift,
        IsTimeRange = true,
        ClientId = client.Id,
        Client = client,
        StartShift = new TimeOnly(8, 0),
        EndShift = new TimeOnly(17, 0),
        WorkTime = 0.75m,
        FromDate = new DateOnly(2026, 11, 2),
        IsMonday = isoWeekday == 1,
        IsTuesday = isoWeekday == 2,
        IsWednesday = isoWeekday == 3,
        IsThursday = isoWeekday == 4,
        IsFriday = isoWeekday == 5
    };

    private void GivenTwoRegions()
    {
        GivenCustomer("Bern A", BernLat, BernLon, 1, 3, 5);
        GivenCustomer("Bern B", BernLat + 0.01, BernLon + 0.01, 2, 4);
        GivenCustomer("Bern C", BernLat - 0.01, BernLon, 4);
        GivenCustomer("Winterthur A", WinterthurLat, WinterthurLon, 1, 3, 5);
        GivenCustomer("Winterthur B", WinterthurLat + 0.01, WinterthurLon + 0.01, 1, 4);
        GivenCustomer("Winterthur C", WinterthurLat, WinterthurLon - 0.01, 2);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = ["Admin"]
    };

    private static Dictionary<string, object> Params(int tourCount = 2) => new()
    {
        ["location"] = "Winterthur",
        ["tourCount"] = tourCount,
        ["groupName"] = GroupName
    };

    private Task<SkillResult> Run(int tourCount = 2) => _skill.ExecuteAsync(Ctx(), Params(tourCount));

    private HashSet<Guid> ContainerOf(Guid containerId) => _storedTemplates[containerId]
        .SelectMany(t => t.ContainerTemplateItems).Where(i => i.ShiftId.HasValue).Select(i => i.ShiftId!.Value).ToHashSet();

    [Test]
    public async Task EveryCustomer_IsInExactlyOneTour_WithAllItsWeekdayServices()
    {
        GivenTwoRegions();

        var result = await Run();

        result.Success.ShouldBeTrue(result.Message);
        _storedTemplates.Count.ShouldBe(2);
        foreach (var clientShifts in _tasks.GroupBy(t => t.ClientId))
        {
            var containersWithClient = _storedTemplates.Keys
                .Where(id => ContainerOf(id).Overlaps(clientShifts.Select(s => s.Id))).ToList();
            containersWithClient.Count.ShouldBe(1, $"customer {clientShifts.First().Client!.Name} is split over several tours");
            ContainerOf(containersWithClient[0]).IsSupersetOf(clientShifts.Select(s => s.Id)).ShouldBeTrue();
        }

        var bernTour = _storedTemplates.Keys.Single(id => ContainerOf(id).Contains(_tasks.First(t => t.Client!.Name == "Bern A").Id));
        ContainerOf(bernTour).Count.ShouldBe(6);
        _tasks.Where(t => t.Client!.Name.StartsWith("Winterthur", StringComparison.Ordinal))
            .ShouldAllBe(t => !ContainerOf(bernTour).Contains(t.Id));
    }

    [Test]
    public async Task EveryAutofill_IsRestrictedToTheServicesOfOneTourOnOneWeekday()
    {
        GivenTwoRegions();

        await Run();

        _autofillRequests.ShouldAllBe(r => r.RestrictToShiftIds != null && r.AdditionalAvailableWorkIds == null);
        foreach (var request in _autofillRequests)
        {
            var restricted = _tasks.Where(t => request.RestrictToShiftIds!.Contains(t.Id)).ToList();
            restricted.ShouldAllBe(t => RunsOn(t, request.Weekday));
            restricted.Select(t => t.Client!.Name.Split(' ')[0]).Distinct().Count().ShouldBe(1);
        }

        _autofillRequests.SelectMany(r => r.RestrictToShiftIds!).Distinct().Count().ShouldBe(_tasks.Count);
    }

    [Test]
    public async Task TheContainers_AreActiveOnlyOnWeekdaysThatHaveServices_AndNamedByNumber()
    {
        GivenCustomer("Bern B", BernLat, BernLon, 2, 4);
        GivenCustomer("Winterthur A", WinterthurLat, WinterthurLon, 1, 3);

        var result = await Run();

        result.Success.ShouldBeTrue(result.Message);
        var west = _createdContainers.Single(c => c.IsTuesday);
        west.IsThursday.ShouldBeTrue();
        west.IsMonday.ShouldBeFalse();
        west.IsWednesday.ShouldBeFalse();
        west.IsFriday.ShouldBeFalse();
        var east = _createdContainers.Single(c => c.IsMonday);
        east.IsWednesday.ShouldBeTrue();
        east.IsTuesday.ShouldBeFalse();
        _createdContainers.Select(c => c.Name).ShouldBe(["Tour 1", "Tour 2"], ignoreOrder: true);
        _createdContainers.ShouldAllBe(c => c.Status == ShiftStatus.SealedOrder && c.ShiftType == ShiftType.IsContainer);
        _storedTemplates.Values.SelectMany(t => t).Select(t => t.Weekday).Distinct().Order().ShouldBe([1, 2, 3, 4]);
    }

    [Test]
    public async Task AServiceThatDoesNotFitADay_IsReportedPerTourAndWeekday_NeverDropped()
    {
        GivenTwoRegions();
        var droppedName = "Bern A 3";
        var dropped = _tasks.Single(t => t.Name == droppedName).Id;
        _selectionFor = request => request.RestrictToShiftIds!.Where(id => id != dropped).ToList();

        var result = await Run();

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("NOT placed");
        result.Message.ShouldContain("Bern A");
        var data = System.Text.Json.JsonSerializer.Serialize(result.Data);
        data.ShouldContain("\"NotPlaced\":[\"Bern A\"]");
        _storedTemplates.Values.SelectMany(t => t).SelectMany(t => t.ContainerTemplateItems).Select(i => i.ShiftId)
            .ShouldNotContain(dropped);
        _storedTemplates.Values.SelectMany(t => t).SelectMany(t => t.ContainerTemplateItems).Count(i => i.ShiftId.HasValue)
            .ShouldBe(_tasks.Count - 1);
    }

    [Test]
    public async Task ATourThatPlacesNothing_FailsTheRun_AndReportsNoSuccess()
    {
        GivenTwoRegions();
        var bernIds = _tasks.Where(t => t.Client!.Name.StartsWith("Bern", StringComparison.Ordinal)).Select(t => t.Id).ToHashSet();
        _selectionFor = request => request.RestrictToShiftIds!.Where(id => !bernIds.Contains(id)).ToList();

        var result = await Run();

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("No service of 'Tour 1'");
        result.Message.ShouldContain("rolled back");
    }

    [Test]
    public async Task ATemplateThatDoesNotHoldWhatWasPlaced_FailsTheRun()
    {
        GivenTwoRegions();
        _persistedView = templates => templates
            .Select(t => new ContainerTemplateResource
            {
                Id = t.Id, ContainerId = t.ContainerId, Weekday = t.Weekday, IsHoliday = t.IsHoliday,
                FromTime = t.FromTime, UntilTime = t.UntilTime, RouteInfo = t.RouteInfo,
                ContainerTemplateItems = t.ContainerTemplateItems.Take(t.ContainerTemplateItems.Count - 1).ToList()
            }).ToList();

        var result = await Run();

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("could not be confirmed");
    }

    [Test]
    public async Task AServiceStoredInTheWrongTour_FailsTheVerification()
    {
        GivenTwoRegions();
        var foreign = _tasks.First(t => t.Client!.Name == "Winterthur A").Id;
        _persistedView = templates => templates
            .Select(t => t.ContainerTemplateItems.Any(i => i.ShiftId == _tasks.First(x => x.Client!.Name == "Bern A").Id)
                ? WithExtra(t, foreign)
                : t)
            .ToList();

        var result = await Run();

        result.Success.ShouldBeFalse();
    }

    private static ContainerTemplateResource WithExtra(ContainerTemplateResource template, Guid foreignShiftId)
    {
        var copy = Stored(template, template.ContainerId);
        copy.ContainerTemplateItems.Add(new ContainerTemplateItemResource { ShiftId = foreignShiftId });
        return copy;
    }

    [Test]
    public async Task AServiceThatStillRunsOnSeveralWeekdays_RefusesTheRun_BeforeAnythingIsCreated()
    {
        GivenTwoRegions();
        var client = new Client { Id = Guid.NewGuid(), Name = "Multi" };
        _locationByClient[client.Id] = (BernLat, BernLon);
        var multi = Visit(client, 1);
        multi.IsWednesday = true;
        _tasks.Add(multi);

        var result = await Run();

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("split_shifts_by_weekday");
        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task ACustomerWithoutALocation_IsReportedAsUnplanned_NotDropped()
    {
        GivenTwoRegions();
        var lost = new Client { Id = Guid.NewGuid(), Name = "Nowhere" };
        _tasks.Add(Visit(lost, 2));

        var result = await Run();

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("NOT planned at all");
        result.Message.ShouldContain("Nowhere");
        _storedTemplates.Values.SelectMany(t => t).SelectMany(t => t.ContainerTemplateItems).Count(i => i.ShiftId.HasValue)
            .ShouldBe(_tasks.Count - 1);
    }

    [Test]
    public async Task MoreToursThanLocatedCustomers_AreRefused()
    {
        GivenCustomer("Bern A", BernLat, BernLon, 1);
        GivenCustomer("Bern B", BernLat, BernLon, 2);

        var result = await Run(tourCount: 3);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("fewer than the 3 tours");
        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task ATourCountBelowTwo_IsRefused()
    {
        GivenTwoRegions();

        var result = await Run(tourCount: 1);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("at least 2");
    }

    [Test]
    public async Task TheResult_CarriesCountsPerTourAndWeekday_VerifiedAndTheRouteOfTheFirstContainer()
    {
        GivenTwoRegions();

        var result = await Run();

        result.Success.ShouldBeTrue(result.Message);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        json.ShouldContain("\"Verified\":true");
        json.ShouldContain("\"Visits\":" + _tasks.Count);
        json.ShouldContain("\"Placed\":" + _tasks.Count);
        json.ShouldContain("\"IsoWeekday\":1");
        json.ShouldContain(Klacks.Api.Application.Constants.ContainerTemplateRoutes.ForContainer(_storedTemplates.Keys.First()));
        result.Message.ShouldContain("verified");
    }

    [Test]
    public async Task TheEstimationNote_IsAppended_WhenAnAutofillUsedStraightLineEstimates()
    {
        GivenTwoRegions();
        _autofill.AutofillAsync(Arg.Any<ContainerAutofillRequest>()).Returns(ci =>
        {
            var request = ci.Arg<ContainerAutofillRequest>();
            _autofillRequests.Add(request);
            return ContainerAutofillRouteFixture.Route(request.RestrictToShiftIds!.ToList(), request.RestrictToShiftIds!.Count, isEstimated: true);
        });

        var result = await Run();

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("only estimates");
    }
}
