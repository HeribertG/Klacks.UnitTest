// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClientMissingCoreDataDetector — covers the empty result, the defensive
/// skip when nothing is missing, the aggregation into ONE event per missing field with the
/// contracted severity and dedup key, and the lockstep between DetectAsync and the
/// fingerprint scan.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class ClientMissingCoreDataDetectorTests
{
    private IClientCoreDataReadRepository _repo = null!;
    private ClientMissingCoreDataDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _repo = Substitute.For<IClientCoreDataReadRepository>();
        _sut = CreateSut(new DateOnly(2026, 1, 15));
    }

    private ClientMissingCoreDataDetector CreateSut(DateOnly today)
    {
        var clock = new FixedCompanyClock(new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
        return new ClientMissingCoreDataDetector(_repo, NullLogger<ClientMissingCoreDataDetector>.Instance, clock);
    }

    private void StubStatuses(params ClientCoreDataStatus[] statuses)
    {
        _repo.GetActiveClientsWithMissingCoreDataAsync(Arg.Any<DateOnly>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(statuses.ToList());
    }

    private static ClientMissingCoreDataSummaryTriggerEvent EventFor(
        IReadOnlyList<IAgentTriggerEvent> events,
        string missingField) =>
        events
            .Cast<ClientMissingCoreDataSummaryTriggerEvent>()
            .Single(evt => evt.MissingField == missingField);

    [Test]
    public async Task DetectAsync_NoClientsWithGaps_ReturnsEmpty()
    {
        StubStatuses();

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ScansUncapped_SoTheReportedCountIsTheTruth()
    {
        StubStatuses();

        await _sut.DetectAsync();

        await _repo.Received(1).GetActiveClientsWithMissingCoreDataAsync(
            new DateOnly(2026, 1, 15), int.MaxValue, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DetectAsync_ClientWithCompleteCoreData_EmitsNothing()
    {
        StubStatuses(new ClientCoreDataStatus(Guid.NewGuid(), "Max", "Müller", true, true));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_OnlyAddressesMissing_EmitsTheAddressAggregateAlone()
    {
        var clientId = Guid.NewGuid();
        StubStatuses(new ClientCoreDataStatus(clientId, "Max", "Müller", false, true));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var evt = (ClientMissingCoreDataSummaryTriggerEvent)events[0];
        Assert.That(evt.MissingField, Is.EqualTo(ClientMissingCoreDataTriggerEvent.AddressField));
        Assert.That(evt.Severity, Is.EqualTo(AgentTriggerSeverity.Medium));
        Assert.That(evt.DedupKey, Is.EqualTo(ClientMissingCoreDataTriggerEvent.AddressField));
        Assert.That(evt.AffectedClients.Single().ClientId, Is.EqualTo(clientId));
        Assert.That(evt.SummaryParams["names"], Is.EqualTo("Max Müller"));
        Assert.That(
            evt.Summary,
            Is.EqualTo(ProactiveMessageMarkers.I18nPrefix + ProactiveMessageI18nKeys.ClientMissingAddressSummary));
    }

    [Test]
    public async Task DetectAsync_OnlyContactsMissing_EmitsTheContactAggregateAlone()
    {
        StubStatuses(new ClientCoreDataStatus(Guid.NewGuid(), "Max", "Müller", true, false));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var evt = (ClientMissingCoreDataSummaryTriggerEvent)events[0];
        Assert.That(evt.MissingField, Is.EqualTo(ClientMissingCoreDataTriggerEvent.ContactField));
        Assert.That(evt.Severity, Is.EqualTo(AgentTriggerSeverity.Low));
        Assert.That(evt.DedupKey, Is.EqualTo(ClientMissingCoreDataTriggerEvent.ContactField));
        Assert.That(
            evt.Summary,
            Is.EqualTo(ProactiveMessageMarkers.I18nPrefix + ProactiveMessageI18nKeys.ClientMissingContactSummary));
    }

    /// <summary>
    /// The whole reduction: many clients with both gaps collapse to exactly TWO events, one per field -
    /// never one per client and field, and never a single merged event, because the two gaps carry
    /// different severities and different sentences.
    /// </summary>
    [Test]
    public async Task DetectAsync_ManyClientsMissingBoth_EmitsExactlyTwoAggregatesOnePerField()
    {
        const int clientCount = 40;
        StubStatuses(Enumerable.Range(0, clientCount)
            .Select(i => new ClientCoreDataStatus(Guid.NewGuid(), "Max", $"Müller{i}", false, false))
            .ToArray());

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(2));
        Assert.That(events.Select(e => e.DedupKey), Is.Unique);
        Assert.That(EventFor(events, ClientMissingCoreDataTriggerEvent.AddressField).AffectedClients, Has.Count.EqualTo(clientCount));
        Assert.That(EventFor(events, ClientMissingCoreDataTriggerEvent.ContactField).AffectedClients, Has.Count.EqualTo(clientCount));
        Assert.That(
            EventFor(events, ClientMissingCoreDataTriggerEvent.AddressField).SummaryParams["count"],
            Is.EqualTo(clientCount.ToString()));
    }

    [Test]
    public async Task DetectAsync_MixedGaps_SortsEachClientIntoTheAggregateOfTheFieldItLacks()
    {
        var withoutAddress = Guid.NewGuid();
        var withoutContact = Guid.NewGuid();
        StubStatuses(
            new ClientCoreDataStatus(withoutAddress, "Max", "Müller", false, true),
            new ClientCoreDataStatus(withoutContact, "Eva", "Meier", true, false));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(2));
        Assert.That(
            EventFor(events, ClientMissingCoreDataTriggerEvent.AddressField).AffectedClients.Single().ClientId,
            Is.EqualTo(withoutAddress));
        Assert.That(
            EventFor(events, ClientMissingCoreDataTriggerEvent.ContactField).AffectedClients.Single().ClientId,
            Is.EqualTo(withoutContact));
    }

    /// <summary>
    /// The count must come from the uncapped scan while only the rendered name list is capped.
    /// </summary>
    [Test]
    public async Task DetectAsync_MoreThanTenAffected_ListsTenNamesAndAnOverflowCountButTheTrueTotal()
    {
        var statuses = Enumerable.Range(0, ProactiveNameListRenderer.MaxListedNames + 3)
            .Select(i => new ClientCoreDataStatus(Guid.NewGuid(), "Max", $"Müller{i}", false, true))
            .ToArray();
        StubStatuses(statuses);

        var events = await _sut.DetectAsync();

        var evt = (ClientMissingCoreDataSummaryTriggerEvent)events[0];
        Assert.That(evt.SummaryParams["count"], Is.EqualTo(statuses.Length.ToString()));
        Assert.That(evt.SummaryParams["names"], Does.EndWith(" +3"));
        Assert.That(
            evt.SummaryParams["names"].Split(", ").Length,
            Is.EqualTo(ProactiveNameListRenderer.MaxListedNames));
    }

    /// <summary>
    /// The structured payload list is capped at the names the sentence can show, while "count" stays the
    /// full total. Nobody reads the list back - the inbox and the reminder sweep both drop non-scalar
    /// payload entries - but it is rewritten into PayloadJson on every detector tick and reloaded on every
    /// inbox poll, and this kind's row stands open until the last gap is filled, so an uncapped list only
    /// ever grew.
    /// </summary>
    [Test]
    public async Task DetectAsync_MoreThanTenAffected_CapsThePayloadClientListButKeepsTheTrueCount()
    {
        var statuses = Enumerable.Range(0, ProactiveNameListRenderer.MaxListedNames + 3)
            .Select(i => new ClientCoreDataStatus(Guid.NewGuid(), "Max", $"Müller{i}", false, true))
            .ToArray();
        StubStatuses(statuses);

        var events = await _sut.DetectAsync();

        var evt = (ClientMissingCoreDataSummaryTriggerEvent)events[0];
        Assert.That(evt.Payload["count"], Is.EqualTo(statuses.Length));
        Assert.That(
            (IReadOnlyList<ProactiveAffectedClient>)evt.Payload["clients"]!,
            Has.Count.EqualTo(ProactiveNameListRenderer.MaxListedNames));
        Assert.That(evt.AffectedClients, Has.Count.EqualTo(statuses.Length),
            "The event still carries everybody; only the payload copy is capped.");
    }

    /// <summary>
    /// A core-data field that AllMissingFields names but Lacks does not know must fail loudly. The former
    /// conditional fell through to the contact branch, so a newly added field would have been reported as
    /// a missing way to be contacted - the wrong sentence under the wrong severity, with nothing to show
    /// that the two lists had drifted apart.
    /// </summary>
    [Test]
    public void Lacks_AFieldItDoesNotKnow_Throws()
    {
        var status = new ClientCoreDataStatus(Guid.NewGuid(), "Max", "Müller", true, true);

        Assert.That(
            () => ClientMissingCoreDataDetector.Lacks(status, "unknown_core_data_field"),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    /// <summary>
    /// The dedup key is the field alone, never the company day: this finding has no natural period, and
    /// a day-stamped key would leave the active fingerprint set every midnight although nothing was
    /// fixed, turning a state-based ledger row into a permanent daily nag.
    /// </summary>
    [Test]
    public async Task DetectAsync_DedupKeys_AreTheFieldsAloneAndIndependentOfWhoIsAffected()
    {
        StubStatuses(
            new ClientCoreDataStatus(Guid.NewGuid(), "Max", "Müller", false, false),
            new ClientCoreDataStatus(Guid.NewGuid(), "Eva", "Meier", false, false));

        var events = await _sut.DetectAsync();

        Assert.That(
            events.Select(e => e.DedupKey),
            Is.EquivalentTo(new[]
            {
                ClientMissingCoreDataTriggerEvent.AddressField,
                ClientMissingCoreDataTriggerEvent.ContactField
            }));
    }

    [Test]
    public async Task DetectAsync_AggregatesNameNoSingleEntity()
    {
        StubStatuses(new ClientCoreDataStatus(Guid.NewGuid(), "Max", "Müller", false, false));

        var events = await _sut.DetectAsync();

        foreach (var evt in events)
        {
            Assert.That(evt.EntityId, Is.Null);
            Assert.That(evt.SummaryParams!.ContainsKey("name"), Is.False);
            Assert.That(evt.ActionParams, Is.Null);
            Assert.That(evt.ActionRoute, Is.EqualTo(ProactiveActionRoutes.ClientList));
        }
    }

    [Test]
    public async Task DetectAsync_SummaryParams_CarryExactlyThePlaceholdersTheCataloguesInterpolate()
    {
        StubStatuses(new ClientCoreDataStatus(Guid.NewGuid(), "Max", "Müller", false, false));

        var events = await _sut.DetectAsync();

        foreach (var evt in events)
        {
            evt.SummaryParams!.Keys.ShouldBe(
                ProactiveNoiseSummaryPlaceholders.ClientMissingCoreDataNames, ignoreOrder: true);
        }
    }

    [Test]
    public async Task DetectAsync_AucklandCompanyDayAcrossUtcMidnight_UsesCompanyDayNotUtcDay()
    {
        // UTC instant 2026-06-27T23:30Z is still 27.06 in UTC but already 28.06 11:30 in Pacific/Auckland
        // (+12:00, no DST in the southern-hemisphere winter). Passing the company day, not the UTC day,
        // as the reference date is what this test pins.
        var instant = DateTimeOffset.Parse(
            "2026-06-27T23:30:00Z", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal);
        var clock = new FixedCompanyClock(instant, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland"));
        _sut = new ClientMissingCoreDataDetector(_repo, NullLogger<ClientMissingCoreDataDetector>.Instance, clock);
        StubStatuses();

        await _sut.DetectAsync();

        await _repo.Received(1).GetActiveClientsWithMissingCoreDataAsync(
            new DateOnly(2026, 6, 28), int.MaxValue, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Replaces this detector's fixture in DetectorFingerprintContainmentTests, whose
    /// "fingerprints.Count > events.Count" assertion an uncapped aggregate cannot satisfy. Set EQUALITY
    /// is the stronger statement: a narrower set would resolve the row the same tick opened, a wider one
    /// would leave a field's row open after its last gap was filled.
    /// </summary>
    [Test]
    public async Task GetActiveFingerprintsAsync_MatchesDetectAsync_AsOneFingerprintPerMissingField()
    {
        StubStatuses(Enumerable.Range(0, 30)
            .Select(i => new ClientCoreDataStatus(Guid.NewGuid(), "Max", $"Müller{i}", false, false))
            .ToArray());

        var events = await _sut.DetectAsync();
        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        Assert.That(fingerprints, Has.Count.EqualTo(2));
        Assert.That(
            fingerprints,
            Is.EquivalentTo(events.Select(AgentConditionLedgerPolicy.FingerprintFor)));
    }

    [Test]
    public async Task GetActiveFingerprintsAsync_OnlyOneFieldMissing_ReportsOnlyThatField()
    {
        StubStatuses(new ClientCoreDataStatus(Guid.NewGuid(), "Max", "Müller", true, false));

        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        Assert.That(
            fingerprints.Single(),
            Is.EqualTo(AgentConditionLedgerPolicy.FingerprintFor(
                AgentTriggerKinds.ClientMissingCoreData,
                ClientMissingCoreDataSummaryTriggerEvent.DedupKeyFor(
                    ClientMissingCoreDataTriggerEvent.ContactField))),
            "A field nobody is missing must stay out of the set, or its ledger row would never resolve.");
    }

    [Test]
    public async Task GetActiveFingerprintsAsync_NoGaps_ReturnsEmptySoTheRowsResolve()
    {
        StubStatuses();

        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        Assert.That(fingerprints, Is.Empty);
    }
}
