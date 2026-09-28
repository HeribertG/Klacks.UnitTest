// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the inbox rules of chat-requested analyses: default scope stores today's snapshot, an admin
/// requester is left to the detector, a planner requester gets an own entry, a subtree run notifies only
/// the requester, and a report without report findings notifies nobody. A group-restricted requester's entry
/// carries only what that requester may see (requester snapshot), while the stored daily snapshot keeps the
/// whole installation for the admins. An apply that removed the snapshot during the analysis keeps the
/// pre-apply result out of the store; the requester then gets an own entry even as admin.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Microsoft.Extensions.Caching.Memory;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class GroupingFeasibilityNotifierTests
{
    private const int CacheSizeLimit = 10;
    private static readonly DateOnly Today = new(2026, 9, 27);

    private IAgentTriggerService _triggers = null!;
    private MemoryCache _cache = null!;
    private GroupingFeasibilityDailySnapshotStore _store = null!;
    private GroupingFeasibilityNotifier _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _triggers = Substitute.For<IAgentTriggerService>();
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = CacheSizeLimit });
        _store = new GroupingFeasibilityDailySnapshotStore(_cache);
        var clock = Substitute.For<ICompanyClock>();
        clock.GetTodayDateAsync(Arg.Any<CancellationToken>()).Returns(Today);
        _sut = new GroupingFeasibilityNotifier(_triggers, _store, clock);
    }

    [TearDown]
    public void TearDown() => _cache.Dispose();

    private static GroupingFeasibilityReport Report(Guid? scope = null, bool withFinding = true)
    {
        IReadOnlyList<GroupingFinding> findings = withFinding
            ? [new GroupingFinding(GroupingFindingCode.ClientFitsNoShift, ReportOnly: true, ClientId: Guid.NewGuid())]
            : [];
        return new GroupingFeasibilityReport(
            new GroupingAnalysisRequest(Today, Today.AddDays(GroupingFeasibilityDefaults.DefaultHorizonDays), scope),
            findings, [], "x", GroupingFingerprint.ForReport(findings),
            new Dictionary<Guid, string>(), new Dictionary<Guid, IReadOnlyList<Guid>>(), new Dictionary<Guid, IReadOnlyList<Guid>>(), new Dictionary<Guid, string>(), new Dictionary<Guid, string>(), 1, 1);
    }

    [Test]
    public async Task DefaultScope_AdminRequester_StoresTodaysSnapshotAndLeavesDeliveryToTheDetector()
    {
        var report = Report();

        await _sut.NotifyAsync(report, Full(report), Guid.NewGuid(), requesterIsAdmin: true, _store.CurrentGeneration, CancellationToken.None);

        _store.TryGet(GroupingFeasibilityDay.KeyFor(Today), out _).ShouldNotBeNull();
        await _triggers.DidNotReceiveWithAnyArgs().OnEventAsync(default!, default);
    }

    [Test]
    public async Task DefaultScope_PlannerRequester_GetsAnOwnEntry()
    {
        var requester = Guid.NewGuid();

        var report = Report();

        await _sut.NotifyAsync(report, Full(report), requester, requesterIsAdmin: false, _store.CurrentGeneration, CancellationToken.None);

        _store.TryGet(GroupingFeasibilityDay.KeyFor(Today), out _).ShouldNotBeNull();
        await _triggers.Received(1).OnEventAsync(
            Arg.Is<GroupingFeasibilityRequesterTriggerEvent>(e => e.RequesterId == requester), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Subtree_DoesNotTouchTheDailySnapshot()
    {
        var report = Report(scope: Guid.NewGuid());

        await _sut.NotifyAsync(report, Full(report), Guid.NewGuid(), requesterIsAdmin: true, _store.CurrentGeneration, CancellationToken.None);

        _store.TryGet(GroupingFeasibilityDay.KeyFor(Today), out _).ShouldBeNull();
        await _triggers.ReceivedWithAnyArgs(1).OnEventAsync(default!, default);
    }

    [Test]
    public async Task NoReportFindings_NotifiesNobody()
    {
        var report = Report(withFinding: false);

        await _sut.NotifyAsync(report, Full(report), Guid.NewGuid(), requesterIsAdmin: false, _store.CurrentGeneration, CancellationToken.None);

        await _triggers.DidNotReceiveWithAnyArgs().OnEventAsync(default!, default);
    }

    [Test]
    public async Task RestrictedRequester_WithOnlyForeignReportFindings_GetsNoEntry_ButTheDailySnapshotKeepsTheWholeInstallation()
    {
        var report = Report();
        var nothingVisible = new GroupingFeasibilityDailySnapshot(
            GroupingFingerprint.ForReport([]), GroupingFeasibilityCounts.From([], 0), HasReportFindings: false);

        await _sut.NotifyAsync(report, nothingVisible, Guid.NewGuid(), requesterIsAdmin: false, _store.CurrentGeneration, CancellationToken.None);

        await _triggers.DidNotReceiveWithAnyArgs().OnEventAsync(default!, default);
        _store.TryGet(GroupingFeasibilityDay.KeyFor(Today), out _)!.Counts.UnmatchedClients.ShouldBe(1);
    }

    [Test]
    public async Task RestrictedRequester_EntryCarriesTheRequesterCountsAndFingerprint()
    {
        var report = Report();
        var scoped = new GroupingFeasibilityDailySnapshot("scoped", new GroupingFeasibilityCounts(2, 0, 0, 7), HasReportFindings: true);

        await _sut.NotifyAsync(report, scoped, Guid.NewGuid(), requesterIsAdmin: false, _store.CurrentGeneration, CancellationToken.None);

        await _triggers.Received(1).OnEventAsync(
            Arg.Is<GroupingFeasibilityRequesterTriggerEvent>(e => e.Counts == scoped.Counts && e.DedupKey == "scoped"),
            Arg.Any<CancellationToken>());
        _store.TryGet(GroupingFeasibilityDay.KeyFor(Today), out _)!.Counts.ShouldBe(GroupingFeasibilityCounts.From(report));
    }

    [Test]
    public async Task ApplyDuringTheChatAnalysis_PreApplyResultIsNotStored_AndTheAdminGetsAnOwnEntry()
    {
        var generationBeforeAnalysis = _sut.CaptureSnapshotGeneration();
        _store.Remove(GroupingFeasibilityDay.KeyFor(Today));
        var report = Report();

        await _sut.NotifyAsync(report, Full(report), Guid.NewGuid(), requesterIsAdmin: true, generationBeforeAnalysis, CancellationToken.None);

        _store.TryGet(GroupingFeasibilityDay.KeyFor(Today), out _).ShouldBeNull();
        await _triggers.ReceivedWithAnyArgs(1).OnEventAsync(default!, default);
    }

    private static GroupingFeasibilityDailySnapshot Full(GroupingFeasibilityReport report) => GroupingFeasibilityDailySnapshot.From(report);
}
