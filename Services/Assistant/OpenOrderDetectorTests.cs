// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for OpenOrderDetector since it emits one daily summary instead of one event per order. An order is
/// open while it is an unsealed OriginalOrder that has not ended (UntilDate empty or on or after the company day);
/// sealed, ended, scenario and soft-deleted orders never count. The scan is uncapped, emits at most one
/// OpenOrdersSummaryTriggerEvent per tick keyed on the company day, carries every order's groups from ONE batched
/// lookup, and its fingerprint set equals the emitted set (which is what resolves the per-order rows of the former
/// shape). Runs on the real ShiftReader over EF InMemory (InMemoryShiftStore).
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class OpenOrderDetectorTests
{
    private static readonly DateOnly Today = new(2026, 10, 22);

    private InMemoryShiftStore _store = null!;
    private IShiftGroupScopeReader _groupScopeReader = null!;
    private OpenOrderDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _store = new InMemoryShiftStore();
        _groupScopeReader = ShiftGroupScopeReaderStub.WithoutAnyGroups();
        _sut = CreateSut(new FixedCompanyClock(new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
    }

    [TearDown]
    public void TearDown() => _store.Dispose();

    private OpenOrderDetector CreateSut(FixedCompanyClock clock) =>
        new(_store.Reader, _groupScopeReader, clock, NullLogger<OpenOrderDetector>.Instance);

    private static Shift MakeShift(
        DateOnly fromDate,
        DateOnly? untilDate = null,
        ShiftStatus status = ShiftStatus.OriginalOrder,
        Guid? analyseToken = null,
        Guid? scenarioSourceShiftId = null,
        bool isDeleted = false) => new()
    {
        Id = Guid.NewGuid(),
        Status = status,
        FromDate = fromDate,
        UntilDate = untilDate,
        Name = "Test Order",
        AnalyseToken = analyseToken,
        ScenarioSourceShiftId = scenarioSourceShiftId,
        IsDeleted = isDeleted
    };

    private async Task<OpenOrdersSummaryTriggerEvent> SingleSummaryAsync() =>
        (OpenOrdersSummaryTriggerEvent)(await _sut.DetectAsync()).Single();

    [Test]
    public async Task DetectAsync_NoOpenOrder_EmitsNothingAndHoldsNoFingerprint()
    {
        _store.Seed();

        var events = await _sut.DetectAsync();
        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        Assert.That(events, Is.Empty);
        Assert.That(fingerprints, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ManyOpenOrders_EmitsExactlyOneSummaryCountingAllOfThem()
    {
        _store.Seed(Enumerable.Range(0, 1200).Select(offset => MakeShift(Today.AddDays(offset % 90))));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1), "One summary per tick, never one event per order.");
        var summary = (OpenOrdersSummaryTriggerEvent)events.Single();
        Assert.That(summary.Count, Is.EqualTo(1200), "The count is uncapped; the former 1000-row scan cap is gone.");
        Assert.That(summary.Kind, Is.EqualTo(AgentTriggerKinds.OpenOrder));
        Assert.That(summary.EntityId, Is.Null);
    }

    [Test]
    public async Task DetectAsync_EndedOrder_IsNotCounted_ButARunningOrderIs()
    {
        var ended = MakeShift(Today.AddDays(-10), untilDate: Today.AddDays(-1));
        var endingToday = MakeShift(Today.AddDays(-10), untilDate: Today);
        var runningWithoutEnd = MakeShift(Today.AddDays(-3));
        _store.Seed(ended, endingToday, runningWithoutEnd);

        var summary = await SingleSummaryAsync();

        Assert.That(summary.Orders.Select(order => order.ShiftId),
            Is.EquivalentTo(new[] { endingToday.Id, runningWithoutEnd.Id }));
        Assert.That(summary.SoonCount, Is.Zero, "Orders that already started are open, but they do not start within the next days.");
    }

    [TestCase(ShiftStatus.SealedOrder)]
    [TestCase(ShiftStatus.OriginalShift)]
    [TestCase(ShiftStatus.SplitShift)]
    public async Task DetectAsync_OrderThatIsNoLongerAnOriginalOrder_IsNotCounted(ShiftStatus status)
    {
        _store.Seed(MakeShift(Today.AddDays(3), status: status));

        Assert.That(await _sut.DetectAsync(), Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ScenarioClonesAndSoftDeletedOrders_AreNotCounted()
    {
        var real = MakeShift(Today.AddDays(3));
        _store.Seed(
            real,
            MakeShift(Today.AddDays(3), analyseToken: Guid.NewGuid()),
            MakeShift(Today.AddDays(3), scenarioSourceShiftId: Guid.NewGuid()),
            MakeShift(Today.AddDays(3), isDeleted: true));

        var summary = await SingleSummaryAsync();

        Assert.That(summary.Orders.Select(order => order.ShiftId), Is.EqualTo(new[] { real.Id }));
    }

    [Test]
    public async Task DetectAsync_SoonCount_CoversTodayUpToSevenDaysAhead()
    {
        _store.Seed(
            MakeShift(Today),
            MakeShift(Today.AddDays(OpenOrdersSummaryTriggerEvent.SoonWindowDays)),
            MakeShift(Today.AddDays(OpenOrdersSummaryTriggerEvent.SoonWindowDays + 1)),
            MakeShift(Today.AddDays(40)));

        var summary = await SingleSummaryAsync();

        Assert.That(summary.Count, Is.EqualTo(4));
        Assert.That(summary.SoonCount, Is.EqualTo(2));
        Assert.That(summary.SummaryParams["count"], Is.EqualTo("4"));
        Assert.That(summary.SummaryParams["soonCount"], Is.EqualTo("2"));
    }

    [Test]
    public async Task DetectAsync_DedupKeyIsTheCompanyDay_SoTwoTicksOfOneDayShareOneCondition()
    {
        _store.Seed(MakeShift(Today.AddDays(3)));

        var first = await SingleSummaryAsync();
        var second = (OpenOrdersSummaryTriggerEvent)(await CreateSut(
            new FixedCompanyClock(new DateTimeOffset(2026, 10, 22, 21, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc)).DetectAsync()).Single();

        Assert.That(first.DedupKey, Is.EqualTo("2026-10-22"));
        Assert.That(second.DedupKey, Is.EqualTo(first.DedupKey));
    }

    [Test]
    public async Task DetectAsync_AucklandCompanyDayAcrossUtcMidnight_UsesCompanyDayNotUtcDay()
    {
        // 2026-06-27T23:30Z is already 28.06 in Pacific/Auckland (+12:00 in the southern winter). An order that
        // ended on 27.06 is still open under the UTC day and closed under the company day.
        var instant = DateTimeOffset.Parse(
            "2026-06-27T23:30:00Z", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal);
        _sut = CreateSut(new FixedCompanyClock(instant, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland")));
        var endedYesterday = MakeShift(new DateOnly(2026, 6, 20), untilDate: new DateOnly(2026, 6, 27));
        var open = MakeShift(new DateOnly(2026, 6, 30));
        _store.Seed(endedYesterday, open);

        var summary = (OpenOrdersSummaryTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(summary.CompanyDay, Is.EqualTo(new DateOnly(2026, 6, 28)));
        Assert.That(summary.Orders.Select(order => order.ShiftId), Is.EqualTo(new[] { open.Id }));
    }

    [Test]
    public async Task DetectAsync_CarriesEveryOrdersGroups_FromOneBatchedLookup()
    {
        var inOneGroup = MakeShift(Today.AddDays(3));
        var inTwoGroups = MakeShift(Today.AddDays(4));
        var ungrouped = MakeShift(Today.AddDays(5));
        var firstGroup = Guid.NewGuid();
        var secondGroup = Guid.NewGuid();
        _store.Seed(inOneGroup, inTwoGroups, ungrouped);
        ShiftGroupScopeReaderStub.SetGroups(
            _groupScopeReader,
            (inOneGroup.Id, new[] { firstGroup }),
            (inTwoGroups.Id, new[] { firstGroup, secondGroup }));

        var summary = await SingleSummaryAsync();

        var groupsByOrder = summary.Orders.ToDictionary(order => order.ShiftId, order => order.GroupIds);
        Assert.That(groupsByOrder[inOneGroup.Id], Is.EquivalentTo(new[] { firstGroup }));
        Assert.That(groupsByOrder[inTwoGroups.Id], Is.EquivalentTo(new[] { firstGroup, secondGroup }));
        Assert.That(groupsByOrder[ungrouped.Id], Is.Empty);
        Assert.That(summary.GroupIds, Is.EquivalentTo(new[] { firstGroup, secondGroup }));
        Assert.That(summary.RequiresGroupScope, Is.True);
        await _groupScopeReader.Received(1).GetGroupIdsByShiftIdsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetActiveFingerprintsAsync_EqualsTheEmittedSet_AndReusesTheScan()
    {
        _store.Seed(MakeShift(Today.AddDays(3)), MakeShift(Today.AddDays(30)));

        var events = await _sut.DetectAsync();
        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        Assert.That(fingerprints, Is.EquivalentTo(events.Select(AgentConditionLedgerPolicy.FingerprintFor)));
        Assert.That(fingerprints.Single(), Is.EqualTo("open_order:2026-10-22"));
        await _groupScopeReader.Received(1).GetGroupIdsByShiftIdsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetActiveFingerprintsAsync_NeverContainsAPerOrderFingerprintOfTheFormerShape()
    {
        var order = MakeShift(Today.AddDays(3));
        _store.Seed(order);
        var formerFingerprint = AgentConditionLedgerPolicy.FingerprintFor(
            AgentTriggerKinds.OpenOrder, $"{order.Id}:{order.FromDate:yyyy-MM-dd}");

        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        Assert.That(fingerprints, Does.Not.Contain(formerFingerprint));
    }
}
