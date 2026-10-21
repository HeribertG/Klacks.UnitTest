// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for ContainerAutofillService, written against the pre-split implementation so the
/// split into collaborators can be proven behaviour-neutral. The distance matrix is a 1D line (latitude =
/// position, 1 unit = 1 km = 600 s), so every travel time is exact. Only scenarios with at most two selected
/// stops assert the route order: above two stops the unseeded ant colony optimizer reorders the route.
/// The service is built through one helper so a constructor change during the split touches nothing else.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Services.RouteOptimization;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using RouteSegmentDirections = Klacks.Api.Domain.Services.RouteOptimization.RouteSegmentDirections;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Services.RouteOptimization;

[TestFixture]
public class ContainerAutofillServiceCharacterizationTests
{
    private const double SecondsPerUnit = 600;
    private const string BaseAddress = "Base";
    private const string EndAddress = "End";
    private const double BaseLatitude = 0;
    private const double EndLatitude = 5;

    private static readonly TimeOnly ContainerFrom = new(8, 0);
    private static readonly TimeOnly ContainerUntil = new(12, 0);

    private IContainerAvailableTasksService _availableTasks = null!;
    private IRouteOptimizationService _routeOptimization = null!;
    private IGeocodingService _geocoding = null!;
    private IAddressCoordinateWriter _coordinateWriter = null!;
    private ISettingsReader _settingsReader = null!;
    private IRouteDirectionsBuilder _directionsBuilder = null!;
    private List<RouteSegmentDirections> _directions = null!;
    private bool _matrixIsEstimated;
    private ServiceProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _availableTasks = Substitute.For<IContainerAvailableTasksService>();
        _routeOptimization = Substitute.For<IRouteOptimizationService>();
        _geocoding = Substitute.For<IGeocodingService>();
        _coordinateWriter = Substitute.For<IAddressCoordinateWriter>();
        _settingsReader = Substitute.For<ISettingsReader>();
        _directionsBuilder = Substitute.For<IRouteDirectionsBuilder>();
        _directions = [];
        _matrixIsEstimated = false;

        _routeOptimization
            .CalculateDistanceMatrixForLocationsAsync(Arg.Any<List<Location>>(), Arg.Any<ContainerTransportMode>())
            .Returns(call => BuildLineMatrix(call.Arg<List<Location>>()));
        _geocoding
            .GeocodeAddressAsync(BaseAddress, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(((double?)BaseLatitude, (double?)0));
        _geocoding
            .GeocodeAddressAsync(EndAddress, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(((double?)EndLatitude, (double?)0));
        _settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsEntity?)null);
        _directionsBuilder
            .GetRouteDirectionsAsync(Arg.Any<List<Location>>(), Arg.Any<DistanceMatrix>(), Arg.Any<ContainerTransportMode>())
            .Returns(_ => _directions);
    }

    [TearDown]
    public void TearDown() => _provider?.Dispose();

    [Test]
    public async Task NoTimeRangeTasks_ReturnsEmptyResult_CountingAllAvailableTasks()
    {
        // Arrange
        GivenTasks(CreateShift("A", 1, isTimeRange: false), CreateShift("B", 2, isTimeRange: false));

        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest(transportMode: ContainerTransportMode.ByFoot));

        // Assert
        result.TotalAvailableShifts.ShouldBe(2);
        result.SelectedShiftCount.ShouldBe(0);
        result.OptimizedRoute.ShouldBeEmpty();
        result.TransportMode.ShouldBe(ContainerTransportMode.ByFoot);
        await _routeOptimization.DidNotReceiveWithAnyArgs().CalculateDistanceMatrixForLocationsAsync(default!, default);
    }

    [Test]
    public async Task NoGeocodableLocation_ReturnsEmptyResult_CountingOnlyTimeRangeTasks()
    {
        // Arrange
        var withoutAddress = CreateShift("A", 1);
        withoutAddress.Client!.Addresses = [];
        GivenTasks(withoutAddress, CreateShift("B", 2, isTimeRange: false));

        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest());

        // Assert
        result.TotalAvailableShifts.ShouldBe(1);
        result.SelectedShiftCount.ShouldBe(0);
    }

    [Test]
    public async Task BaseNotGeocodable_ReturnsEmptyResult_CountingOnlyTimeRangeTasks()
    {
        // Arrange
        GivenTasks(CreateShift("A", 1), CreateShift("B", 2), CreateShift("C", 3, isTimeRange: false));

        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest(startBase: "Nowhere"));

        // Assert
        result.TotalAvailableShifts.ShouldBe(2);
        result.SelectedShiftCount.ShouldBe(0);
        await _routeOptimization.DidNotReceiveWithAnyArgs().CalculateDistanceMatrixForLocationsAsync(default!, default);
    }

    [Test]
    public async Task TwoStops_SameBase_BuildsExactResult()
    {
        // Arrange
        var a = CreateShift("A", 1);
        var b = CreateShift("B", 2);
        GivenTasks(a, b);
        _matrixIsEstimated = true;
        _directions = [new RouteSegmentDirections("StartBase", "A", "car", 1, TimeSpan.FromMinutes(10), [])];

        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest());

        // Assert
        result.SelectedShiftIds.ShouldBe([a.Id, b.Id]);
        result.RouteIndices.ShouldBe([0, 1]);
        result.FullRouteIndices.ShouldBe([2, 0, 1, 2]);
        result.OptimizedRoute.Select(l => l.Name).ShouldBe(["StartBase", "A", "B", "StartBase"]);
        result.TotalDistanceKm.ShouldBe(4);
        result.DistanceFromStartBaseKm.ShouldBe(1);
        result.DistanceToEndBaseKm.ShouldBe(2);
        result.TravelTimeFromStartBase.ShouldBe(TimeSpan.FromSeconds(600));
        result.TravelTimeToEndBase.ShouldBe(TimeSpan.FromSeconds(1200));
        result.TotalWorkTime.ShouldBe(TimeSpan.FromHours(1));
        result.TotalBriefingDebriefingTime.ShouldBe(TimeSpan.FromHours(1));
        result.EstimatedTravelTime.ShouldBe(TimeSpan.FromSeconds(6000));
        result.RemainingTime.ShouldBe(TimeSpan.FromSeconds(8400));
        result.TotalAvailableShifts.ShouldBe(2);
        result.SelectedShiftCount.ShouldBe(2);
        result.PlacedTimeBlocks.ShouldBeNull();
        result.IsEstimated.ShouldBeTrue();
        result.SegmentDirections.ShouldBeSameAs(_directions);
        await _directionsBuilder.Received(1).GetRouteDirectionsAsync(
            result.OptimizedRoute, Arg.Any<DistanceMatrix>(), ContainerTransportMode.ByCar);
        await _geocoding.Received(2).GeocodeAddressAsync(BaseAddress, "Switzerland", Arg.Any<CancellationToken>());
        await _coordinateWriter.DidNotReceiveWithAnyArgs().UpdateCoordinatesAsync(default, default, default, default);
    }

    [Test]
    public async Task CandidateLocation_CarriesShiftDataAndMapsMixToCar()
    {
        // Arrange
        var a = CreateShift("A", 1, workHours: 1.5m);
        a.BriefingTime = new TimeOnly(0, 10);
        a.DebriefingTime = new TimeOnly(0, 5);
        GivenTasks(a);

        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest(transportMode: ContainerTransportMode.Mix));

        // Assert
        var stop = result.OptimizedRoute[1];
        stop.Name.ShouldBe("A");
        stop.Address.ShouldBe("A-Street 1, 8000 A-City");
        stop.ShiftId.ShouldBe(a.Id);
        stop.TransportMode.ShouldBe(TransportMode.ByCar);
        stop.BriefingTime.ShouldBe(TimeSpan.FromMinutes(10));
        stop.DebriefingTime.ShouldBe(TimeSpan.FromMinutes(5));
        stop.WorkTime.ShouldBe(TimeSpan.FromHours(1.5));
        stop.TimeRangeStart.ShouldBe(a.StartShift);
        stop.TimeRangeEnd.ShouldBe(a.EndShift);
        result.TransportMode.ShouldBe(ContainerTransportMode.Mix);
        await _settingsReader.Received(1).GetSetting(SettingKeys.RouteMinTravelTimeByCar);
    }

    [Test]
    public async Task ClientWithoutName_IsNamedUnknown()
    {
        // Arrange
        var a = CreateShift("A", 1);
        a.Client = null;
        var withAddressButNoClient = CreateShift("B", 2);
        withAddressButNoClient.Client!.Name = null!;
        GivenTasks(a, withAddressButNoClient);

        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest());

        // Assert
        result.OptimizedRoute.Select(l => l.Name).ShouldBe(["StartBase", "Unknown", "StartBase"]);
    }

    [Test]
    public async Task DuplicateAddressAndMissingCity_AreSkipped()
    {
        // Arrange
        var a = CreateShift("A", 1);
        var duplicate = CreateShift("A", 1);
        var noCity = CreateShift("C", 3);
        noCity.Client!.Addresses.First().City = string.Empty;
        GivenTasks(a, duplicate, noCity);

        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest());

        // Assert
        result.SelectedShiftIds.ShouldBe([a.Id]);
        result.TotalAvailableShifts.ShouldBe(3);
        await _routeOptimization.Received(1).CalculateDistanceMatrixForLocationsAsync(
            Arg.Is<List<Location>>(l => l.Count == 2), ContainerTransportMode.ByCar);
    }

    [Test]
    public async Task MissingCoordinates_AreGeocodedAndWrittenBack()
    {
        // Arrange
        var stored = CreateShift("A", 1);
        var needsGeocoding = CreateShift("B", 2);
        var address = needsGeocoding.Client!.Addresses.First();
        address.Latitude = null;
        address.Longitude = null;
        address.Country = "Austria";
        _geocoding
            .GeocodeAddressAsync("B-Street 1, 8000 B-City", "Austria", Arg.Any<CancellationToken>())
            .Returns(((double?)2, (double?)0));
        GivenTasks(needsGeocoding, stored);

        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest());

        // Assert
        result.SelectedShiftIds.ShouldBe([stored.Id, needsGeocoding.Id]);
        await _coordinateWriter.Received(1).UpdateCoordinatesAsync(address.Id, 2, 0, CancellationToken.None);
    }

    [Test]
    public async Task EmptyCountry_IsPassedAsIs_AndUngeocodableShiftIsDropped()
    {
        // Arrange
        var stored = CreateShift("A", 1);
        var notFound = CreateShift("B", 2);
        var address = notFound.Client!.Addresses.First();
        address.Latitude = null;
        address.Longitude = null;
        _geocoding
            .GeocodeAddressAsync("B-Street 1, 8000 B-City", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(((double?)null, (double?)null));
        GivenTasks(stored, notFound);

        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest());

        // Assert
        result.SelectedShiftIds.ShouldBe([stored.Id]);
        await _geocoding.Received(1).GeocodeAddressAsync("B-Street 1, 8000 B-City", string.Empty, Arg.Any<CancellationToken>());
        await _coordinateWriter.DidNotReceiveWithAnyArgs().UpdateCoordinatesAsync(default, default, default, default);
    }

    [Test]
    public async Task MinTravelTimeSetting_RaisesShortLegsButKeepsDiagonalAndLongLegs()
    {
        // Arrange
        GivenTasks(CreateShift("A", 1), CreateShift("B", 2));
        _settingsReader.GetSetting(SettingKeys.RouteMinTravelTimeByCar)
            .Returns(new SettingsEntity { Type = SettingKeys.RouteMinTravelTimeByCar, Value = "15" });

        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest());

        // Assert
        result.DurationMatrix[2, 0].ShouldBe(900);
        result.DurationMatrix[0, 1].ShouldBe(900);
        result.DurationMatrix[1, 2].ShouldBe(1200);
        result.DurationMatrix[0, 0].ShouldBe(0);
        result.TravelTimeFromStartBase.ShouldBe(TimeSpan.FromSeconds(900));
        result.DistanceMatrix[2, 0].ShouldBe(1);
    }

    [TestCase(ContainerTransportMode.ByBicycle, SettingKeys.RouteMinTravelTimeByBicycle)]
    [TestCase(ContainerTransportMode.ByFoot, SettingKeys.RouteMinTravelTimeByFoot)]
    [TestCase(ContainerTransportMode.Mix, SettingKeys.RouteMinTravelTimeByCar)]
    public async Task MinTravelTimeSetting_IsReadPerTransportMode(ContainerTransportMode mode, string expectedKey)
    {
        // Arrange
        GivenTasks(CreateShift("A", 1));

        // Act
        await CreateSut().AutofillAsync(CreateRequest(transportMode: mode));

        // Assert
        await _settingsReader.Received(1).GetSetting(expectedKey);
    }

    [Test]
    public async Task UnparsableMinTravelTimeSetting_LeavesMatrixUntouched()
    {
        // Arrange
        GivenTasks(CreateShift("A", 1));
        _settingsReader.GetSetting(SettingKeys.RouteMinTravelTimeByCar)
            .Returns(new SettingsEntity { Type = SettingKeys.RouteMinTravelTimeByCar, Value = "abc" });

        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest());

        // Assert
        result.DurationMatrix[1, 0].ShouldBe(600);
    }

    [Test]
    public async Task CandidateThatCannotReturnBeforeContainerEnd_IsExcluded()
    {
        // Arrange
        var near = CreateShift("A", 1);
        var far = CreateShift("B", 12);
        GivenTasks(near, far);

        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest());

        // Assert
        result.SelectedShiftIds.ShouldBe([near.Id]);
        result.TotalAvailableShifts.ShouldBe(2);
    }

    [Test]
    public async Task CandidateOutsideItsTimeRange_IsExcluded()
    {
        // Arrange
        var inWindow = CreateShift("A", 1);
        var lateWindow = CreateShift("B", 2, windowStart: new TimeOnly(14, 0), windowEnd: new TimeOnly(15, 0));
        GivenTasks(inWindow, lateWindow);

        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest());

        // Assert
        result.SelectedShiftIds.ShouldBe([inWindow.Id]);
    }

    [Test]
    public async Task ToleranceZero_DisablesTimeRangeCheck()
    {
        // Arrange
        var inWindow = CreateShift("A", 1);
        var lateWindow = CreateShift("B", 2, windowStart: new TimeOnly(14, 0), windowEnd: new TimeOnly(14, 1));
        GivenTasks(inWindow, lateWindow);

        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest(tolerance: 0));

        // Assert
        result.SelectedShiftIds.ShouldBe([inWindow.Id, lateWindow.Id]);
    }

    [Test]
    public async Task LockedShift_ViolatingItsTimeRange_StaysInRouteFirst()
    {
        // Arrange
        var free = CreateShift("A", 2);
        var locked = CreateShift("B", 1, windowStart: new TimeOnly(14, 0), windowEnd: new TimeOnly(15, 0));
        GivenTasks(free, locked);
        IReadOnlyCollection<Guid> lockIds = [locked.Id, Guid.NewGuid()];

        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest(additionalWorkIds: lockIds));

        // Assert
        result.SelectedShiftIds.ShouldBe([locked.Id, free.Id]);
        await _availableTasks.Received(1).GetAvailableTasksAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Is(ContainerFrom), Arg.Is(ContainerUntil),
            Arg.Is<string?>(v => v == null), Arg.Is<Guid?>(v => v == null),
            Arg.Is<bool?>(v => v == null), Arg.Is<bool?>(v => v == null),
            Arg.Is(lockIds), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LockedShifts_FollowCandidateOrder_NotRequestOrderOrCost()
    {
        // Arrange
        var far = CreateShift("A", 2);
        var near = CreateShift("B", 1);
        GivenTasks(far, near);

        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest(additionalWorkIds: [near.Id, far.Id]));

        // Assert
        result.SelectedShiftIds.ShouldBe([far.Id, near.Id]);
    }

    [Test]
    public async Task PostInsertion_AddsEarlyWindowShiftBeforeGreedyPick()
    {
        // Arrange
        var longStay = CreateShift("A", 1, workHours: 2m);
        var earlyWindow = CreateShift("B", 1.5, workHours: 0.5m, windowStart: new TimeOnly(8, 0), windowEnd: new TimeOnly(8, 20));
        GivenTasks(longStay, earlyWindow);

        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest());

        // Assert
        result.SelectedShiftIds.ShouldBe([earlyWindow.Id, longStay.Id]);
        result.FullRouteIndices.ShouldBe([2, 1, 0, 2]);
    }

    [Test]
    public async Task UnmovableBlock_ShiftsGreedyArrival_ButPostInsertionIgnoresIt()
    {
        // Arrange
        var wide = CreateShift("A", 1);
        var earlyWindow = CreateShift("B", 1.5, windowStart: new TimeOnly(8, 0), windowEnd: new TimeOnly(8, 20));
        GivenTasks(wide, earlyWindow);
        var block = new TimeBlock(Guid.NewGuid(), "Meeting", new TimeOnly(8, 0), new TimeOnly(9, 0), TimeSpan.FromHours(1), false);

        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest(timeBlocks: [block]));

        // Assert
        result.SelectedShiftIds.ShouldBe([earlyWindow.Id, wide.Id]);
        result.PlacedTimeBlocks.ShouldNotBeNull();
        result.PlacedTimeBlocks!.Single().Block.ShouldBe(block);
        result.RemainingTime.ShouldBe(TimeSpan.FromSeconds(10800 - 5400));
    }

    [Test]
    public async Task DifferentEndBase_GetsOwnIndex()
    {
        // Arrange
        var a = CreateShift("A", 1);
        var b = CreateShift("B", 3);
        GivenTasks(a, b);

        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest(endBase: EndAddress));

        // Assert
        result.FullRouteIndices.First().ShouldBe(2);
        result.FullRouteIndices.Last().ShouldBe(3);
        result.OptimizedRoute.Last().Name.ShouldBe("EndBase");
        result.DistanceToEndBaseKm.ShouldBe(2);
        result.TotalDistanceKm.ShouldBe(5);
    }

    [Test]
    public async Task MoreThanTwoStops_AreAllSelectedAfterRouteOptimization()
    {
        // Arrange
        var shifts = new[] { CreateShift("A", 3), CreateShift("B", 1), CreateShift("C", 4), CreateShift("D", 2) };
        GivenTasks(shifts);

        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest(endBase: EndAddress));

        // Assert
        result.SelectedShiftCount.ShouldBe(4);
        result.SelectedShiftIds.ShouldBe(shifts.Select(s => s.Id), ignoreOrder: true);
        result.RouteIndices.ShouldBe([0, 1, 2, 3], ignoreOrder: true);
        result.FullRouteIndices.First().ShouldBe(4);
        result.FullRouteIndices.Last().ShouldBe(5);
        result.TotalWorkTime.ShouldBe(TimeSpan.FromHours(2));
    }

    private IContainerAutofillService CreateSut()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_availableTasks);
        services.AddSingleton(_routeOptimization);
        services.AddSingleton(_geocoding);
        services.AddSingleton(_coordinateWriter);
        services.AddSingleton(_settingsReader);
        services.AddSingleton(_directionsBuilder);
        services.AddScoped<IContainerAutofillService, ContainerAutofillService>();
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        return _provider.CreateScope().ServiceProvider.GetRequiredService<IContainerAutofillService>();
    }

    private void GivenTasks(params Shift[] shifts)
    {
        _availableTasks
            .GetAvailableTasksAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<TimeOnly>(), Arg.Any<TimeOnly>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns(shifts.ToList());
    }

    private static ContainerAutofillRequest CreateRequest(
        string startBase = BaseAddress,
        string endBase = BaseAddress,
        ContainerTransportMode transportMode = ContainerTransportMode.ByCar,
        double tolerance = 0.5,
        List<TimeBlock>? timeBlocks = null,
        IReadOnlyCollection<Guid>? additionalWorkIds = null)
    {
        return new ContainerAutofillRequest(
            Guid.NewGuid(), 1, false, startBase, endBase, ContainerFrom, ContainerUntil,
            transportMode, tolerance, CancellationToken.None, timeBlocks, additionalWorkIds);
    }

    private static Shift CreateShift(
        string name,
        double position,
        bool isTimeRange = true,
        decimal workHours = 0.5m,
        TimeOnly? windowStart = null,
        TimeOnly? windowEnd = null)
    {
        return new Shift
        {
            Id = Guid.NewGuid(),
            Name = name,
            IsTimeRange = isTimeRange,
            WorkTime = workHours,
            StartShift = windowStart ?? new TimeOnly(8, 0),
            EndShift = windowEnd ?? new TimeOnly(12, 0),
            Client = new Client
            {
                Id = Guid.NewGuid(),
                Name = name,
                Addresses =
                [
                    new Address
                    {
                        Id = Guid.NewGuid(),
                        Street = $"{name}-Street 1",
                        Zip = "8000",
                        City = $"{name}-City",
                        Latitude = position,
                        Longitude = 0
                    }
                ]
            }
        };
    }

    private DistanceMatrix BuildLineMatrix(List<Location> locations)
    {
        var size = locations.Count;
        var distances = new double[size, size];
        var durations = new double[size, size];
        for (var i = 0; i < size; i++)
        {
            for (var j = 0; j < size; j++)
            {
                var units = Math.Abs(locations[i].Latitude - locations[j].Latitude);
                distances[i, j] = units;
                durations[i, j] = units * SecondsPerUnit;
            }
        }

        return new DistanceMatrix(locations, distances, durations, IsEstimated: _matrixIsEstimated);
    }
}
