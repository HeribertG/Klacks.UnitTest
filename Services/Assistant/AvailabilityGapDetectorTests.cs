// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AvailabilityGapDetector — covers the anti-spam guard (no availability
/// entries in the system), the empty result, severity per distance to month start, the
/// next-month window computation, the aggregation into ONE event per tick and the lockstep
/// between DetectAsync and the fingerprint scan.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class AvailabilityGapDetectorTests
{
    private IClientAvailabilityReadRepository _repo = null!;
    private AvailabilityGapDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _repo = Substitute.For<IClientAvailabilityReadRepository>();
        _repo.AnyAvailabilityEntriesExistAsync(Arg.Any<CancellationToken>()).Returns(true);
        _sut = CreateSut(new DateOnly(2026, 1, 15));
    }

    private AvailabilityGapDetector CreateSut(DateOnly today)
    {
        var clock = new FixedCompanyClock(new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
        return new AvailabilityGapDetector(_repo, NullLogger<AvailabilityGapDetector>.Instance, clock);
    }

    private void StubClients(params PlannableClientInfo[] clients)
    {
        _repo.GetPlannableClientsWithoutAvailabilityAsync(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(clients.ToList());
    }

    [Test]
    public async Task DetectAsync_NoAvailabilityEntriesInSystem_ReturnsEmptyWithoutClientScan()
    {
        _repo.AnyAvailabilityEntriesExistAsync(Arg.Any<CancellationToken>()).Returns(false);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
        await _repo.DidNotReceiveWithAnyArgs().GetPlannableClientsWithoutAvailabilityAsync(default, default, default, default);
    }

    [Test]
    public async Task DetectAsync_NoClientsWithoutAvailability_EmitsNothing()
    {
        StubClients();

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_MidMonth_EmitsOneMediumSeverityEventForNextCalendarMonth()
    {
        var clientId = Guid.NewGuid();
        StubClients(new PlannableClientInfo(clientId, "Max", "Müller"));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var evt = (AvailabilityGapSummaryTriggerEvent)events[0];
        Assert.That(evt.PeriodStart, Is.EqualTo(new DateOnly(2026, 2, 1)));
        Assert.That(evt.PeriodEnd, Is.EqualTo(new DateOnly(2026, 2, 28)));
        Assert.That(evt.Severity, Is.EqualTo(AgentTriggerSeverity.Medium));
        Assert.That(evt.AffectedClients.Single().ClientId, Is.EqualTo(clientId));
        Assert.That(evt.SummaryParams["count"], Is.EqualTo("1"));
        Assert.That(evt.SummaryParams["names"], Is.EqualTo("Max Müller"));
    }

    /// <summary>
    /// The heart of the aggregation: many findings, ONE event. The per-employee shape produced one inbox
    /// row per employee per recipient, which is a mailing and not a notification.
    /// </summary>
    [Test]
    public async Task DetectAsync_ManyClientsWithoutAvailability_EmitsExactlyOneAggregatedEvent()
    {
        const int clientCount = 37;
        StubClients(Enumerable.Range(0, clientCount)
            .Select(i => new PlannableClientInfo(Guid.NewGuid(), "Max", $"Müller{i}"))
            .ToArray());

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var evt = (AvailabilityGapSummaryTriggerEvent)events[0];
        Assert.That(evt.AffectedClients, Has.Count.EqualTo(clientCount));
        Assert.That(evt.SummaryParams["count"], Is.EqualTo(clientCount.ToString()));
    }

    /// <summary>
    /// The count must come from the uncapped scan while only the rendered name list is capped. A capped
    /// read would report the cap as the count and state a wrong number in the planner's inbox.
    /// </summary>
    [Test]
    public async Task DetectAsync_MoreThanTenAffected_ListsTenNamesAndAnOverflowCountButTheTrueTotal()
    {
        var clients = Enumerable.Range(0, ProactiveNameListRenderer.MaxListedNames + 2)
            .Select(i => new PlannableClientInfo(Guid.NewGuid(), "Max", $"Müller{i}"))
            .ToArray();
        StubClients(clients);

        var events = await _sut.DetectAsync();

        var evt = (AvailabilityGapSummaryTriggerEvent)events[0];
        Assert.That(evt.SummaryParams["count"], Is.EqualTo(clients.Length.ToString()));
        Assert.That(evt.SummaryParams["names"], Does.EndWith(" +2"));
        Assert.That(
            evt.SummaryParams["names"].Split(", ").Length,
            Is.EqualTo(ProactiveNameListRenderer.MaxListedNames));
    }

    /// <summary>
    /// The structured payload list is capped at the names the sentence can show, while "count" stays the
    /// full total. Nobody reads the list back - the inbox and the reminder sweep both drop non-scalar
    /// payload entries - but it is rewritten into PayloadJson on every detector tick and reloaded on
    /// every inbox poll, so an uncapped list carried the whole workforce through both paths for nothing.
    /// </summary>
    [Test]
    public async Task DetectAsync_MoreThanTenAffected_CapsThePayloadClientListButKeepsTheTrueCount()
    {
        var clients = Enumerable.Range(0, ProactiveNameListRenderer.MaxListedNames + 2)
            .Select(i => new PlannableClientInfo(Guid.NewGuid(), "Max", $"Müller{i}"))
            .ToArray();
        StubClients(clients);

        var events = await _sut.DetectAsync();

        var evt = (AvailabilityGapSummaryTriggerEvent)events[0];
        Assert.That(evt.Payload["count"], Is.EqualTo(clients.Length));
        Assert.That(
            (IReadOnlyList<ProactiveAffectedClient>)evt.Payload["clients"]!,
            Has.Count.EqualTo(ProactiveNameListRenderer.MaxListedNames));
        Assert.That(evt.AffectedClients, Has.Count.EqualTo(clients.Length),
            "The event still carries everybody; only the payload copy is capped.");
    }

    [Test]
    public async Task DetectAsync_ScansUncapped_SoTheReportedCountIsTheTruth()
    {
        StubClients(new PlannableClientInfo(Guid.NewGuid(), "Max", "Müller"));

        await _sut.DetectAsync();

        await _repo.Received(1).GetPlannableClientsWithoutAvailabilityAsync(
            new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), int.MaxValue, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The dedup key is the scanned MONTH, never the company day: a day-stamped key would leave the
    /// active fingerprint set at midnight although nothing was fixed, so reconcile would resolve the row
    /// and the partial unique index would re-arm a fresh one every morning - a daily nag.
    /// </summary>
    [Test]
    public async Task DetectAsync_DedupKey_IsThePeriodAloneAndIndependentOfWhoIsAffected()
    {
        StubClients(
            new PlannableClientInfo(Guid.NewGuid(), "Max", "Müller"),
            new PlannableClientInfo(Guid.NewGuid(), "Eva", "Meier"));

        var events = await _sut.DetectAsync();

        Assert.That(events[0].DedupKey, Is.EqualTo("2026-02"));
        Assert.That(events[0].DedupKey, Does.Not.Contain(":"));
    }

    [Test]
    public async Task DetectAsync_AggregateNamesNoSingleEntity()
    {
        StubClients(new PlannableClientInfo(Guid.NewGuid(), "Max", "Müller"));

        var events = await _sut.DetectAsync();

        Assert.That(events[0].EntityId, Is.Null);
        Assert.That(events[0].SummaryParams!.ContainsKey("name"), Is.False);
        Assert.That(events[0].ActionParams!.ContainsKey(ProactiveActionParamKeys.ClientId), Is.False);
    }

    [Test]
    public async Task DetectAsync_SummaryParams_CarryExactlyThePlaceholdersTheCataloguesInterpolate()
    {
        StubClients(new PlannableClientInfo(Guid.NewGuid(), "Max", "Müller"));

        var events = await _sut.DetectAsync();

        events[0].SummaryParams!.Keys.ShouldBe(
            ProactiveNoiseSummaryPlaceholders.AvailabilityGapNames, ignoreOrder: true);
        Assert.That(
            events[0].Summary,
            Is.EqualTo(ProactiveMessageMarkers.I18nPrefix + ProactiveMessageI18nKeys.AvailabilityGapSummary));
    }

    [Test]
    public async Task DetectAsync_WithinSevenDaysOfMonthStart_EmitsHighSeverity()
    {
        _sut = CreateSut(new DateOnly(2026, 1, 27));
        StubClients(new PlannableClientInfo(Guid.NewGuid(), "Max", "Müller"));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0].Severity, Is.EqualTo(AgentTriggerSeverity.High));
    }

    [Test]
    public async Task DetectAsync_AucklandCompanyDayAcrossUtcMidnight_UsesCompanyDayNotUtcDay()
    {
        // UTC instant 2026-06-27T23:30Z is still 27.06 in UTC but already 28.06 11:30 in Pacific/Auckland
        // (+12:00, no DST in the southern-hemisphere winter). Next calendar month is July either way, so
        // daysUntilPeriodStart distinguishes the two: 4 under (wrong) UTC day, 3 under the (correct)
        // company day.
        var instant = DateTimeOffset.Parse(
            "2026-06-27T23:30:00Z", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal);
        var clock = new FixedCompanyClock(instant, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland"));
        _sut = new AvailabilityGapDetector(_repo, NullLogger<AvailabilityGapDetector>.Instance, clock);
        StubClients(new PlannableClientInfo(Guid.NewGuid(), "Max", "Müller"));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var evt = (AvailabilityGapSummaryTriggerEvent)events[0];
        Assert.That(evt.PeriodStart, Is.EqualTo(new DateOnly(2026, 7, 1)));
        Assert.That(evt.DaysUntilPeriodStart, Is.EqualTo(3),
            "Company day (Pacific/Auckland) is already 28.06 at this UTC instant; the detector must not fall back to the UTC day 27.06.");
    }

    /// <summary>
    /// Replaces this detector's fixture in DetectorFingerprintContainmentTests, whose
    /// "fingerprints.Count > events.Count" assertion an uncapped aggregate cannot satisfy: both paths now
    /// return exactly one. Set EQUALITY is the stronger statement anyway - a narrower set would resolve
    /// the row the same tick opened, a wider one would leave a row open after the gap was filled.
    /// </summary>
    [Test]
    public async Task GetActiveFingerprintsAsync_MatchesDetectAsync_AsOneFingerprintPerPeriod()
    {
        StubClients(Enumerable.Range(0, 30)
            .Select(i => new PlannableClientInfo(Guid.NewGuid(), "Max", $"Müller{i}"))
            .ToArray());

        var events = await _sut.DetectAsync();
        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        Assert.That(fingerprints, Has.Count.EqualTo(1));
        Assert.That(
            fingerprints,
            Is.EquivalentTo(events.Select(AgentConditionLedgerPolicy.FingerprintFor)));
        Assert.That(
            fingerprints.Single(),
            Is.EqualTo(AgentConditionLedgerPolicy.FingerprintFor(
                AgentTriggerKinds.AvailabilityGap,
                AvailabilityGapSummaryTriggerEvent.DedupKeyFor(new DateOnly(2026, 2, 1)))));
    }

    [Test]
    public async Task GetActiveFingerprintsAsync_NoClients_ReturnsEmptySoTheRowResolves()
    {
        StubClients();

        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        Assert.That(fingerprints, Is.Empty);
    }

    [Test]
    public async Task GetActiveFingerprintsAsync_NoAvailabilityEntriesInSystem_ReturnsEmpty()
    {
        _repo.AnyAvailabilityEntriesExistAsync(Arg.Any<CancellationToken>()).Returns(false);

        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        Assert.That(fingerprints, Is.Empty);
    }
}
