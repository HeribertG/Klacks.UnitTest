// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the daily detector: no data means no analysis and no event; the analysis runs once per company
/// day, later ticks of the same day reuse the snapshot and the next company day recomputes; only report findings raise an admin event whose dedup key is the
/// report fingerprint; the fingerprint scan returns exactly the event's fingerprint; a snapshot stored
/// by a chat run is reused without a new analysis. The resolve cases feed the scan into the real ledger
/// service, so an old report's row resolves when a new fingerprint appears or the report findings vanish.
/// The race cases pin that an analysis overlapped by an apply (snapshot removed meanwhile) never stores its
/// pre-apply result: the detector recomputes once on the committed data and stores only that.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Interfaces.Grouping;
using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class GroupingFeasibilityDetectorTests
{
    private const int CacheSizeLimit = 100;
    private static readonly DateOnly Monday = new(2026, 9, 28);

    private IGroupingFeasibilityDataSource _dataSource = null!;
    private IGroupingFeasibilityAnalyzer _analyzer = null!;
    private ICompanyClock _clock = null!;
    private MemoryCache _cache = null!;
    private GroupingFeasibilityDailySnapshotStore _store = null!;
    private GroupingFeasibilityDetector _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _dataSource = Substitute.For<IGroupingFeasibilityDataSource>();
        _analyzer = Substitute.For<IGroupingFeasibilityAnalyzer>();
        _clock = Substitute.For<ICompanyClock>();
        _clock.GetTodayDateAsync(Arg.Any<CancellationToken>()).Returns(Monday);
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = CacheSizeLimit });
        _store = new GroupingFeasibilityDailySnapshotStore(_cache);
        _sut = NewDetector();
    }

    [TearDown]
    public void TearDown() => _cache.Dispose();

    private GroupingFeasibilityDetector NewDetector() =>
        new(_dataSource, _analyzer, _store, _clock, NullLogger<GroupingFeasibilityDetector>.Instance);

    private static GroupingFeasibilityReport Report(bool withReportFinding)
    {
        IReadOnlyList<GroupingFinding> findings = withReportFinding
            ? [new GroupingFinding(GroupingFindingCode.ShiftUnfillableGlobally, ReportOnly: true, ShiftId: Guid.NewGuid())]
            : [];
        return new GroupingFeasibilityReport(
            new GroupingAnalysisRequest(Monday, Monday.AddDays(GroupingFeasibilityDefaults.DefaultHorizonDays), null),
            findings, [], "plan-fingerprint", GroupingFingerprint.ForReport(findings),
            new Dictionary<Guid, string>(), new Dictionary<Guid, IReadOnlyList<Guid>>(), new Dictionary<Guid, IReadOnlyList<Guid>>(), new Dictionary<Guid, string>(), new Dictionary<Guid, string>(), 1, 1);
    }

    private void StoreSnapshot(GroupingFeasibilityReport report) =>
        _store.Set(GroupingFeasibilityDay.KeyFor(Monday), GroupingFeasibilityDailySnapshot.From(report), _store.CurrentGeneration)
            .ShouldBeTrue();

    [Test]
    public async Task NoData_NoAnalysisAndNoEvent()
    {
        _dataSource.HasAnalysableDataAsync(Monday, Arg.Any<CancellationToken>()).Returns(false);

        (await _sut.DetectAsync()).ShouldBeEmpty();
        (await _sut.GetActiveFingerprintsAsync()).ShouldBeEmpty();
        await _analyzer.DidNotReceiveWithAnyArgs().AnalyzeAsync(default!, default);
    }

    [Test]
    public async Task ReportFindings_RaiseOneAdminEventKeyedByTheReportFingerprint()
    {
        _dataSource.HasAnalysableDataAsync(Monday, Arg.Any<CancellationToken>()).Returns(true);
        var report = Report(withReportFinding: true);
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>()).Returns(report);

        var events = await _sut.DetectAsync();

        var triggerEvent = events.ShouldHaveSingleItem().ShouldBeOfType<GroupingFeasibilityTriggerEvent>();
        triggerEvent.DedupKey.ShouldBe(report.ReportFingerprint);
        triggerEvent.AdminOnly.ShouldBeTrue();
        AgentConditionLedgerPolicy.IsLedgerTracked(triggerEvent).ShouldBeTrue();
    }

    [Test]
    public async Task ReportFindings_AnalyseTheInstallationFromCompanyTodayOverTheDefaultHorizon()
    {
        _dataSource.HasAnalysableDataAsync(Monday, Arg.Any<CancellationToken>()).Returns(true);
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>()).Returns(Report(withReportFinding: true));

        await _sut.DetectAsync();

        await _analyzer.Received(1).AnalyzeAsync(
            new GroupingAnalysisRequest(Monday, Monday.AddDays(GroupingFeasibilityDefaults.DefaultHorizonDays), null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FingerprintScan_ContainsTheEmittedEvent()
    {
        _dataSource.HasAnalysableDataAsync(Monday, Arg.Any<CancellationToken>()).Returns(true);
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>()).Returns(Report(withReportFinding: true));

        var triggerEvent = (await _sut.DetectAsync()).ShouldHaveSingleItem();
        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        fingerprints.ShouldBe([AgentConditionLedgerPolicy.FingerprintFor(triggerEvent)]);
    }

    [Test]
    public async Task SecondTickOnTheSameDay_ReusesTheSnapshot()
    {
        _dataSource.HasAnalysableDataAsync(Monday, Arg.Any<CancellationToken>()).Returns(true);
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>()).Returns(Report(withReportFinding: true));

        await _sut.DetectAsync();
        var nextTick = NewDetector();
        await nextTick.DetectAsync();
        var fingerprints = await nextTick.GetActiveFingerprintsAsync();

        await _analyzer.Received(1).AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>());
        fingerprints.ShouldHaveSingleItem().ShouldStartWith(AgentTriggerKinds.GroupingFeasibility);
    }

    [Test]
    public async Task NoReportFindings_NoEventAndNoActiveFingerprint()
    {
        _dataSource.HasAnalysableDataAsync(Monday, Arg.Any<CancellationToken>()).Returns(true);
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>()).Returns(Report(withReportFinding: false));

        (await _sut.DetectAsync()).ShouldBeEmpty();
        (await _sut.GetActiveFingerprintsAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task SnapshotStoredByAChatRun_IsUsedWithoutAnalysis()
    {
        StoreSnapshot(Report(withReportFinding: true));

        (await _sut.DetectAsync()).ShouldHaveSingleItem();
        await _analyzer.DidNotReceiveWithAnyArgs().AnalyzeAsync(default!, default);
        await _dataSource.DidNotReceiveWithAnyArgs().HasAnalysableDataAsync(default, default);
    }

    [Test]
    public async Task RemovedSnapshot_IsRecomputedOnTheNextTick()
    {
        StoreSnapshot(Report(withReportFinding: true));
        await _sut.DetectAsync();
        _store.Remove(GroupingFeasibilityDay.KeyFor(Monday));
        _dataSource.HasAnalysableDataAsync(Monday, Arg.Any<CancellationToken>()).Returns(true);
        var fresh = Report(withReportFinding: true);
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>()).Returns(fresh);

        var events = await NewDetector().DetectAsync();

        events.ShouldHaveSingleItem().DedupKey.ShouldBe(fresh.ReportFingerprint);
        await _analyzer.Received(1).AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ChangedReport_ResolvesTheOldFingerprintsLedgerRow()
    {
        var oldReport = Report(withReportFinding: true);
        var newReport = Report(withReportFinding: true);
        StoreSnapshot(oldReport);
        var oldFingerprint = AgentConditionLedgerPolicy.FingerprintFor(AgentTriggerKinds.GroupingFeasibility, oldReport.ReportFingerprint);
        (await NewDetector().GetActiveFingerprintsAsync()).ShouldBe([oldFingerprint]);

        StoreSnapshot(newReport);
        var active = await NewDetector().GetActiveFingerprintsAsync();

        active.ShouldBe([AgentConditionLedgerPolicy.FingerprintFor(AgentTriggerKinds.GroupingFeasibility, newReport.ReportFingerprint)]);
        (await ResolveAgainstLedgerAsync(oldFingerprint, active)).ShouldBeTrue();
    }

    [Test]
    public async Task ReportFindingsGone_ResolvesTheOpenLedgerRow()
    {
        var oldReport = Report(withReportFinding: true);
        StoreSnapshot(oldReport);
        var oldFingerprint = AgentConditionLedgerPolicy.FingerprintFor(AgentTriggerKinds.GroupingFeasibility, oldReport.ReportFingerprint);

        StoreSnapshot(Report(withReportFinding: false));
        var active = await NewDetector().GetActiveFingerprintsAsync();

        active.ShouldBeEmpty();
        (await ResolveAgainstLedgerAsync(oldFingerprint, active)).ShouldBeTrue();
    }

    [Test]
    public async Task UnchangedReport_KeepsTheOpenLedgerRow()
    {
        var report = Report(withReportFinding: true);
        StoreSnapshot(report);
        var fingerprint = AgentConditionLedgerPolicy.FingerprintFor(AgentTriggerKinds.GroupingFeasibility, report.ReportFingerprint);

        var active = await NewDetector().GetActiveFingerprintsAsync();

        (await ResolveAgainstLedgerAsync(fingerprint, active)).ShouldBeFalse();
    }

    [Test]
    public async Task NextCompanyDay_RecomputesAndResolvesTheFixedReport()
    {
        _dataSource.HasAnalysableDataAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(true);
        var stale = Report(withReportFinding: true);
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>()).Returns(stale);
        var staleFingerprint = AgentConditionLedgerPolicy.FingerprintFor(AgentTriggerKinds.GroupingFeasibility, stale.ReportFingerprint);
        (await _sut.GetActiveFingerprintsAsync()).ShouldBe([staleFingerprint]);

        var tuesday = Monday.AddDays(1);
        _clock.GetTodayDateAsync(Arg.Any<CancellationToken>()).Returns(tuesday);
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>()).Returns(Report(withReportFinding: false));
        var active = await NewDetector().GetActiveFingerprintsAsync();

        await _analyzer.Received(2).AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>());
        await _analyzer.Received(1).AnalyzeAsync(
            new GroupingAnalysisRequest(tuesday, tuesday.AddDays(GroupingFeasibilityDefaults.DefaultHorizonDays), null),
            Arg.Any<CancellationToken>());
        active.ShouldBeEmpty();
        (await ResolveAgainstLedgerAsync(staleFingerprint, active)).ShouldBeTrue();
    }

    [Test]
    public void DayKey_IsTheCompanyCalendarDay()
    {
        GroupingFeasibilityDay.KeyFor(new DateOnly(2026, 9, 27)).ShouldBe("2026-09-27");
        GroupingFeasibilityDay.KeyFor(Monday).ShouldBe("2026-09-28");
    }

    [Test]
    public void SnapshotStore_KeepsOneSnapshotPerDay()
    {
        var snapshot = GroupingFeasibilityDailySnapshot.From(Report(withReportFinding: true));

        _store.Set(GroupingFeasibilityDay.KeyFor(Monday), snapshot, _store.CurrentGeneration);

        _store.TryGet(GroupingFeasibilityDay.KeyFor(Monday), out _).ShouldBe(snapshot);
        _store.TryGet(GroupingFeasibilityDay.KeyFor(Monday.AddDays(1)), out _).ShouldBeNull();
    }

    [Test]
    public async Task ApplyDuringTheAnalysis_PreApplyResultIsNotStored_AndTheCommittedStateIsRecomputed()
    {
        _dataSource.HasAnalysableDataAsync(Monday, Arg.Any<CancellationToken>()).Returns(true);
        var preApply = Report(withReportFinding: true);
        var postApply = Report(withReportFinding: false);
        var calls = 0;
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls++;
            if (calls == 1)
            {
                _store.Remove(GroupingFeasibilityDay.KeyFor(Monday));
                return preApply;
            }

            return postApply;
        });

        var events = await _sut.DetectAsync();

        events.ShouldBeEmpty();
        calls.ShouldBe(2);
        _store.TryGet(GroupingFeasibilityDay.KeyFor(Monday), out _)!.ReportFingerprint.ShouldBe(postApply.ReportFingerprint);
    }

    [Test]
    public async Task ApplyDuringBothAnalyses_LastResultServesOnlyThisTick_AndNothingIsStored()
    {
        _dataSource.HasAnalysableDataAsync(Monday, Arg.Any<CancellationToken>()).Returns(true);
        var report = Report(withReportFinding: true);
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _store.Remove(GroupingFeasibilityDay.KeyFor(Monday));
            return report;
        });

        var events = await _sut.DetectAsync();
        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        events.ShouldHaveSingleItem().DedupKey.ShouldBe(report.ReportFingerprint);
        fingerprints.ShouldBe([AgentConditionLedgerPolicy.FingerprintFor(events[0])]);
        await _analyzer.Received(2).AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>());
        _store.TryGet(GroupingFeasibilityDay.KeyFor(Monday), out _).ShouldBeNull();
    }

    private static async Task<bool> ResolveAgainstLedgerAsync(string openFingerprint, IReadOnlySet<string> activeFingerprints)
    {
        var repository = Substitute.For<IAgentConditionRepository>();
        var openRow = new AgentCondition
        {
            Id = Guid.NewGuid(),
            TriggerKind = AgentTriggerKinds.GroupingFeasibility,
            Fingerprint = openFingerprint,
            Status = AgentConditionStatus.Reported,
        };
        repository.GetOpenByKindAsync(AgentTriggerKinds.GroupingFeasibility, Arg.Any<CancellationToken>()).Returns([openRow]);
        repository.TryTransitionAsync(
                Arg.Any<Guid>(), Arg.Any<AgentConditionStatus>(), Arg.Any<AgentConditionStatus>(),
                Arg.Any<AgentConditionTransitionFields?>(), Arg.Any<AgentConditionEvent>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var ledger = new AgentConditionLedgerService(repository, TimeProvider.System, NullLogger<AgentConditionLedgerService>.Instance);

        var resolved = await ledger.MarkResolvedAsync(AgentTriggerKinds.GroupingFeasibility, activeFingerprints);

        if (resolved == 0)
        {
            return false;
        }

        await repository.Received(1).TryTransitionAsync(
            openRow.Id, AgentConditionStatus.Reported, AgentConditionStatus.Resolved,
            Arg.Any<AgentConditionTransitionFields?>(), Arg.Any<AgentConditionEvent>(), Arg.Any<CancellationToken>());
        return true;
    }
}
