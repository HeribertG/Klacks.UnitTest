// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests ContainerAutofillCandidateLocator: freshly geocoded coordinates are written back one after another on the
/// request token and before the extraction returns (the scoped DbContext allows no parallel operations), a failed
/// write is logged instead of lost, and the geocoding country comes from the address or the company default country
/// (APP_ADDRESS_COUNTRY), never from a hard-coded country.
/// </summary>

using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Services.RouteOptimization.ContainerAutofill;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.RouteOptimization;

[TestFixture]
public class ContainerAutofillCandidateLocatorTests
{
    private const string AustriaCode = "AT";
    private const string AustriaQueryName = "Österreich";

    private IGeocodingService _geocoding = null!;
    private IAddressCoordinateWriter _coordinateWriter = null!;
    private ICountryResolver _countryResolver = null!;
    private RecordingLogger<ContainerAutofillCandidateLocator> _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _geocoding = Substitute.For<IGeocodingService>();
        _coordinateWriter = Substitute.For<IAddressCoordinateWriter>();
        _countryResolver = Substitute.For<ICountryResolver>();
        _logger = new RecordingLogger<ContainerAutofillCandidateLocator>();

        _geocoding
            .GeocodeAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(((double?)47.0, (double?)8.0));
    }

    [Test]
    public async Task ExtractLocations_WaitsForTheCoordinateWrites_BeforeReturning()
    {
        // Arrange
        var pendingWrite = new TaskCompletionSource();
        _coordinateWriter
            .UpdateCoordinatesAsync(Arg.Any<Guid>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(pendingWrite.Task);
        var shifts = new List<Shift> { CreateShiftWithoutCoordinates("A") };

        // Act
        var extraction = CreateSut().ExtractLocationsFromShiftsAsync(shifts, ContainerTransportMode.ByCar, CancellationToken.None);

        // Assert
        extraction.IsCompleted.ShouldBeFalse();
        pendingWrite.SetResult();
        var locations = await extraction;
        locations.Count.ShouldBe(1);
    }

    [Test]
    public async Task ExtractLocations_WritesCoordinatesOneAfterAnother()
    {
        // Arrange
        var active = 0;
        var maxActive = 0;
        async Task TrackedWriteAsync()
        {
            var now = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref active);
        }

        _coordinateWriter
            .UpdateCoordinatesAsync(Arg.Any<Guid>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(_ => TrackedWriteAsync());
        var shifts = new List<Shift> { CreateShiftWithoutCoordinates("A"), CreateShiftWithoutCoordinates("B"), CreateShiftWithoutCoordinates("C") };

        // Act
        await CreateSut().ExtractLocationsFromShiftsAsync(shifts, ContainerTransportMode.ByCar, CancellationToken.None);

        // Assert
        maxActive.ShouldBe(1);
        await _coordinateWriter.ReceivedWithAnyArgs(3).UpdateCoordinatesAsync(default, default, default, default);
    }

    [Test]
    public async Task ExtractLocations_PassesTheRequestTokenToTheCoordinateWriter()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var shift = CreateShiftWithoutCoordinates("A");
        var addressId = shift.Client!.Addresses.First().Id;

        // Act
        await CreateSut().ExtractLocationsFromShiftsAsync([shift], ContainerTransportMode.ByCar, cancellation.Token);

        // Assert
        await _coordinateWriter.Received(1).UpdateCoordinatesAsync(addressId, 47.0, 8.0, cancellation.Token);
    }

    [Test]
    public async Task ExtractLocations_FailedWrite_IsLoggedAndTheOtherWritesAndLocationsStay()
    {
        // Arrange
        var failing = CreateShiftWithoutCoordinates("A");
        var succeeding = CreateShiftWithoutCoordinates("B");
        var failingAddressId = failing.Client!.Addresses.First().Id;
        var succeedingAddressId = succeeding.Client!.Addresses.First().Id;
        _coordinateWriter
            .UpdateCoordinatesAsync(failingAddressId, Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("write failed")));

        // Act
        var locations = await CreateSut().ExtractLocationsFromShiftsAsync(
            [failing, succeeding], ContainerTransportMode.ByCar, CancellationToken.None);

        // Assert
        locations.Count.ShouldBe(2);
        await _coordinateWriter.Received(1).UpdateCoordinatesAsync(
            succeedingAddressId, Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Exception is InvalidOperationException);
    }

    [Test]
    public async Task ExtractLocations_AddressWithoutCountry_IsGeocodedWithTheCompanyDefaultCountry()
    {
        // Arrange
        _countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns(Austria());
        var shift = CreateShiftWithoutCoordinates("A");

        // Act
        await CreateSut().ExtractLocationsFromShiftsAsync([shift], ContainerTransportMode.ByCar, CancellationToken.None);

        // Assert
        await _geocoding.Received(1).GeocodeAddressAsync("A-Street 1, 1010 A-City", AustriaQueryName, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExtractLocations_CountryCode_IsGeocodedWithItsCountryName_ResolvedOncePerCode()
    {
        // Arrange
        _countryResolver.ResolveAsync(AustriaCode, Arg.Any<CancellationToken>()).Returns(Austria());
        var first = CreateShiftWithoutCoordinates("A", AustriaCode);
        var second = CreateShiftWithoutCoordinates("B", AustriaCode);

        // Act
        await CreateSut().ExtractLocationsFromShiftsAsync([first, second], ContainerTransportMode.ByCar, CancellationToken.None);

        // Assert
        await _geocoding.Received(1).GeocodeAddressAsync("A-Street 1, 1010 A-City", AustriaQueryName, Arg.Any<CancellationToken>());
        await _geocoding.Received(1).GeocodeAddressAsync("B-Street 1, 1010 B-City", AustriaQueryName, Arg.Any<CancellationToken>());
        await _countryResolver.Received(1).ResolveAsync(AustriaCode, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExtractLocations_NoCountryAndNoDefaultCountry_IsGeocodedWithoutCountry()
    {
        // Arrange
        var shift = CreateShiftWithoutCoordinates("A");

        // Act
        await CreateSut().ExtractLocationsFromShiftsAsync([shift], ContainerTransportMode.ByCar, CancellationToken.None);

        // Assert
        await _geocoding.Received(1).GeocodeAddressAsync("A-Street 1, 1010 A-City", string.Empty, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GeocodeBaseAddress_UsesTheCompanyDefaultCountry()
    {
        // Arrange
        _countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns(Austria());

        // Act
        var location = await CreateSut().GeocodeBaseAddressAsync("Ringstrasse 1, 1010 Wien", "StartBase", CancellationToken.None);

        // Assert
        location.ShouldNotBeNull();
        await _geocoding.Received(1).GeocodeAddressAsync("Ringstrasse 1, 1010 Wien", AustriaQueryName, Arg.Any<CancellationToken>());
    }

    private ContainerAutofillCandidateLocator CreateSut()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_geocoding);
        services.AddSingleton(_coordinateWriter);
        services.AddSingleton(_countryResolver);
        services.AddSingleton<ILogger<ContainerAutofillCandidateLocator>>(_logger);
        var provider = services.BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<ContainerAutofillCandidateLocator>(provider);
    }

    private static Countries Austria() => new()
    {
        Id = Guid.NewGuid(),
        Abbreviation = AustriaCode,
        Prefix = "+43",
        Name = new MultiLanguage { De = AustriaQueryName, En = "Austria" }
    };

    private static Shift CreateShiftWithoutCoordinates(string name, string country = "")
    {
        return new Shift
        {
            Id = Guid.NewGuid(),
            Name = name,
            IsTimeRange = true,
            WorkTime = 0.5m,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(12, 0),
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
        };
    }
}
