// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for UngroupedWorkforceDetector. Covers the three-part gate (no group, enough active
/// employees, something already planned), the audience and route of the emitted event, and — the part
/// that actually protects the ledger — that the fingerprint scan falls silent in EVERY case the
/// emission gate does, not only when a group appears.
/// </summary>

using System.Globalization;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class UngroupedWorkforceDetectorTests
{
    private const string CountParam = "count";

    private static readonly DateOnly Today = new(2026, 9, 21);

    private IScheduleActivityProbe _activityProbe = null!;
    private UngroupedWorkforceDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _activityProbe = Substitute.For<IScheduleActivityProbe>();
        _sut = new UngroupedWorkforceDetector(
            _activityProbe,
            new FixedCompanyClock(new DateTimeOffset(Today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))),
            NullLogger<UngroupedWorkforceDetector>.Instance);
    }

    private void Stub(bool hasGroups, bool hasWork, int activeEmployees)
    {
        _activityProbe.GetSetupStateAsync(Arg.Any<CancellationToken>())
            .Returns(new ScheduleSetupState(true, true, hasWork, true, hasGroups));
        _activityProbe.CountActiveEmployeesAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(activeEmployees);
    }

    private static string ExpectedFingerprint() =>
        AgentConditionLedgerPolicy.FingerprintFor(
            AgentTriggerKinds.UngroupedWorkforce, UngroupedWorkforceTriggerEvent.InstallationDedupKey);

    [Test]
    public async Task DetectAsync_BelowTheThreshold_EmitsNothing()
    {
        Stub(
            hasGroups: false,
            hasWork: true,
            activeEmployees: UngroupedWorkforceDetector.MinEmployeesForGroupingSuggestion - 1);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_AGroupAlreadyExists_EmitsNothing()
    {
        Stub(hasGroups: true, hasWork: true, activeEmployees: 400);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    /// <summary>
    /// The inverse of no_schedule_yet's own gate: an installation that has never planned anything needs
    /// the first schedule, not a grouping, and the two kinds must never speak in the same tick.
    /// </summary>
    [Test]
    public async Task DetectAsync_NothingIsPlannedYet_EmitsNothing()
    {
        Stub(hasGroups: false, hasWork: false, activeEmployees: 400);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_AtTheThreshold_EmitsOneEventCarryingTheCount()
    {
        const int employees = UngroupedWorkforceDetector.MinEmployeesForGroupingSuggestion;
        Stub(hasGroups: false, hasWork: true, activeEmployees: employees);

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(events[0].Kind, Is.EqualTo(AgentTriggerKinds.UngroupedWorkforce));
            Assert.That(events[0].Summary, Does.Contain(ProactiveMessageI18nKeys.UngroupedWorkforce));
            Assert.That(
                events[0].SummaryParams![CountParam],
                Is.EqualTo(employees.ToString(CultureInfo.InvariantCulture)));
        });
    }

    [Test]
    public async Task DetectAsync_ConditionHolds_EmitsExactlyOneEventPerTick()
    {
        Stub(hasGroups: false, hasWork: true, activeEmployees: 623);

        var first = await _sut.DetectAsync();
        var second = await _sut.DetectAsync();

        Assert.Multiple(() =>
        {
            Assert.That(first, Has.Count.EqualTo(1));
            Assert.That(second, Has.Count.EqualTo(1));
            Assert.That(second[0].DedupKey, Is.EqualTo(first[0].DedupKey));
        });
    }

    /// <summary>
    /// The dedup key must not move with the headcount: dispatch dedup has no time window, so a key
    /// carrying the count would open a fresh ledger row on every tick that hires or loses one person
    /// while the row it replaced never resolved.
    /// </summary>
    [Test]
    public async Task DetectAsync_DifferentHeadcounts_ShareTheSameDedupKey()
    {
        Stub(hasGroups: false, hasWork: true, activeEmployees: 20);
        var small = (await _sut.DetectAsync())[0];

        Stub(hasGroups: false, hasWork: true, activeEmployees: 900);
        var large = (await _sut.DetectAsync())[0];

        Assert.That(large.DedupKey, Is.EqualTo(small.DedupKey));
    }

    [Test]
    public async Task DetectAsync_EmittedEvent_IsAdminOnlyLowAndPointsAtTheGroupList()
    {
        Stub(hasGroups: false, hasWork: true, activeEmployees: 42);

        var triggerEvent = (await _sut.DetectAsync())[0];

        Assert.Multiple(() =>
        {
            Assert.That(triggerEvent.AdminOnly, Is.True);
            Assert.That(triggerEvent.TargetUserId, Is.Null);
            Assert.That(triggerEvent.Severity, Is.EqualTo(AgentTriggerSeverity.Low));
            Assert.That(triggerEvent.GroupId, Is.Null);
            Assert.That(triggerEvent.ActionRoute, Is.EqualTo(ProactiveActionRoutes.GroupList));
        });
    }

    [Test]
    public async Task GetActiveFingerprintsAsync_ConditionHolds_ContainsTheEmittedFingerprint()
    {
        Stub(hasGroups: false, hasWork: true, activeEmployees: 42);

        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        Assert.That(fingerprints, Is.EquivalentTo(new[] { ExpectedFingerprint() }));
    }

    [Test]
    public async Task GetActiveFingerprintsAsync_AGroupExists_IsEmptySoTheLedgerRowResolves()
    {
        Stub(hasGroups: true, hasWork: true, activeEmployees: 400);

        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        Assert.That(fingerprints, Is.Empty);
    }

    /// <summary>
    /// A fingerprint scan that only watched the group condition would keep an open row alive asserting a
    /// finding that stopped being true for another reason, which is the one direction the ledger cannot
    /// recover from on its own.
    /// </summary>
    [Test]
    public async Task GetActiveFingerprintsAsync_WorkforceShrankBelowTheThreshold_IsEmpty()
    {
        Stub(
            hasGroups: false,
            hasWork: true,
            activeEmployees: UngroupedWorkforceDetector.MinEmployeesForGroupingSuggestion - 1);

        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        Assert.That(fingerprints, Is.Empty);
    }

    [Test]
    public async Task GetActiveFingerprintsAsync_NothingIsPlannedAnyMore_IsEmpty()
    {
        Stub(hasGroups: false, hasWork: false, activeEmployees: 400);

        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        Assert.That(fingerprints, Is.Empty);
    }

    /// <summary>
    /// "No groups" is read from the setup snapshot's FLAT group probe, and the headcount is asked for the
    /// company's own day — not the host day and not a hard-coded zone.
    /// </summary>
    [Test]
    public async Task DetectAsync_CountsTheWorkforceAgainstTheCompanyDay()
    {
        Stub(hasGroups: false, hasWork: true, activeEmployees: 42);

        await _sut.DetectAsync();

        await _activityProbe.Received(1).CountActiveEmployeesAsync(Today, Arg.Any<CancellationToken>());
    }
}
