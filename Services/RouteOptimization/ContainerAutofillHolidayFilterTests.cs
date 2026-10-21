// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Proves that the container autofill offers exactly the TimeRange tasks the available-task list
/// (ContainerAvailableTasksService, the rule behind GET Containers/available-tasks) returns for the same holiday flag:
/// a holiday request only gets holiday tasks (Shift.IsHoliday), a weekday request never gets them. The real list
/// service runs over EF InMemory; only geocoding, the distance matrix and the directions are faked.
/// </summary>

using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.ContainerTemplates;
using Klacks.Api.Domain.Services.RouteOptimization;
using Klacks.Api.Infrastructure.Extensions;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using RouteSegmentDirections = Klacks.Api.Domain.Services.RouteOptimization.RouteSegmentDirections;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Services.RouteOptimization;

[TestFixture]
public class ContainerAutofillHolidayFilterTests
{
    private const int Monday = 1;
    private const string BaseAddress = "Base";
    private const double SecondsPerUnit = 600;
    private const string DefaultCountryQueryName = "Österreich";

    private static readonly TimeOnly ContainerFrom = new(8, 0);
    private static readonly TimeOnly ContainerUntil = new(12, 0);

    private DataBaseContext _context = null!;
    private IContainerAvailableTasksService _availableTasks = null!;
    private ServiceProvider _provider = null!;
    private Shift _container = null!;
    private Shift _weekdayTask = null!;
    private Shift _holidayTask = null!;
    private Shift _weekdayAndHolidayTask = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _availableTasks = CreateAvailableTasksService(_context);

        _container = CreateShift("Container", ShiftType.IsContainer, isTimeRange: false, position: null);
        _weekdayTask = CreateShift("Weekday", ShiftType.IsTask, isTimeRange: true, position: 1);
        _holidayTask = CreateShift("Holiday", ShiftType.IsTask, isTimeRange: true, position: 2);
        _holidayTask.IsHoliday = true;
        _weekdayAndHolidayTask = CreateShift("WeekdayAndHoliday", ShiftType.IsTask, isTimeRange: true, position: 3);
        _weekdayAndHolidayTask.IsWeekdayAndHoliday = true;

        await _context.Shift.AddRangeAsync(_container, _weekdayTask, _holidayTask, _weekdayAndHolidayTask);
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _provider?.Dispose();
        _context?.Dispose();
    }

    [Test]
    public async Task HolidayRequest_OffersOnlyHolidayTasks()
    {
        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest(isHoliday: true));

        // Assert
        result.TotalAvailableShifts.ShouldBe(1);
        result.SelectedShiftIds.ShouldBe([_holidayTask.Id]);
    }

    [Test]
    public async Task WeekdayRequest_NeverOffersHolidayTasks()
    {
        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest(isHoliday: false));

        // Assert
        result.TotalAvailableShifts.ShouldBe(2);
        result.SelectedShiftIds.ShouldBe([_weekdayTask.Id, _weekdayAndHolidayTask.Id], ignoreOrder: true);
        result.SelectedShiftIds.ShouldNotContain(_holidayTask.Id);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Candidates_AreExactlyTheTimeRangeTasksOfTheAvailableTaskListForTheSameHolidayFlag(bool isHoliday)
    {
        // Arrange
        var listed = await _availableTasks.GetAvailableTasksAsync(
            _container.Id, Monday, ContainerFrom, ContainerUntil, isHoliday: isHoliday);

        // Act
        var result = await CreateSut().AutofillAsync(CreateRequest(isHoliday));

        // Assert
        listed.ShouldNotBeEmpty();
        result.SelectedShiftIds.ShouldBe(listed.Where(s => s.IsTimeRange).Select(s => s.Id), ignoreOrder: true);
    }

    private IContainerAutofillService CreateSut()
    {
        var routeOptimization = Substitute.For<IRouteOptimizationService>();
        routeOptimization
            .CalculateDistanceMatrixForLocationsAsync(Arg.Any<List<Location>>(), Arg.Any<ContainerTransportMode>())
            .Returns(call => BuildLineMatrix(call.Arg<List<Location>>()));

        var geocoding = Substitute.For<IGeocodingService>();
        geocoding
            .GeocodeAddressAsync(BaseAddress, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(((double?)0, (double?)0));

        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsEntity?)null);

        var countryResolver = Substitute.For<ICountryResolver>();
        countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns(new Countries
        {
            Id = Guid.NewGuid(),
            Abbreviation = "AT",
            Prefix = "+43",
            Name = new MultiLanguage { De = DefaultCountryQueryName, En = "Austria" }
        });

        var directionsBuilder = Substitute.For<IRouteDirectionsBuilder>();
        directionsBuilder
            .GetRouteDirectionsAsync(Arg.Any<List<Location>>(), Arg.Any<DistanceMatrix>(), Arg.Any<ContainerTransportMode>())
            .Returns(new List<RouteSegmentDirections>());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_availableTasks);
        services.AddSingleton(routeOptimization);
        services.AddSingleton(geocoding);
        services.AddSingleton(Substitute.For<IAddressCoordinateWriter>());
        services.AddSingleton(settingsReader);
        services.AddSingleton(countryResolver);
        services.AddSingleton(directionsBuilder);
        services.AddContainerAutofillServices();
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        return _provider.CreateScope().ServiceProvider.GetRequiredService<IContainerAutofillService>();
    }

    private ContainerAutofillRequest CreateRequest(bool isHoliday)
    {
        return new ContainerAutofillRequest(
            _container.Id, Monday, isHoliday, BaseAddress, BaseAddress, ContainerFrom, ContainerUntil,
            ContainerTransportMode.ByCar, 0.5, CancellationToken.None);
    }

    private static IContainerAvailableTasksService CreateAvailableTasksService(DataBaseContext context)
    {
        var collectionUpdateService = new EntityCollectionUpdateService(context);
        var shiftRepository = new ShiftRepository(
            context,
            Substitute.For<ILogger<Shift>>(),
            Substitute.For<IShiftQueryPipelineService>(),
            Substitute.For<IShiftGroupManagementService>(),
            collectionUpdateService,
            Substitute.For<IShiftValidator>(),
            new ScheduleMapper(),
            new FixedCompanyClock(DateTimeOffset.UtcNow));
        var groupItemRepository = new GroupItemRepository(context, Substitute.For<ILogger<GroupItem>>());
        var containerTemplateService = new ContainerTemplateService(
            Substitute.For<IUnitOfWork>(), Substitute.For<ILogger<ContainerTemplateService>>());
        var containerTemplateRepository = new ContainerTemplateRepository(
            context, Substitute.For<ILogger<ContainerTemplate>>(), collectionUpdateService, containerTemplateService);

        return new ContainerAvailableTasksService(
            shiftRepository,
            groupItemRepository,
            containerTemplateRepository,
            Substitute.For<ILogger<ContainerAvailableTasksService>>());
    }

    private static Shift CreateShift(string name, ShiftType shiftType, bool isTimeRange, double? position)
    {
        return new Shift
        {
            Id = Guid.NewGuid(),
            Name = name,
            Abbreviation = name,
            Description = name,
            Status = ShiftStatus.OriginalShift,
            ShiftType = shiftType,
            FromDate = DateOnly.FromDateTime(DateTime.UtcNow),
            StartShift = ContainerFrom,
            EndShift = ContainerUntil,
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            WorkTime = 0.5m,
            IsTimeRange = isTimeRange,
            IsMonday = true,
            Client = position is null ? null : CreateClient(name, position.Value)
        };
    }

    private static Client CreateClient(string name, double position)
    {
        return new Client
        {
            Id = Guid.NewGuid(),
            Name = name,
            Addresses =
            [
                new Address
                {
                    Id = Guid.NewGuid(),
                    ValidFrom = DateTime.UtcNow.Date,
                    Street = $"{name}-Street 1",
                    Zip = "8000",
                    City = $"{name}-City",
                    Country = "AT",
                    Latitude = position,
                    Longitude = 0
                }
            ]
        };
    }

    private static DistanceMatrix BuildLineMatrix(List<Location> locations)
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

        return new DistanceMatrix(locations, distances, durations, IsEstimated: false);
    }
}
