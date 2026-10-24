// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests AddressGeocodingBackfillRunner, one pass of the background geocoding: it asks for due addresses with the
/// privacy switch and the two retry windows, attributes a fill to the pending request or the system scan, pauses
/// only after real geocoder requests, stops at an unreachable geocoder and reports whether a full batch went through.
/// </summary>

using Klacks.Api.Application.DTOs.Geocoding;
using Klacks.Api.Application.Interfaces.Geocoding;
using Klacks.Api.Application.Services.Geocoding;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Geocoding;

[TestFixture]
public class AddressGeocodingBackfillRunnerTests
{
    private static readonly DateTime Now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    private IAddressGeocodingStateRepository _states = null!;
    private IAddressGeocodingFiller _filler = null!;
    private IAddressGeocodingSettings _settings = null!;
    private IGeocodingService _geocoder = null!;
    private int _pauses;

    [SetUp]
    public void SetUp()
    {
        _states = Substitute.For<IAddressGeocodingStateRepository>();
        _filler = Substitute.For<IAddressGeocodingFiller>();
        _settings = Substitute.For<IAddressGeocodingSettings>();
        _geocoder = Substitute.For<IGeocodingService>();
        _geocoder.IsEnabled.Returns(true);
        _pauses = 0;
    }

    [Test]
    public async Task RunBatchAsync_QueriesDueAddressesWithPrivacySwitchAndRetryWindows()
    {
        // Arrange
        _settings.AreEmployeeAddressesAllowedAsync(Arg.Any<CancellationToken>()).Returns(true);
        Due();

        // Act
        await Runner().RunBatchAsync(CancellationToken.None);

        // Assert
        await _states.Received(1).GetDueCandidatesAsync(
            new AddressGeocodingDueQuery(
                true,
                Now.AddDays(-AddressGeocodingLimits.NotFoundRetryDays),
                Now.AddMinutes(-AddressGeocodingLimits.UnavailableRetryMinutes),
                AddressGeocodingLimits.ScanBatchSize),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunBatchAsync_AttributesFillsToThePendingRequestOrTheScan_PausesOnlyAfterGeocoderCalls()
    {
        // Arrange
        var request = new AddressGeocodingAttribution(AddressGeocodingTrigger.Assistant, Guid.NewGuid());
        var requested = Candidate() with { PendingRequest = request };
        var scanned = Candidate();
        Due(requested, scanned);
        _filler.FillAsync(requested, request, Arg.Any<CancellationToken>())
            .Returns(new AddressGeocodingFillResult(AddressGeocodingFillStatus.Filled, true, 1, 2, AddressGeocodingSource.NominatimLocality));
        _filler.FillAsync(scanned, AddressGeocodingAttribution.SystemScan, Arg.Any<CancellationToken>())
            .Returns(new AddressGeocodingFillResult(AddressGeocodingFillStatus.Filled, false, 1, 2, AddressGeocodingSource.LocalityCache));

        // Act
        var more = await Runner().RunBatchAsync(CancellationToken.None);

        // Assert
        more.ShouldBeFalse();
        _pauses.ShouldBe(1);
        await _filler.Received(1).FillAsync(requested, request, Arg.Any<CancellationToken>());
        await _filler.Received(1).FillAsync(scanned, AddressGeocodingAttribution.SystemScan, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunBatchAsync_GeocoderUnreachable_StopsThePass()
    {
        // Arrange
        var first = Candidate();
        var second = Candidate();
        Due(first, second);
        _filler.FillAsync(first, Arg.Any<AddressGeocodingAttribution>(), Arg.Any<CancellationToken>())
            .Returns(new AddressGeocodingFillResult(AddressGeocodingFillStatus.Unavailable, true));

        // Act
        var more = await Runner().RunBatchAsync(CancellationToken.None);

        // Assert
        more.ShouldBeFalse();
        await _filler.DidNotReceive().FillAsync(second, Arg.Any<AddressGeocodingAttribution>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunBatchAsync_FullBatch_ReportsMoreDue()
    {
        // Arrange
        Due(Enumerable.Range(0, AddressGeocodingLimits.ScanBatchSize).Select(_ => Candidate()).ToArray());
        _filler.FillAsync(Arg.Any<AddressGeocodingCandidate>(), Arg.Any<AddressGeocodingAttribution>(), Arg.Any<CancellationToken>())
            .Returns(new AddressGeocodingFillResult(AddressGeocodingFillStatus.NotFound, false));

        // Act
        var more = await Runner().RunBatchAsync(CancellationToken.None);

        // Assert
        more.ShouldBeTrue();
        _pauses.ShouldBe(0);
    }

    [Test]
    public async Task RunBatchAsync_GeocodingSwitchedOff_ReadsNothing()
    {
        // Arrange
        _geocoder.IsEnabled.Returns(false);

        // Act
        var more = await Runner().RunBatchAsync(CancellationToken.None);

        // Assert
        more.ShouldBeFalse();
        await _states.DidNotReceiveWithAnyArgs().GetDueCandidatesAsync(default!, default);
    }

    private void Due(params AddressGeocodingCandidate[] candidates) =>
        _states.GetDueCandidatesAsync(Arg.Any<AddressGeocodingDueQuery>(), Arg.Any<CancellationToken>()).Returns(candidates.ToList());

    private AddressGeocodingBackfillRunner Runner() => new(
        _states, _filler, _settings, _geocoder, new SettableTimeProvider(Now),
        NullLogger<AddressGeocodingBackfillRunner>.Instance,
        _ =>
        {
            _pauses++;
            return Task.CompletedTask;
        });

    private static AddressGeocodingCandidate Candidate() =>
        new(Guid.NewGuid(), Guid.NewGuid(), EntityTypeEnum.Customer, "Muster AG", "8001", "Zürich", "CH", string.Empty);
}
