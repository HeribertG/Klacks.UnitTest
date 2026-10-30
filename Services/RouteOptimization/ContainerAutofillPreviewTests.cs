// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The dry run of the container autofill over the real services (InMemoryContainerAutofill): with at most two stops no
/// randomized step runs, so it must report exactly what the real run does, and it must store nothing and ask for no
/// directions. The located tasks, the tasks without a location and the emptied plans are told apart.
/// </summary>

using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Services.RouteOptimization;
using Klacks.Api.Domain.Services.RouteOptimization.ContainerAutofill;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RouteSegmentDirections = Klacks.Api.Domain.Services.RouteOptimization.RouteSegmentDirections;

namespace Klacks.UnitTest.Services.RouteOptimization;

[TestFixture]
public class ContainerAutofillPreviewTests
{
    private const int StoredWednesday = 3;
    private const int NoGeocodingLimit = 100;

    private static readonly TimeOnly DayStart = new(8, 0);
    private static readonly TimeOnly DayEnd = new(18, 0);
    private static readonly DateOnly TaskStart = new(2026, 11, 2);

    private DataBaseContext _context = null!;
    private IAddressCoordinateWriter _coordinateWriter = null!;
    private IGeocodingService _geocoding = null!;
    private IRouteDirectionsBuilder _directions = null!;
    private IContainerAvailableTasksService _availableTasks = null!;
    private ServiceProvider? _provider;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _coordinateWriter = Substitute.For<IAddressCoordinateWriter>();
        _geocoding = Substitute.For<IGeocodingService>();
        _directions = Substitute.For<IRouteDirectionsBuilder>();
        _directions
            .GetRouteDirectionsAsync(Arg.Any<List<Location>>(), Arg.Any<DistanceMatrix>(), Arg.Any<ContainerTransportMode>())
            .Returns(new List<RouteSegmentDirections>());
        _availableTasks = InMemoryContainerAutofill.AvailableTasks(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _provider?.Dispose();
        _context.Dispose();
    }

    [Test]
    public async Task Preview_ReportsWhatTheRealRunDoes()
    {
        await SeedAsync(Task("Near", 1), Task("Far", 2));
        var service = Service();

        var real = await service.AutofillAsync(new ContainerAutofillRequest(
            Guid.NewGuid(), StoredWednesday, false, InMemoryContainerAutofill.BaseAddress, InMemoryContainerAutofill.BaseAddress, DayStart, DayEnd));
        var preview = await service.PreviewAsync(Request(await NewContainerTasksAsync()));

        preview.Outcome.ShouldBe(ContainerAutofillPlanOutcome.Routed);
        preview.Result.SelectedShiftIds.ShouldBe(real.SelectedShiftIds, ignoreOrder: true);
        preview.Result.TotalDistanceKm.ShouldBe(real.TotalDistanceKm);
        preview.Result.EstimatedTravelTime.ShouldBe(real.EstimatedTravelTime);
        preview.Result.RemainingTime.ShouldBe(real.RemainingTime);
    }

    [Test]
    public async Task Preview_StoresNoCoordinatesAndAsksForNoDirections_TheRealRunDoes()
    {
        var task = Task("Unplaced", 1);
        task.Client!.Addresses.Single().Latitude = null;
        task.Client.Addresses.Single().Longitude = null;
        await SeedAsync(task);
        _geocoding
            .GeocodeAddressAsync(Arg.Is<string>(address => address != InMemoryContainerAutofill.BaseAddress), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(((double?)1, (double?)0));
        var service = Service();

        var preview = await service.PreviewAsync(Request(await NewContainerTasksAsync()));

        preview.Result.SelectedShiftIds.ShouldBe([task.Id]);
        await _coordinateWriter.DidNotReceiveWithAnyArgs().UpdateCoordinatesAsync(default, default, default, default);
        await _directions.DidNotReceiveWithAnyArgs().GetRouteDirectionsAsync(default!, default!, default);

        await service.AutofillAsync(new ContainerAutofillRequest(
            Guid.NewGuid(), StoredWednesday, false, InMemoryContainerAutofill.BaseAddress, InMemoryContainerAutofill.BaseAddress, DayStart, DayEnd));

        await _coordinateWriter.Received(1).UpdateCoordinatesAsync(
            task.Client.Addresses.Single().Id, 1, 0, Arg.Any<CancellationToken>());
        await _directions.ReceivedWithAnyArgs(1).GetRouteDirectionsAsync(default!, default!, default);
    }

    [Test]
    public async Task Preview_WithoutTimeRangeTasks_NeverCallsAService()
    {
        var fixedTask = Task("Fixed", 1);
        fixedTask.IsTimeRange = false;

        var preview = await Service().PreviewAsync(Request([fixedTask]));

        preview.Outcome.ShouldBe(ContainerAutofillPlanOutcome.NoTimeRangeTasks);
        preview.TimeRangeTaskCount.ShouldBe(0);
        await _geocoding.DidNotReceiveWithAnyArgs().GeocodeAddressAsync(default!, default!, default);
    }

    [Test]
    public async Task Preview_SaysWhyATaskHasNoLocation()
    {
        var first = Task("Shared", 1);
        var sameAddress = Task("Shared", 1);
        sameAddress.Client = first.Client;
        var noAddress = Task("Nowhere", 2);
        noAddress.Client!.Addresses.Clear();
        var noCity = Task("Cityless", 3);
        noCity.Client!.Addresses.Single().City = string.Empty;
        var notFound = Task("Unknown", 4);
        notFound.Client!.Addresses.Single().Latitude = null;

        var preview = await Service().PreviewAsync(Request([first, sameAddress, noAddress, noCity, notFound]));

        Reason(preview, sameAddress).ShouldBe(ContainerAutofillUnlocatedReason.SameAddress);
        Reason(preview, noAddress).ShouldBe(ContainerAutofillUnlocatedReason.NoAddress);
        Reason(preview, noCity).ShouldBe(ContainerAutofillUnlocatedReason.NoAddress);
        Reason(preview, notFound).ShouldBe(ContainerAutofillUnlocatedReason.NotLocated);
        preview.Result.SelectedShiftIds.ShouldBe([first.Id]);
    }

    [Test]
    public async Task Preview_SendsNoMoreAddressesToTheGeocoderThanItsLimit()
    {
        var first = Task("First", 1);
        var second = Task("Second", 2);
        foreach (var address in new[] { first, second }.Select(task => task.Client!.Addresses.Single()))
        {
            address.Latitude = null;
            address.Longitude = null;
        }

        var preview = await Service().PreviewAsync(Request([first, second]) with { MaxGeocodedAddresses = 1 });

        await _geocoding.Received(1).GeocodeAddressAsync(
            Arg.Is<string>(address => address != InMemoryContainerAutofill.BaseAddress), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Reason(preview, second).ShouldBe(ContainerAutofillUnlocatedReason.NotLookedUp);
    }

    [Test]
    public async Task EstimatedMatrix_IsReportedAlsoWhenNothingWasSelected()
    {
        var tooLate = Task("Late", 1);
        tooLate.StartShift = new TimeOnly(6, 0);
        tooLate.EndShift = new TimeOnly(7, 0);

        var preview = await Service(estimated: true).PreviewAsync(Request([tooLate]) with { TimeRangeTolerance = 1 });

        preview.Outcome.ShouldBe(ContainerAutofillPlanOutcome.NothingSelected);
        preview.TravelTimesEstimated.ShouldBeTrue();
        preview.Omissions.ShouldHaveSingleItem().Reason.ShouldBe(ContainerAutofillOmissionReason.TimeWindowConflict);
    }

    [Test]
    public async Task UnknownBase_EndsThePlanBeforeTheMatrix()
    {
        var preview = await Service().PreviewAsync(Request([Task("Near", 1)]) with { StartBase = "Unknown base" });

        preview.Outcome.ShouldBe(ContainerAutofillPlanOutcome.BaseNotLocated);
        preview.Omissions.ShouldBeEmpty();
    }

    [Test]
    public async Task NewContainerScope_ReadsValidityAndGroupsLikeAStoredContainer()
    {
        var groupId = Guid.NewGuid();
        var inGroup = Task("InGroup", 1);
        inGroup.GroupItems = [new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = inGroup.Id }];
        var ended = Task("Ended", 2);
        ended.GroupItems = [new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = ended.Id }];
        ended.UntilDate = TaskStart;
        var otherGroup = Task("Other", 3);
        await SeedAsync(inGroup, ended, otherGroup);

        var tasks = await _availableTasks.GetAvailableTasksForNewContainerAsync(
            new NewContainerTaskScope([groupId], TaskStart.AddDays(7), null), StoredWednesday, DayStart, DayEnd);

        tasks.Select(task => task.Id).ShouldBe([inGroup.Id]);
    }

    [Test]
    public async Task NewContainerScope_ALoneTaskInItsGroupKeepsTheFilter()
    {
        var groupId = Guid.NewGuid();
        var loneTask = Task("Lone", 1);
        loneTask.GroupItems = [new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = loneTask.Id }];
        var otherGroup = Task("Other", 2);
        await SeedAsync(loneTask, otherGroup);

        var tasks = await _availableTasks.GetAvailableTasksForNewContainerAsync(
            new NewContainerTaskScope([groupId], TaskStart, null), StoredWednesday, DayStart, DayEnd);

        tasks.Select(task => task.Id).ShouldBe([loneTask.Id]);
    }

    private static ContainerAutofillUnlocatedReason Reason(ContainerAutofillPreview preview, Shift task) =>
        preview.Unlocated.Single(entry => entry.ShiftId == task.Id).Reason;

    private IContainerAutofillService Service(bool estimated = false)
    {
        var (service, provider) = InMemoryContainerAutofill.Create(_availableTasks, _coordinateWriter, _geocoding, _directions, estimated);
        _provider = provider;
        return service;
    }

    private Task<List<Shift>> NewContainerTasksAsync() =>
        _availableTasks.GetAvailableTasksForNewContainerAsync(new NewContainerTaskScope([], TaskStart, null), StoredWednesday, DayStart, DayEnd);

    private static ContainerAutofillPreviewRequest Request(IReadOnlyList<Shift> tasks) => new(
        tasks, InMemoryContainerAutofill.BaseAddress, InMemoryContainerAutofill.BaseAddress, DayStart, DayEnd,
        ContainerTransportMode.ByCar, 0.5, NoGeocodingLimit);

    private async Task SeedAsync(params Shift[] tasks)
    {
        await _context.Shift.AddRangeAsync(tasks);
        await _context.SaveChangesAsync();
    }

    private static Shift Task(string name, double position)
    {
        return new Shift
        {
            Id = Guid.NewGuid(),
            Name = name,
            Abbreviation = name,
            Description = name,
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = TaskStart,
            StartShift = DayStart,
            EndShift = new TimeOnly(17, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            WorkTime = 0.75m,
            IsTimeRange = true,
            IsWednesday = true,
            Client = InMemoryContainerAutofill.ClientAt(name, position),
        };
    }
}
