// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for UngroupedShiftsDetector. Covers the two-part gate (at least one group exists, enough
/// duties without a group), the audience and route of the emitted event, the constant dedup key, and —
/// the part that actually protects the ledger — that the fingerprint scan falls silent in EVERY case the
/// emission gate does, not only when the duties are assigned.
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
public class UngroupedShiftsDetectorTests
{
    private const string CountParam = "count";

    private static readonly DateOnly Today = new(2026, 9, 21);

    private IScheduleActivityProbe _activityProbe = null!;
    private UngroupedShiftsDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _activityProbe = Substitute.For<IScheduleActivityProbe>();
        _sut = new UngroupedShiftsDetector(
            _activityProbe,
            new FixedCompanyClock(new DateTimeOffset(Today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))),
            NullLogger<UngroupedShiftsDetector>.Instance);
    }

    private void Stub(bool hasGroups, int ungroupedShifts)
    {
        _activityProbe.GetSetupStateAsync(Arg.Any<CancellationToken>())
            .Returns(new ScheduleSetupState(true, true, true, true, hasGroups));
        _activityProbe.CountUngroupedPlannableShiftsAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(ungroupedShifts);
    }

    private static string ExpectedFingerprint() =>
        AgentConditionLedgerPolicy.FingerprintFor(
            AgentTriggerKinds.UngroupedShifts, UngroupedShiftsTriggerEvent.InstallationDedupKey);

    [Test]
    public async Task DetectAsync_BelowTheThreshold_EmitsNothing()
    {
        Stub(
            hasGroups: true,
            ungroupedShifts: UngroupedShiftsDetector.MinUngroupedShiftsForRecommendation - 1);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    /// <summary>
    /// The inverse of ungrouped_workforce's own gate: an installation holding no group at all needs its
    /// first group, not a review of individual duties, and the two kinds must never speak in one tick.
    /// </summary>
    [Test]
    public async Task DetectAsync_NoGroupExistsAtAll_EmitsNothingBecauseThatIsTheOtherKindsDomain()
    {
        Stub(hasGroups: false, ungroupedShifts: 400);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_AtTheThreshold_EmitsOneEventCarryingTheCount()
    {
        const int shifts = UngroupedShiftsDetector.MinUngroupedShiftsForRecommendation;
        Stub(hasGroups: true, ungroupedShifts: shifts);

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(events[0].Kind, Is.EqualTo(AgentTriggerKinds.UngroupedShifts));
            Assert.That(events[0].Summary, Does.Contain(ProactiveMessageI18nKeys.UngroupedShifts));
            Assert.That(
                events[0].SummaryParams![CountParam],
                Is.EqualTo(shifts.ToString(CultureInfo.InvariantCulture)));
            Assert.That(events[0].Payload![CountParam], Is.EqualTo(shifts));
        });
    }

    [Test]
    public async Task DetectAsync_ConditionHolds_EmitsExactlyOneEventPerTick()
    {
        Stub(hasGroups: true, ungroupedShifts: 47);

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
    /// The dedup key must move neither with the count nor with the day: dispatch dedup has no time
    /// window, so a per-day key would re-open the finding every midnight while the row it replaced never
    /// resolved, and a key carrying the count would do the same on every tick that groups one duty.
    /// </summary>
    [Test]
    public async Task DetectAsync_DifferentCounts_ShareTheSameConstantDedupKey()
    {
        Stub(hasGroups: true, ungroupedShifts: 12);
        var few = (await _sut.DetectAsync())[0];

        Stub(hasGroups: true, ungroupedShifts: 900);
        var many = (await _sut.DetectAsync())[0];

        Assert.Multiple(() =>
        {
            Assert.That(many.DedupKey, Is.EqualTo(few.DedupKey));
            Assert.That(few.DedupKey, Is.EqualTo(UngroupedShiftsTriggerEvent.InstallationDedupKey));
        });
    }

    [Test]
    public async Task DetectAsync_EmittedEvent_IsAdminOnlyLowAndPointsAtTheShiftList()
    {
        Stub(hasGroups: true, ungroupedShifts: 42);

        var triggerEvent = (await _sut.DetectAsync())[0];

        Assert.Multiple(() =>
        {
            Assert.That(triggerEvent.AdminOnly, Is.True);
            Assert.That(triggerEvent.TargetUserId, Is.Null);
            Assert.That(triggerEvent.Severity, Is.EqualTo(AgentTriggerSeverity.Low));
            Assert.That(triggerEvent.GroupId, Is.Null);
            Assert.That(triggerEvent.ActionRoute, Is.EqualTo(ProactiveActionRoutes.ShiftList));
        });
    }

    [Test]
    public async Task GetActiveFingerprintsAsync_ConditionHolds_ContainsTheEmittedFingerprint()
    {
        Stub(hasGroups: true, ungroupedShifts: 42);

        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        Assert.That(fingerprints, Is.EquivalentTo(new[] { ExpectedFingerprint() }));
    }

    /// <summary>
    /// A fingerprint scan narrower than the emission gate would keep an open row alive asserting a
    /// finding that stopped being true, which is the one direction the ledger cannot recover from.
    /// </summary>
    [Test]
    public async Task GetActiveFingerprintsAsync_DutiesWereAssigned_IsEmptySoTheLedgerRowResolves()
    {
        Stub(
            hasGroups: true,
            ungroupedShifts: UngroupedShiftsDetector.MinUngroupedShiftsForRecommendation - 1);

        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        Assert.That(fingerprints, Is.Empty);
    }

    [Test]
    public async Task GetActiveFingerprintsAsync_NoGroupExistsAnyMore_IsEmpty()
    {
        Stub(hasGroups: false, ungroupedShifts: 400);

        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        Assert.That(fingerprints, Is.Empty);
    }

    /// <summary>
    /// The validity window is measured against the company's own day — not the host day and not a
    /// hard-coded zone.
    /// </summary>
    [Test]
    public async Task DetectAsync_CountsTheDutiesAgainstTheCompanyDay()
    {
        Stub(hasGroups: true, ungroupedShifts: 42);

        await _sut.DetectAsync();

        await _activityProbe.Received(1).CountUngroupedPlannableShiftsAsync(Today, Arg.Any<CancellationToken>());
    }
}
