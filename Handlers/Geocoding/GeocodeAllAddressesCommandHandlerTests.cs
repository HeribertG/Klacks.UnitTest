// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests GeocodeAllAddressesCommandHandler (admin batch): a disabled geocoder is reported without reading
/// addresses; otherwise every address without coordinates goes through the geocoding selection (privacy rule,
/// completeness) and only the eligible ones are geocoded, unlimited, attributed to the admin batch; the counts are
/// eligible total, filled and the rest.
/// </summary>

using Klacks.Api.Application.Commands.Geocoding;
using Klacks.Api.Application.DTOs.Geocoding;
using Klacks.Api.Application.Handlers.Geocoding;
using Klacks.Api.Application.Interfaces.Geocoding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Handlers.Geocoding;

[TestFixture]
public class GeocodeAllAddressesCommandHandlerTests
{
    private IAddressGeocodingStateRepository _states = null!;
    private IAddressGeocodingService _geocoding = null!;
    private GeocodeAllAddressesCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _states = Substitute.For<IAddressGeocodingStateRepository>();
        _geocoding = Substitute.For<IAddressGeocodingService>();
        _geocoding.IsGeocodingEnabled.Returns(true);
        _handler = new GeocodeAllAddressesCommandHandler(_states, _geocoding, NullLogger<GeocodeAllAddressesCommandHandler>.Instance);
    }

    [Test]
    public async Task Handle_GeocodingDisabled_ReportsDisabledAndTouchesNothing()
    {
        // Arrange
        _geocoding.IsGeocodingEnabled.Returns(false);

        // Act
        var result = await _handler.Handle(new GeocodeAllAddressesCommand(), CancellationToken.None);

        // Assert
        result.ShouldBe(GeocodeAllAddressesResult.Disabled);
        await _states.DidNotReceiveWithAnyArgs().GetMissingCoordinatesAsync(default, default);
        await _geocoding.DidNotReceiveWithAnyArgs().GeocodeAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task Handle_GeocodesOnlyEligibleAddressesAndCountsFilledAndRest()
    {
        // Arrange
        var hit = Candidate(EntityTypeEnum.Customer);
        var miss = Candidate(EntityTypeEnum.Customer);
        var employee = Candidate(EntityTypeEnum.Employee);
        _states.GetMissingCoordinatesAsync(null, Arg.Any<CancellationToken>()).Returns([hit, miss, employee]);
        _geocoding.SelectAsync(Arg.Any<IReadOnlyList<AddressGeocodingCandidate>>(), Arg.Any<CancellationToken>())
            .Returns(new AddressGeocodingSelection([hit, miss], [employee], []));
        _geocoding.GeocodeAsync(
                Arg.Any<IReadOnlyList<AddressGeocodingCandidate>>(), Arg.Any<AddressGeocodingAttribution>(),
                Arg.Any<AddressGeocodingBudget>(), Arg.Any<CancellationToken>())
            .Returns(new AddressGeocodingRunSummary(
                false, [new AddressGeocodingFilledAddress(hit, 47.37, 8.54, AddressGeocodingSource.NominatimLocality)], 1, 0, 0, 0));

        // Act
        var result = await _handler.Handle(new GeocodeAllAddressesCommand(), CancellationToken.None);

        // Assert
        result.ShouldBe(new GeocodeAllAddressesResult(false, 2, 1, 1));
        await _geocoding.Received(1).GeocodeAsync(
            Arg.Is<IReadOnlyList<AddressGeocodingCandidate>>(list => list.SequenceEqual(new[] { hit, miss })),
            new AddressGeocodingAttribution(AddressGeocodingTrigger.AdminBatch, null),
            AddressGeocodingBudget.Unlimited,
            Arg.Any<CancellationToken>());
    }

    private static AddressGeocodingCandidate Candidate(EntityTypeEnum clientType) =>
        new(Guid.NewGuid(), Guid.NewGuid(), clientType, "Muster", "8001", "Zürich", "CH", string.Empty);
}
