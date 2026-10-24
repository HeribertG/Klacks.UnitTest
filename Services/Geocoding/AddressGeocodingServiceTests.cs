// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests AddressGeocodingService, the system function behind Klacksy, the import and the admin batch: the selection
/// splits incomplete, privacy-blocked (employee addresses while the setting is off) and eligible addresses; a run
/// fills within its budget, hands the rest to the backfill and stops at an unreachable geocoder; a request writes
/// only eligible, still coordinate-less addresses, attributes them to the signed-in user when the trigger names
/// none, and writes nothing while geocoding or the backfill is off.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Application.DTOs.Geocoding;
using Klacks.Api.Application.Interfaces.Geocoding;
using Klacks.Api.Application.Services.Geocoding;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Geocoding;

[TestFixture]
public class AddressGeocodingServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    private IAddressGeocodingFiller _filler = null!;
    private IAddressGeocodingStateRepository _states = null!;
    private IAddressGeocodingSettings _settings = null!;
    private IGeocodingService _geocoder = null!;
    private IHttpContextAccessor _http = null!;

    [SetUp]
    public void SetUp()
    {
        _filler = Substitute.For<IAddressGeocodingFiller>();
        _states = Substitute.For<IAddressGeocodingStateRepository>();
        _states.RequestAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<AddressGeocodingAttribution>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyCollection<Guid>>().Count);
        _settings = Substitute.For<IAddressGeocodingSettings>();
        _geocoder = Substitute.For<IGeocodingService>();
        _geocoder.IsEnabled.Returns(true);
        _http = Substitute.For<IHttpContextAccessor>();
    }

    [Test]
    public async Task SelectAsync_SettingOff_BlocksEmployeeAndExternalEmployeeAddresses()
    {
        // Arrange
        var customer = Candidate(EntityTypeEnum.Customer);
        var employee = Candidate(EntityTypeEnum.Employee);
        var external = Candidate(EntityTypeEnum.ExternEmp);
        var incomplete = Candidate(EntityTypeEnum.Customer, zip: " ");

        // Act
        var selection = await Service().SelectAsync([customer, employee, external, incomplete]);

        // Assert
        selection.Eligible.ShouldBe([customer]);
        selection.PrivacyBlocked.ShouldBe([employee, external]);
        selection.Incomplete.ShouldBe([incomplete]);
    }

    [Test]
    public async Task SelectAsync_SettingOn_EmployeeAddressesAreEligible()
    {
        // Arrange
        _settings.AreEmployeeAddressesAllowedAsync(Arg.Any<CancellationToken>()).Returns(true);
        var employee = Candidate(EntityTypeEnum.Employee);

        // Act
        var selection = await Service().SelectAsync([employee]);

        // Assert
        selection.Eligible.ShouldBe([employee]);
        selection.PrivacyBlocked.ShouldBeEmpty();
    }

    [Test]
    public async Task GeocodeAsync_GeocoderCallBudgetSpent_QueuesTheRestForTheBackfill()
    {
        // Arrange
        var first = Candidate(EntityTypeEnum.Customer);
        var second = Candidate(EntityTypeEnum.Customer);
        var third = Candidate(EntityTypeEnum.Customer);
        _filler.FillAsync(first, Arg.Any<AddressGeocodingAttribution>(), Arg.Any<CancellationToken>())
            .Returns(Filled(usedGeocoder: true));
        _filler.FillAsync(second, Arg.Any<AddressGeocodingAttribution>(), Arg.Any<CancellationToken>())
            .Returns(new AddressGeocodingFillResult(AddressGeocodingFillStatus.NotFound, true));
        var attribution = new AddressGeocodingAttribution(AddressGeocodingTrigger.Assistant, Guid.NewGuid());

        // Act
        var summary = await Service().GeocodeAsync([first, second, third], attribution, new AddressGeocodingBudget(2, 20, true));

        // Assert
        summary.Filled.Select(f => f.Address).ShouldBe([first]);
        summary.Filled[0].Source.ShouldBe(AddressGeocodingSource.NominatimLocality);
        summary.NotFound.ShouldBe(1);
        summary.Queued.ShouldBe(1);
        await _filler.DidNotReceive().FillAsync(third, Arg.Any<AddressGeocodingAttribution>(), Arg.Any<CancellationToken>());
        await _states.Received(1).RequestAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { third.AddressId })), attribution, Now, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GeocodeAsync_CacheHitsDoNotSpendTheGeocoderBudget()
    {
        // Arrange
        var candidates = Enumerable.Range(0, 4).Select(_ => Candidate(EntityTypeEnum.Customer)).ToList();
        _filler.FillAsync(Arg.Any<AddressGeocodingCandidate>(), Arg.Any<AddressGeocodingAttribution>(), Arg.Any<CancellationToken>())
            .Returns(Filled(usedGeocoder: false));

        // Act
        var summary = await Service().GeocodeAsync(candidates, AddressGeocodingAttribution.SystemScan, new AddressGeocodingBudget(1, 20, true));

        // Assert
        summary.Filled.Count.ShouldBe(4);
        summary.Queued.ShouldBe(0);
    }

    [Test]
    public async Task GeocodeAsync_GeocoderUnreachable_StopsAndQueuesTheFailedAndTheRest()
    {
        // Arrange
        var first = Candidate(EntityTypeEnum.Customer);
        var second = Candidate(EntityTypeEnum.Customer);
        _filler.FillAsync(first, Arg.Any<AddressGeocodingAttribution>(), Arg.Any<CancellationToken>())
            .Returns(new AddressGeocodingFillResult(AddressGeocodingFillStatus.Unavailable, true));

        // Act
        var summary = await Service().GeocodeAsync([first, second], AddressGeocodingAttribution.SystemScan, AddressGeocodingBudget.Unlimited);

        // Assert
        summary.Unavailable.ShouldBe(1);
        summary.Queued.ShouldBe(2);
        await _filler.DidNotReceive().FillAsync(second, Arg.Any<AddressGeocodingAttribution>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GeocodeAsync_BackfillNotRunning_ReportsTheRemainderAsDeferred()
    {
        // Arrange
        var first = Candidate(EntityTypeEnum.Customer);
        var second = Candidate(EntityTypeEnum.Customer);
        _filler.FillAsync(first, Arg.Any<AddressGeocodingAttribution>(), Arg.Any<CancellationToken>())
            .Returns(Filled(usedGeocoder: true));

        // Act
        var summary = await Service(backfillActive: false).GeocodeAsync(
            [first, second], AddressGeocodingAttribution.SystemScan, new AddressGeocodingBudget(1, 20, true));

        // Assert
        summary.Queued.ShouldBe(0);
        summary.Deferred.ShouldBe(1);
        await _states.DidNotReceiveWithAnyArgs().RequestAsync(default!, default!, default, default);
    }

    [Test]
    public async Task GeocodeAsync_EmployeeAddressPassedWithoutSelection_IsNeverSent()
    {
        // Arrange
        var employee = Candidate(EntityTypeEnum.Employee);

        // Act
        var summary = await Service().GeocodeAsync([employee], AddressGeocodingAttribution.SystemScan, AddressGeocodingBudget.Unlimited);

        // Assert
        summary.Filled.ShouldBeEmpty();
        await _filler.DidNotReceiveWithAnyArgs().FillAsync(default!, default!, default);
    }

    [Test]
    public async Task GeocodeAsync_HandOverToTheBackfillFails_StillReportsWhatWasWritten()
    {
        // Arrange
        var first = Candidate(EntityTypeEnum.Customer);
        var second = Candidate(EntityTypeEnum.Customer);
        _filler.FillAsync(first, Arg.Any<AddressGeocodingAttribution>(), Arg.Any<CancellationToken>())
            .Returns(Filled(usedGeocoder: true));
        _states.RequestAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<AddressGeocodingAttribution>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new InvalidOperationException("database down"));

        // Act
        var summary = await Service().GeocodeAsync([first, second], AddressGeocodingAttribution.SystemScan, new AddressGeocodingBudget(1, 20, true));

        // Assert
        summary.Filled.Select(f => f.Address).ShouldBe([first]);
        summary.Queued.ShouldBe(0);
        summary.Deferred.ShouldBe(1);
    }

    [Test]
    public async Task GeocodeAsync_GeocodingSwitchedOff_ReportsDisabledAndDoesNothing()
    {
        // Arrange
        _geocoder.IsEnabled.Returns(false);

        // Act
        var summary = await Service().GeocodeAsync([Candidate(EntityTypeEnum.Customer)], AddressGeocodingAttribution.SystemScan, AddressGeocodingBudget.Unlimited);

        // Assert
        summary.GeocodingDisabled.ShouldBeTrue();
        await _filler.DidNotReceiveWithAnyArgs().FillAsync(default!, default!, default);
    }

    [Test]
    public async Task RequestAsync_RequestsOnlyEligibleAddressesForTheSignedInUser()
    {
        // Arrange
        var userId = Guid.NewGuid();
        _http.HttpContext.Returns(new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())]))
        });
        var customer = Candidate(EntityTypeEnum.Customer);
        var employee = Candidate(EntityTypeEnum.Employee);
        _states.GetMissingCoordinatesByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns([customer, employee]);

        // Act
        var requested = await Service().RequestAsync(
            [customer.AddressId, employee.AddressId], new AddressGeocodingAttribution(AddressGeocodingTrigger.Import, null));

        // Assert
        requested.ShouldBe(1);
        await _states.Received(1).RequestAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { customer.AddressId })),
            new AddressGeocodingAttribution(AddressGeocodingTrigger.Import, userId),
            Now,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RequestAsync_BackfillNotRunningOnThisInstance_RequestsNothing()
    {
        // Act
        var requested = await Service(backfillActive: false).RequestAsync(
            [Guid.NewGuid()], new AddressGeocodingAttribution(AddressGeocodingTrigger.Import, null));

        // Assert
        requested.ShouldBe(0);
        await _states.DidNotReceiveWithAnyArgs().GetMissingCoordinatesByIdsAsync(default!, default);
        await _states.DidNotReceiveWithAnyArgs().RequestAsync(default!, default!, default, default);
    }

    [Test]
    public async Task RequestAsync_GeocodingSwitchedOff_RequestsNothing()
    {
        // Arrange
        _geocoder.IsEnabled.Returns(false);

        // Act
        var requested = await Service().RequestAsync([Guid.NewGuid()], new AddressGeocodingAttribution(AddressGeocodingTrigger.Import, null));

        // Assert
        requested.ShouldBe(0);
        await _states.DidNotReceiveWithAnyArgs().RequestAsync(default!, default!, default, default);
    }

    private AddressGeocodingService Service(bool backfillActive = true) => new(
        _filler, _states, _settings, _geocoder, new AddressGeocodingBackfillAvailability(backfillActive), _http,
        new SettableTimeProvider(Now), NullLogger<AddressGeocodingService>.Instance);

    private static AddressGeocodingFillResult Filled(bool usedGeocoder) => new(
        AddressGeocodingFillStatus.Filled, usedGeocoder, 47.37, 8.54,
        usedGeocoder ? AddressGeocodingSource.NominatimLocality : AddressGeocodingSource.LocalityCache);

    private static AddressGeocodingCandidate Candidate(EntityTypeEnum clientType, string zip = "8001") =>
        new(Guid.NewGuid(), Guid.NewGuid(), clientType, "Muster", zip, "Zürich", "CH", string.Empty);
}
