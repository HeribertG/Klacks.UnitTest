// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests GeocodeAddressesSkill: it runs only with geocoding enabled, filters hidden clients out before anything is
/// geocoded, geocodes through the system function as Klacksy for the calling user with the small chat budget, and
/// its answer states what was written without asking, what runs on in the background, what could not be placed and
/// which employee addresses the privacy setting kept out - without naming any skill.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Geocoding;
using Klacks.Api.Application.Interfaces.Geocoding;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GeocodeAddressesSkillTests
{
    private IClientSearchRepository _search = null!;
    private IClientRepository _clients = null!;
    private IClientVisibilityGuard _visibility = null!;
    private IAddressGeocodingStateRepository _states = null!;
    private IAddressGeocodingService _geocoding = null!;

    [SetUp]
    public void SetUp()
    {
        _search = Substitute.For<IClientSearchRepository>();
        _clients = Substitute.For<IClientRepository>();
        _visibility = Substitute.For<IClientVisibilityGuard>();
        _states = Substitute.For<IAddressGeocodingStateRepository>();
        _geocoding = Substitute.For<IAddressGeocodingService>();
        _geocoding.IsGeocodingEnabled.Returns(true);
    }

    [Test]
    public async Task Execute_GeocodingDisabled_AnswersThatAndReadsNothing()
    {
        // Arrange
        _geocoding.IsGeocodingEnabled.Returns(false);

        // Act
        var result = await Skill().ExecuteAsync(Context(), []);

        // Assert
        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(GeoServiceMessages.GeocodingDisabled);
        await _states.DidNotReceiveWithAnyArgs().GetMissingCoordinatesAsync(default, default);
    }

    [Test]
    public async Task Execute_WithoutClient_GeocodesOnlyVisibleAddressesForTheCallingUser()
    {
        // Arrange
        var visible = Candidate("Muster AG");
        var hidden = Candidate("Geheim AG");
        _states.GetMissingCoordinatesAsync(null, Arg.Any<CancellationToken>()).Returns([visible, hidden]);
        _visibility.FilterVisibleAsync(
                Arg.Any<IReadOnlyCollection<AddressGeocodingCandidate>>(), Arg.Any<Func<AddressGeocodingCandidate, Guid>>(), Arg.Any<CancellationToken>())
            .Returns([visible]);
        _geocoding.SelectAsync(Arg.Any<IReadOnlyList<AddressGeocodingCandidate>>(), Arg.Any<CancellationToken>())
            .Returns(call => new AddressGeocodingSelection(call.Arg<IReadOnlyList<AddressGeocodingCandidate>>(), [], []));
        _geocoding.GeocodeAsync(
                Arg.Any<IReadOnlyList<AddressGeocodingCandidate>>(), Arg.Any<AddressGeocodingAttribution>(),
                Arg.Any<AddressGeocodingBudget>(), Arg.Any<CancellationToken>())
            .Returns(new AddressGeocodingRunSummary(
                false, [new AddressGeocodingFilledAddress(visible, 47.376887, 8.541694, AddressGeocodingSource.NominatimLocality)], 0, 0, 0, 3));
        var context = Context();

        // Act
        var result = await Skill().ExecuteAsync(context, []);

        // Assert
        result.Success.ShouldBeTrue();
        await _geocoding.Received(1).SelectAsync(
            Arg.Is<IReadOnlyList<AddressGeocodingCandidate>>(list => list.SequenceEqual(new[] { visible })), Arg.Any<CancellationToken>());
        await _geocoding.Received(1).GeocodeAsync(
            Arg.Is<IReadOnlyList<AddressGeocodingCandidate>>(list => list.SequenceEqual(new[] { visible })),
            new AddressGeocodingAttribution(AddressGeocodingTrigger.Assistant, context.UserId),
            new AddressGeocodingBudget(AddressGeocodingLimits.AssistantGeocoderCallBudget, AddressGeocodingLimits.AssistantFillLimit, true),
            Arg.Any<CancellationToken>());
        result.Message.ShouldContain("WITHOUT asking");
        result.Message.ShouldContain("Muster AG, 8001 Zürich: 47.37689, 8.54169");
        result.Message.ShouldContain("3 more address(es) are being located in the background");
        result.Message.ShouldNotContain("Geheim AG");
        result.Message.ShouldNotContain(GeocodeAddressesSkill.SkillName);
    }

    [Test]
    public async Task Execute_ReportsNotFoundPrivacySkippedAndIncompleteAddresses()
    {
        // Arrange
        var employee = Candidate("Anna Muster");
        var incomplete = Candidate("Ohne Ort AG");
        var unknown = Candidate("Nirgendwo AG");
        _states.GetMissingCoordinatesAsync(null, Arg.Any<CancellationToken>()).Returns([employee, incomplete, unknown]);
        _visibility.FilterVisibleAsync(
                Arg.Any<IReadOnlyCollection<AddressGeocodingCandidate>>(), Arg.Any<Func<AddressGeocodingCandidate, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyCollection<AddressGeocodingCandidate>>().ToList());
        _geocoding.SelectAsync(Arg.Any<IReadOnlyList<AddressGeocodingCandidate>>(), Arg.Any<CancellationToken>())
            .Returns(new AddressGeocodingSelection([unknown], [employee], [incomplete]));
        _geocoding.GeocodeAsync(
                Arg.Any<IReadOnlyList<AddressGeocodingCandidate>>(), Arg.Any<AddressGeocodingAttribution>(),
                Arg.Any<AddressGeocodingBudget>(), Arg.Any<CancellationToken>())
            .Returns(new AddressGeocodingRunSummary(false, [], 1, 0, 0, 0));

        // Act
        var result = await Skill().ExecuteAsync(Context(), []);

        // Assert
        result.Message.ShouldNotContain("WITHOUT asking");
        result.Message.ShouldContain("1 address(es) could not be located");
        result.Message.ShouldContain("1 employee address(es) were left out");
        result.Message.ShouldContain("1 address(es) have no postcode or city");
    }

    [Test]
    public async Task Execute_HiddenOrUnknownClientId_IsAnsweredAsNotFoundAndNothingIsGeocoded()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        _visibility.IsVisibleAsync(clientId, Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var result = await Skill().ExecuteAsync(Context(), new Dictionary<string, object> { ["clientId"] = clientId.ToString() });

        // Assert
        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Client {clientId} not found.");
        await _states.DidNotReceiveWithAnyArgs().GetMissingCoordinatesAsync(default, default);
        await _geocoding.DidNotReceiveWithAnyArgs().GeocodeAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task Execute_ClientNumberWithoutName_IsRefusedInsteadOfCoveringEveryone()
    {
        // Act
        var result = await Skill().ExecuteAsync(Context(), new Dictionary<string, object> { ["idNumber"] = 42 });

        // Assert
        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Nothing was written");
        await _states.DidNotReceiveWithAnyArgs().GetMissingCoordinatesAsync(default, default);
    }

    [Test]
    public async Task Execute_VisibleClientId_ReadsOnlyThatClient()
    {
        // Arrange
        var client = new Client { Id = Guid.NewGuid(), Name = "Muster AG" };
        _visibility.IsVisibleAsync(client.Id, Arg.Any<CancellationToken>()).Returns(true);
        _clients.Get(client.Id).Returns(client);
        _states.GetMissingCoordinatesAsync(Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>()).Returns([]);
        _visibility.FilterVisibleAsync(
                Arg.Any<IReadOnlyCollection<AddressGeocodingCandidate>>(), Arg.Any<Func<AddressGeocodingCandidate, Guid>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _geocoding.SelectAsync(Arg.Any<IReadOnlyList<AddressGeocodingCandidate>>(), Arg.Any<CancellationToken>())
            .Returns(new AddressGeocodingSelection([], [], []));
        _geocoding.GeocodeAsync(
                Arg.Any<IReadOnlyList<AddressGeocodingCandidate>>(), Arg.Any<AddressGeocodingAttribution>(),
                Arg.Any<AddressGeocodingBudget>(), Arg.Any<CancellationToken>())
            .Returns(new AddressGeocodingRunSummary(false, [], 0, 0, 0, 0));

        // Act
        var result = await Skill().ExecuteAsync(Context(), new Dictionary<string, object> { ["clientId"] = client.Id.ToString() });

        // Assert
        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("Nothing was written");
        await _states.Received(1).GetMissingCoordinatesAsync(
            Arg.Is<IReadOnlyCollection<Guid>?>(ids => ids != null && ids.SequenceEqual(new[] { client.Id })), Arg.Any<CancellationToken>());
    }

    private GeocodeAddressesSkill Skill() => new(_search, _clients, _visibility, _states, _geocoding);

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "planner",
        UserPermissions = ["CanViewClients"]
    };

    private static AddressGeocodingCandidate Candidate(string clientName) =>
        new(Guid.NewGuid(), Guid.NewGuid(), EntityTypeEnum.Customer, clientName, "8001", "Zürich", "CH", string.Empty);
}
