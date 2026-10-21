// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests the geocoding country of RouteOptimizationService: template addresses without coordinates are geocoded with
/// the name of their country (company default country when the address has none) and the bases with the company
/// default country, never with a hard-coded country.
/// </summary>

using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Services.RouteOptimization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.RouteOptimization;

[TestFixture]
public class RouteOptimizationServiceGeocodingCountryTests
{
    private const string AustriaCode = "AT";
    private const string AustriaQueryName = "Österreich";
    private const string GermanyCode = "DE";
    private const string GermanyQueryName = "Deutschland";
    private const int Weekday = 1;

    private IContainerTemplateRepository _containerRepository = null!;
    private IGeocodingService _geocoding = null!;
    private IDistanceMatrixBuilder _distanceMatrixBuilder = null!;
    private ICountryResolver _countryResolver = null!;

    [SetUp]
    public void SetUp()
    {
        _containerRepository = Substitute.For<IContainerTemplateRepository>();
        _geocoding = Substitute.For<IGeocodingService>();
        _distanceMatrixBuilder = Substitute.For<IDistanceMatrixBuilder>();
        _countryResolver = Substitute.For<ICountryResolver>();

        _containerRepository.GetTemplatesForContainer(Arg.Any<Guid>()).Returns(new List<ContainerTemplate>());
        _geocoding
            .GeocodeAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(((double?)null, (double?)null));
        _distanceMatrixBuilder
            .BuildDistanceMatrixAsync(Arg.Any<List<Location>>(), Arg.Any<ContainerTransportMode>())
            .Returns(call => new DistanceMatrix(call.Arg<List<Location>>(), new double[0, 0], new double[0, 0]));
        _countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns(Austria());
        _countryResolver.ResolveAsync(GermanyCode, Arg.Any<CancellationToken>()).Returns(Germany());
    }

    [Test]
    public async Task TemplateAddresses_AreGeocodedWithTheirCountryName_OrTheCompanyDefaultCountry()
    {
        // Arrange
        var template = new ContainerTemplate
        {
            Weekday = Weekday,
            IsHoliday = false,
            ContainerTemplateItems =
            [
                CreateItemWithoutCoordinates("A", country: string.Empty),
                CreateItemWithoutCoordinates("B", country: GermanyCode)
            ]
        };
        _containerRepository.GetTemplatesForContainer(Arg.Any<Guid>()).Returns([template]);

        // Act
        await CreateSut().CalculateDistanceMatrixAsync(Guid.NewGuid(), Weekday, false);

        // Assert
        await _geocoding.Received(1).GeocodeAddressAsync("A-Street 1, 1010 A-City", AustriaQueryName, Arg.Any<CancellationToken>());
        await _geocoding.Received(1).GeocodeAddressAsync("B-Street 1, 1010 B-City", GermanyQueryName, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BaseAddress_IsGeocodedWithTheCompanyDefaultCountry()
    {
        // Act
        await CreateSut().OptimizeRouteAsync(Guid.NewGuid(), Weekday, false, startBase: "Ringstrasse 1, 1010 Wien");

        // Assert
        await _geocoding.Received(1).GeocodeAddressAsync("Ringstrasse 1, 1010 Wien", AustriaQueryName, Arg.Any<CancellationToken>());
    }

    private IRouteOptimizationService CreateSut()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_containerRepository);
        services.AddSingleton(Substitute.For<IBranchRepository>());
        services.AddSingleton(Substitute.For<IShiftRepository>());
        services.AddSingleton(_geocoding);
        services.AddSingleton(_distanceMatrixBuilder);
        services.AddSingleton(Substitute.For<IRouteDirectionsBuilder>());
        services.AddSingleton(_countryResolver);
        services.AddSingleton<ILogger<RouteOptimizationService>>(NullLogger<RouteOptimizationService>.Instance);
        var provider = services.BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<RouteOptimizationService>(provider);
    }

    private static Countries Austria() => new()
    {
        Id = Guid.NewGuid(),
        Abbreviation = AustriaCode,
        Prefix = "+43",
        Name = new MultiLanguage { De = AustriaQueryName, En = "Austria" }
    };

    private static Countries Germany() => new()
    {
        Id = Guid.NewGuid(),
        Abbreviation = GermanyCode,
        Prefix = "+49",
        Name = new MultiLanguage { De = GermanyQueryName, En = "Germany" }
    };

    private static ContainerTemplateItem CreateItemWithoutCoordinates(string name, string country)
    {
        var shiftId = Guid.NewGuid();
        return new ContainerTemplateItem
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            Shift = new Shift
            {
                Id = shiftId,
                Name = name,
                WorkTime = 0.5m,
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
                            Zip = "1010",
                            City = $"{name}-City",
                            Country = country
                        }
                    ]
                }
            }
        };
    }
}
