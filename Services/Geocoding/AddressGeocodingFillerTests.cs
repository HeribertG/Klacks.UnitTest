// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests AddressGeocodingFiller: one address is geocoded to the centroid of its postcode and city, asked at
/// Nominatim only when the persistent locality cache has no usable answer (a not-found answer is reused until its
/// retry window ends); the street is never sent. Hits are written through the conditional fill with source and
/// attribution, the state only when the address has none; misses and an unreachable geocoder are recorded as
/// attempts (the latter never cached); a switched-off geocoder touches nothing; a fill the address no longer
/// matches is reported as superseded and not recorded.
/// </summary>

using Klacks.Api.Application.DTOs.Geocoding;
using Klacks.Api.Application.Interfaces.Geocoding;
using Klacks.Api.Application.Services.Geocoding;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Models.Geocoding;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Domain.Services.Geocoding;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Geocoding;

[TestFixture]
public class AddressGeocodingFillerTests
{
    private const string CountryName = "Schweiz";
    private static readonly DateTime Now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly GeocodingLocalityKey ZurichKey = new("CH", "8001", "zürich");

    private IGeocodingService _geocoder = null!;
    private IGeocodedLocalityRepository _localities = null!;
    private IAddressGeocodingStateRepository _states = null!;
    private ICountryResolver _countries = null!;
    private AddressGeocodingFiller _filler = null!;

    [SetUp]
    public void SetUp()
    {
        _geocoder = Substitute.For<IGeocodingService>();
        _geocoder.IsEnabled.Returns(true);
        _localities = Substitute.For<IGeocodedLocalityRepository>();
        _states = Substitute.For<IAddressGeocodingStateRepository>();
        _states.TryFillAsync(Arg.Any<AddressGeocodingFill>(), Arg.Any<CancellationToken>()).Returns(true);
        _countries = Substitute.For<ICountryResolver>();
        _countries.ResolveAsync("CH", Arg.Any<CancellationToken>()).Returns(Switzerland());

        var stateRepository = Substitute.For<IStateRepository>();
        stateRepository.List().Returns([new State { Abbreviation = "ZH", CountryPrefix = "CH", Name = new MultiLanguage { De = "Zürich" } }]);

        _filler = new AddressGeocodingFiller(
            _geocoder, _localities, _states, _countries, new StateAbbreviationResolver(stateRepository),
            new SettableTimeProvider(Now), NullLogger<AddressGeocodingFiller>.Instance);
    }

    [Test]
    public async Task FillAsync_CacheMiss_AsksForPostcodeAndCityOnly_CachesTheLocalityAndFills()
    {
        // Arrange
        _geocoder.ValidateExactAddressAsync(null, "8001", "Zürich", CountryName)
            .Returns(new GeocodingValidationResult { Found = true, Latitude = 47.37, Longitude = 8.54, State = "Zürich" });
        var attribution = new AddressGeocodingAttribution(AddressGeocodingTrigger.Assistant, Guid.NewGuid());

        // Act
        var result = await _filler.FillAsync(Candidate(), attribution);

        // Assert
        result.ShouldBe(new AddressGeocodingFillResult(
            AddressGeocodingFillStatus.Filled, true, 47.37, 8.54, AddressGeocodingSource.NominatimLocality));
        await _geocoder.Received(1).ValidateExactAddressAsync(null, "8001", "Zürich", CountryName);
        await _localities.Received(1).StoreAsync(
            Arg.Is<GeocodedLocality>(l => l.CountryCode == "CH" && l.PostalCode == "8001" && l.City == "zürich"
                && l.Found && l.Latitude == 47.37 && l.Longitude == 8.54 && l.State == "Zürich" && l.ResolvedAt == Now),
            Arg.Any<CancellationToken>());
        await _states.Received(1).TryFillAsync(
            Arg.Is<AddressGeocodingFill>(f => f.ExpectedZip == "8001" && f.ExpectedCity == "Zürich" && f.ExpectedCountry == "CH"
                && f.Latitude == 47.37 && f.Longitude == 8.54 && f.StateIfEmpty == "ZH"
                && f.Source == AddressGeocodingSource.NominatimLocality && f.Attribution == attribution && f.At == Now),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FillAsync_CachedLocality_FillsWithoutAskingTheGeocoder()
    {
        // Arrange
        _localities.GetAsync(ZurichKey, Arg.Any<CancellationToken>()).Returns(Locality(found: true, Now.AddDays(-400)));

        // Act
        var result = await _filler.FillAsync(Candidate(), AddressGeocodingAttribution.SystemScan);

        // Assert
        result.ShouldBe(new AddressGeocodingFillResult(
            AddressGeocodingFillStatus.Filled, false, 47.37, 8.54, AddressGeocodingSource.LocalityCache));
        await _geocoder.DidNotReceiveWithAnyArgs().ValidateExactAddressAsync(default, default!, default!, default!);
        await _localities.DidNotReceiveWithAnyArgs().StoreAsync(default!, default);
        await _states.Received(1).TryFillAsync(
            Arg.Is<AddressGeocodingFill>(f => f.Source == AddressGeocodingSource.LocalityCache && f.StateIfEmpty == "ZH"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FillAsync_RecentNotFoundInCache_ReportsNotFoundWithoutAskingAndRecordsTheAttempt()
    {
        // Arrange
        _localities.GetAsync(ZurichKey, Arg.Any<CancellationToken>()).Returns(Locality(found: false, Now.AddDays(-1)));
        var candidate = Candidate();

        // Act
        var result = await _filler.FillAsync(candidate, AddressGeocodingAttribution.SystemScan);

        // Assert
        result.ShouldBe(new AddressGeocodingFillResult(AddressGeocodingFillStatus.NotFound, false));
        await _geocoder.DidNotReceiveWithAnyArgs().ValidateExactAddressAsync(default, default!, default!, default!);
        await _states.Received(1).RecordAttemptAsync(candidate, AddressGeocodingOutcome.NotFound, Now, Arg.Any<CancellationToken>());
        await _states.DidNotReceiveWithAnyArgs().TryFillAsync(default!, default);
    }

    [Test]
    public async Task FillAsync_ExpiredNotFoundInCache_AsksTheGeocoderAgain()
    {
        // Arrange
        _localities.GetAsync(ZurichKey, Arg.Any<CancellationToken>())
            .Returns(Locality(found: false, Now.AddDays(-AddressGeocodingLimits.NotFoundRetryDays - 1)));
        _geocoder.ValidateExactAddressAsync(null, "8001", "Zürich", CountryName)
            .Returns(new GeocodingValidationResult { Found = true, Latitude = 47.37, Longitude = 8.54 });

        // Act
        var result = await _filler.FillAsync(Candidate(), AddressGeocodingAttribution.SystemScan);

        // Assert
        result.Status.ShouldBe(AddressGeocodingFillStatus.Filled);
        result.UsedGeocoder.ShouldBeTrue();
    }

    [Test]
    public async Task FillAsync_GeocoderFindsNothing_CachesTheMissAndRecordsNotFound()
    {
        // Arrange
        _geocoder.ValidateExactAddressAsync(null, "8001", "Zürich", CountryName)
            .Returns(new GeocodingValidationResult { Found = false, MatchType = "not_found" });
        var candidate = Candidate();

        // Act
        var result = await _filler.FillAsync(candidate, AddressGeocodingAttribution.SystemScan);

        // Assert
        result.ShouldBe(new AddressGeocodingFillResult(AddressGeocodingFillStatus.NotFound, true));
        await _localities.Received(1).StoreAsync(
            Arg.Is<GeocodedLocality>(l => !l.Found && l.Latitude == null && l.ResolvedAt == Now), Arg.Any<CancellationToken>());
        await _states.Received(1).RecordAttemptAsync(candidate, AddressGeocodingOutcome.NotFound, Now, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FillAsync_GeocoderUnreachable_RecordsUnavailableAndCachesNothing()
    {
        // Arrange
        _geocoder.ValidateExactAddressAsync(null, "8001", "Zürich", CountryName)
            .Returns(new GeocodingValidationResult { Found = false, ServiceUnavailable = true });
        var candidate = Candidate();

        // Act
        var result = await _filler.FillAsync(candidate, AddressGeocodingAttribution.SystemScan);

        // Assert
        result.ShouldBe(new AddressGeocodingFillResult(AddressGeocodingFillStatus.Unavailable, true));
        await _localities.DidNotReceiveWithAnyArgs().StoreAsync(default!, default);
        await _states.Received(1).RecordAttemptAsync(candidate, AddressGeocodingOutcome.Unavailable, Now, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FillAsync_GeocodingSwitchedOff_TouchesNeitherCacheNorAddress()
    {
        // Arrange
        _geocoder.IsEnabled.Returns(false);

        // Act
        var result = await _filler.FillAsync(Candidate(), AddressGeocodingAttribution.SystemScan);

        // Assert
        result.ShouldBe(new AddressGeocodingFillResult(AddressGeocodingFillStatus.Disabled, false));
        await _localities.DidNotReceiveWithAnyArgs().GetAsync(default!, default);
        await _states.DidNotReceiveWithAnyArgs().RecordAttemptAsync(default!, default, default, default);
        await _states.DidNotReceiveWithAnyArgs().TryFillAsync(default!, default);
    }

    [Test]
    public async Task FillAsync_GeocoderReportsDisabled_RecordsNothing()
    {
        // Arrange
        _geocoder.ValidateExactAddressAsync(null, "8001", "Zürich", CountryName)
            .Returns(new GeocodingValidationResult { Found = false, Disabled = true });

        // Act
        var result = await _filler.FillAsync(Candidate(), AddressGeocodingAttribution.SystemScan);

        // Assert
        result.Status.ShouldBe(AddressGeocodingFillStatus.Disabled);
        await _localities.DidNotReceiveWithAnyArgs().StoreAsync(default!, default);
        await _states.DidNotReceiveWithAnyArgs().RecordAttemptAsync(default!, default, default, default);
    }

    [Test]
    public async Task FillAsync_AddressWithState_LeavesTheStateAlone()
    {
        // Arrange
        _localities.GetAsync(ZurichKey, Arg.Any<CancellationToken>()).Returns(Locality(found: true, Now));

        // Act
        await _filler.FillAsync(Candidate(state: "SZ"), AddressGeocodingAttribution.SystemScan);

        // Assert
        await _states.Received(1).TryFillAsync(Arg.Is<AddressGeocodingFill>(f => f.StateIfEmpty == null), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FillAsync_AddressChangedInTheMeantime_ReportsSupersededAndRecordsNothing()
    {
        // Arrange
        _localities.GetAsync(ZurichKey, Arg.Any<CancellationToken>()).Returns(Locality(found: true, Now));
        _states.TryFillAsync(Arg.Any<AddressGeocodingFill>(), Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var result = await _filler.FillAsync(Candidate(), AddressGeocodingAttribution.SystemScan);

        // Assert
        result.Status.ShouldBe(AddressGeocodingFillStatus.Superseded);
        await _states.DidNotReceiveWithAnyArgs().RecordAttemptAsync(default!, default, default, default);
    }

    [Test]
    public async Task FillAsync_UnknownCountryCode_UsesTheDefaultCountry()
    {
        // Arrange
        _countries.ResolveAsync("??", Arg.Any<CancellationToken>()).Returns((Countries?)null);
        _countries.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns(Switzerland());
        _localities.GetAsync(ZurichKey, Arg.Any<CancellationToken>()).Returns(Locality(found: true, Now));

        // Act
        var result = await _filler.FillAsync(Candidate(country: "??"), AddressGeocodingAttribution.SystemScan);

        // Assert
        result.Status.ShouldBe(AddressGeocodingFillStatus.Filled);
        await _states.Received(1).TryFillAsync(Arg.Is<AddressGeocodingFill>(f => f.ExpectedCountry == "??"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FillAsync_PostcodeBlankAfterNormalization_IsRecordedAsNotFoundWithoutAsking()
    {
        // Arrange
        var candidate = Candidate() with { Zip = "	 " };

        // Act
        var result = await _filler.FillAsync(candidate, AddressGeocodingAttribution.SystemScan);

        // Assert
        result.ShouldBe(new AddressGeocodingFillResult(AddressGeocodingFillStatus.NotFound, false));
        await _geocoder.DidNotReceiveWithAnyArgs().ValidateExactAddressAsync(default, default!, default!, default!);
        await _states.Received(1).RecordAttemptAsync(candidate, AddressGeocodingOutcome.NotFound, Now, Arg.Any<CancellationToken>());
    }

    private static AddressGeocodingCandidate Candidate(string country = "CH", string state = "") =>
        new(Guid.NewGuid(), Guid.NewGuid(), EntityTypeEnum.Customer, "Muster AG", "8001", "Zürich", country, state);

    private static GeocodedLocality Locality(bool found, DateTime resolvedAt) => new()
    {
        CountryCode = "CH",
        PostalCode = "8001",
        City = "zürich",
        Found = found,
        Latitude = found ? 47.37 : null,
        Longitude = found ? 8.54 : null,
        State = found ? "Zürich" : null,
        ResolvedAt = resolvedAt
    };

    private static Countries Switzerland() => new()
    {
        Abbreviation = "CH",
        Name = new MultiLanguage { De = CountryName, En = "Switzerland" }
    };
}
