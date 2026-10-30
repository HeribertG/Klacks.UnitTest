// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The real container autofill over EF InMemory: the available-task list (ContainerAvailableTasksService with ShiftReader,
/// GroupItemRepository and ContainerTemplateRepository) and the autofill services as AddContainerAutofillServices
/// registers them; only geocoding of the base address, the distance matrix (a line by latitude, 600 s per unit) and the
/// directions are faked. Clients placed with ClientAt geocode from their stored coordinates. A test that watches the
/// writes or the external services hands in its own coordinate writer, geocoder or directions builder; estimated marks
/// the matrix as the fallback of an unreachable routing service.
/// </summary>

using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Services.ContainerTemplates;
using Klacks.Api.Domain.Services.RouteOptimization;
using Klacks.Api.Infrastructure.Extensions;
using Klacks.Api.Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using RouteSegmentDirections = Klacks.Api.Domain.Services.RouteOptimization.RouteSegmentDirections;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.TestHelpers;

public static class InMemoryContainerAutofill
{
    public const string BaseAddress = "Base";

    private const double SecondsPerUnit = 600;
    private const string DefaultCountryQueryName = "Österreich";

    public static IContainerAvailableTasksService AvailableTasks(DataBaseContext context)
    {
        var collectionUpdateService = new EntityCollectionUpdateService(context);
        var groupItemRepository = new GroupItemRepository(context, Substitute.For<ILogger<GroupItem>>());
        var containerTemplateService = new ContainerTemplateService(
            Substitute.For<IUnitOfWork>(), Substitute.For<ILogger<ContainerTemplateService>>());
        var containerTemplateRepository = new ContainerTemplateRepository(
            context, Substitute.For<ILogger<ContainerTemplate>>(), collectionUpdateService, containerTemplateService);

        return new ContainerAvailableTasksService(
            new ShiftReader(context),
            groupItemRepository,
            containerTemplateRepository,
            Substitute.For<ILogger<ContainerAvailableTasksService>>());
    }

    /// <summary>
    /// Builds the autofill service on top of the given available-task list; the caller disposes the returned provider.
    /// </summary>
    public static (IContainerAutofillService Service, ServiceProvider Provider) Create(
        IContainerAvailableTasksService availableTasks,
        IAddressCoordinateWriter? coordinateWriter = null,
        IGeocodingService? geocodingService = null,
        IRouteDirectionsBuilder? routeDirectionsBuilder = null,
        bool estimated = false)
    {
        var routeOptimization = Substitute.For<IRouteOptimizationService>();
        routeOptimization
            .CalculateDistanceMatrixForLocationsAsync(Arg.Any<List<Location>>(), Arg.Any<ContainerTransportMode>())
            .Returns(call => BuildLineMatrix(call.Arg<List<Location>>()) with { IsEstimated = estimated });

        var geocoding = geocodingService ?? Substitute.For<IGeocodingService>();
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

        var directionsBuilder = routeDirectionsBuilder ?? Substitute.For<IRouteDirectionsBuilder>();
        directionsBuilder
            .GetRouteDirectionsAsync(Arg.Any<List<Location>>(), Arg.Any<DistanceMatrix>(), Arg.Any<ContainerTransportMode>())
            .Returns(new List<RouteSegmentDirections>());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(availableTasks);
        services.AddSingleton(routeOptimization);
        services.AddSingleton(geocoding);
        services.AddSingleton(coordinateWriter ?? Substitute.For<IAddressCoordinateWriter>());
        services.AddSingleton(settingsReader);
        services.AddSingleton(countryResolver);
        services.AddSingleton(directionsBuilder);
        services.AddContainerAutofillServices();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        return (provider.CreateScope().ServiceProvider.GetRequiredService<IContainerAutofillService>(), provider);
    }

    public static Client ClientAt(string name, double position)
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
