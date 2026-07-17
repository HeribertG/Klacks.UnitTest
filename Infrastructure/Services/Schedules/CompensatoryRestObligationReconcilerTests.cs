// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for CompensatoryRestObligationReconciler (K12): obligation creation from rest-shortfall gaps,
/// idempotency, snapshot-anchored fulfilment (stable against later policy changes), soft-delete on
/// repaired plans, cross-midnight handling, and the enabled/analyseToken guards.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Scheduling;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class CompensatoryRestObligationReconcilerTests
{
    private static readonly DateOnly Jun1 = new(2026, 6, 1);
    private static readonly DateOnly WindowStart = new(2026, 5, 20);
    private static readonly DateOnly WindowEnd = new(2026, 7, 15);

    private DataBaseContext _context = null!;
    private ISchedulingPolicyResolver _policyResolver = null!;
    private ISettingsReader _settingsReader = null!;
    private CompensatoryRestObligationReconciler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);

        _policyResolver = Substitute.For<ISchedulingPolicyResolver>();
        SetMinRestHours(11);

        _settingsReader = Substitute.For<ISettingsReader>();
        SetEnabled(true);
        SetDeadlineDays(3);

        var timelineService = new TimelineCalculationService(
            Options.Create(new ScheduleTimeOptions()),
            NullLogger<TimelineCalculationService>.Instance);

        _sut = new CompensatoryRestObligationReconciler(
            _context,
            new CompensatoryRestObligationRepository(_context),
            _policyResolver,
            timelineService,
            _settingsReader);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task Reconcile_ShortRestGap_CreatesObligationWithSnapshotAndDeadline()
    {
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Jun1, new TimeOnly(8, 0), new TimeOnly(20, 0));
        SeedWork(clientId, Jun1.AddDays(1), new TimeOnly(4, 0), new TimeOnly(12, 0));

        await Reconcile(clientId);

        var obligation = ActiveObligations(clientId).ShouldHaveSingleItem();
        obligation.TriggerDate.ShouldBe(Jun1);
        obligation.RestGapStart.ShouldBe(Jun1.ToDateTime(new TimeOnly(20, 0)));
        obligation.StandardRestHours.ShouldBe(11m);
        obligation.ShortfallHours.ShouldBe(3m);
        obligation.DueDate.ShouldBe(Jun1.AddDays(3));
        obligation.FulfilledOn.ShouldBeNull();
    }

    [Test]
    public async Task Reconcile_RunTwice_ProducesExactlyOneRowWithStableValues()
    {
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Jun1, new TimeOnly(8, 0), new TimeOnly(20, 0));
        SeedWork(clientId, Jun1.AddDays(1), new TimeOnly(4, 0), new TimeOnly(12, 0));

        await Reconcile(clientId);
        var afterFirst = ActiveObligations(clientId).ShouldHaveSingleItem();
        var gapStart = afterFirst.RestGapStart;
        var shortfall = afterFirst.ShortfallHours;

        await Reconcile(clientId);

        var afterSecond = ActiveObligations(clientId).ShouldHaveSingleItem();
        afterSecond.RestGapStart.ShouldBe(gapStart);
        afterSecond.ShortfallHours.ShouldBe(shortfall);
        afterSecond.FulfilledOn.ShouldBeNull();
    }

    [Test]
    public async Task Reconcile_FollowingBlockMovesLater_UpdatesShortfallNotRow()
    {
        var clientId = Guid.NewGuid();
        var day1 = SeedWork(clientId, Jun1, new TimeOnly(8, 0), new TimeOnly(20, 0));
        var day2 = SeedWork(clientId, Jun1.AddDays(1), new TimeOnly(4, 0), new TimeOnly(12, 0));
        await Reconcile(clientId);
        ActiveObligations(clientId).Single().ShortfallHours.ShouldBe(3m);

        day2.StartTime = new TimeOnly(6, 0);
        day2.EndTime = new TimeOnly(14, 0);
        _context.Work.Update(day2);
        await _context.SaveChangesAsync();

        await Reconcile(clientId);

        var obligation = ActiveObligations(clientId).ShouldHaveSingleItem();
        obligation.ShortfallHours.ShouldBe(1m);
        _ = day1;
    }

    [Test]
    public async Task Reconcile_TriggerGapRepaired_SoftDeletesObligation()
    {
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Jun1, new TimeOnly(8, 0), new TimeOnly(20, 0));
        var day2 = SeedWork(clientId, Jun1.AddDays(1), new TimeOnly(4, 0), new TimeOnly(12, 0));
        await Reconcile(clientId);
        ActiveObligations(clientId).ShouldHaveSingleItem();

        day2.StartTime = new TimeOnly(7, 0);
        day2.EndTime = new TimeOnly(15, 0);
        _context.Work.Update(day2);
        await _context.SaveChangesAsync();

        await Reconcile(clientId);

        ActiveObligations(clientId).ShouldBeEmpty();
        AllObligations(clientId).ShouldHaveSingleItem().IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task Reconcile_LaterExtendedRestBeforeDeadline_MarksFulfilledOnGapDate()
    {
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Jun1, new TimeOnly(8, 0), new TimeOnly(20, 0));
        SeedWork(clientId, Jun1.AddDays(1), new TimeOnly(4, 0), new TimeOnly(12, 0));
        SeedWork(clientId, Jun1.AddDays(3), new TimeOnly(8, 0), new TimeOnly(16, 0));

        await Reconcile(clientId);

        var obligation = ActiveObligations(clientId).ShouldHaveSingleItem();
        obligation.FulfilledOn.ShouldBe(Jun1.AddDays(1));
    }

    [Test]
    public async Task Reconcile_ExtendedRestAfterDeadline_DoesNotFulfil()
    {
        SetDeadlineDays(1);
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Jun1, new TimeOnly(8, 0), new TimeOnly(20, 0));
        SeedWork(clientId, Jun1.AddDays(1), new TimeOnly(4, 0), new TimeOnly(12, 0));
        // Intermediate exact-minimum rest (11h at Jun2 12:00): not a violation, not a fulfilment; its
        // cross-midnight end pushes the next gap's start onto Jun3.
        SeedWork(clientId, Jun1.AddDays(1), new TimeOnly(23, 0), new TimeOnly(7, 0));
        // First >= threshold gap therefore starts on Jun3, strictly after the Jun2 deadline.
        SeedWork(clientId, Jun1.AddDays(4), new TimeOnly(8, 0), new TimeOnly(16, 0));

        await Reconcile(clientId);

        var obligation = ActiveObligations(clientId).ShouldHaveSingleItem();
        obligation.TriggerDate.ShouldBe(Jun1);
        obligation.FulfilledOn.ShouldBeNull();
    }

    [Test]
    public async Task Reconcile_FulfilmentThreshold_UsesSnapshotNotLivePolicy()
    {
        SetDeadlineDays(30);
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Jun1, new TimeOnly(8, 0), new TimeOnly(20, 0));
        SeedWork(clientId, Jun1.AddDays(1), new TimeOnly(4, 0), new TimeOnly(12, 0));
        await Reconcile(clientId);
        var created = ActiveObligations(clientId).ShouldHaveSingleItem();
        created.StandardRestHours.ShouldBe(11m);
        created.ShortfallHours.ShouldBe(3m);

        // Policy relaxes to 9h AND a 12h later rest appears. A live-policy threshold (9 + 1 = 10) would
        // treat 12h as fulfilling; the snapshot threshold (11 + 3 = 14) must not.
        SetMinRestHours(9);
        SeedWork(clientId, Jun1.AddDays(2), new TimeOnly(0, 0), new TimeOnly(8, 0));

        await Reconcile(clientId);

        var obligation = ActiveObligations(clientId).ShouldHaveSingleItem();
        obligation.StandardRestHours.ShouldBe(11m);
        obligation.ShortfallHours.ShouldBe(3m);
        obligation.FulfilledOn.ShouldBeNull();
    }

    [Test]
    public async Task Reconcile_CrossMidnightPreviousShift_KeysGapOnActualEnd()
    {
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Jun1, new TimeOnly(22, 0), new TimeOnly(6, 0));
        SeedWork(clientId, Jun1.AddDays(1), new TimeOnly(12, 0), new TimeOnly(20, 0));

        await Reconcile(clientId);

        var obligation = ActiveObligations(clientId).ShouldHaveSingleItem();
        obligation.TriggerDate.ShouldBe(Jun1);
        obligation.RestGapStart.ShouldBe(Jun1.AddDays(1).ToDateTime(new TimeOnly(6, 0)));
        obligation.ShortfallHours.ShouldBe(5m);
    }

    [Test]
    public async Task Reconcile_FeatureDisabled_IsNoOp()
    {
        SetEnabled(false);
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Jun1, new TimeOnly(8, 0), new TimeOnly(20, 0));
        SeedWork(clientId, Jun1.AddDays(1), new TimeOnly(4, 0), new TimeOnly(12, 0));

        await Reconcile(clientId);

        AllObligations(clientId).ShouldBeEmpty();
    }

    [Test]
    public async Task Reconcile_ScenarioToken_IsNoOp()
    {
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Jun1, new TimeOnly(8, 0), new TimeOnly(20, 0));
        SeedWork(clientId, Jun1.AddDays(1), new TimeOnly(4, 0), new TimeOnly(12, 0));

        await _sut.ReconcileAsync(clientId, WindowStart, WindowEnd, Guid.NewGuid());

        AllObligations(clientId).ShouldBeEmpty();
    }

    [Test]
    public async Task Reconcile_ViolationInExtendedFulfilmentRegion_DoesNotDuplicateExistingObligation()
    {
        // Obligation A (trigger Jun10) stays open, its deadline reaches into the extended region. Obligation
        // B (trigger Jun12) already exists but lies outside the narrow check window, so it is NOT loaded.
        // The narrow reconcile extends the timeline forward for A's fulfilment scan and re-detects B's gap —
        // it must NOT create a second B row (partial unique index would otherwise reject it).
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Jun1.AddDays(9), new TimeOnly(8, 0), new TimeOnly(20, 0));   // WA1 Jun10
        SeedWork(clientId, Jun1.AddDays(10), new TimeOnly(4, 0), new TimeOnly(12, 0));  // WA2 Jun11 -> gap A 8h
        SeedWork(clientId, Jun1.AddDays(11), new TimeOnly(0, 0), new TimeOnly(8, 0));   // WB1 Jun12 (12h rest, no fulfil)
        SeedWork(clientId, Jun1.AddDays(11), new TimeOnly(14, 0), new TimeOnly(22, 0)); // WB2 Jun12 -> gap B 6h

        // Wide pass creates both A and B (both open).
        await _sut.ReconcileAsync(clientId, Jun1, Jun1.AddDays(40), null);
        ActiveObligations(clientId).Count.ShouldBe(2);

        // Narrow pass: window ends Jun11, so B (trigger Jun12) is unloaded but re-detected in the extended region.
        await _sut.ReconcileAsync(clientId, Jun1.AddDays(4), Jun1.AddDays(10), null);

        var bKey = Jun1.AddDays(11).ToDateTime(new TimeOnly(8, 0));
        ActiveObligations(clientId).Count(o => o.RestGapStart == bKey).ShouldBe(1);
        ActiveObligations(clientId).Count.ShouldBe(2);
    }

    private Task Reconcile(Guid clientId) => _sut.ReconcileAsync(clientId, WindowStart, WindowEnd, null);

    private List<CompensatoryRestObligation> ActiveObligations(Guid clientId) =>
        _context.CompensatoryRestObligation.Where(o => o.ClientId == clientId && !o.IsDeleted).ToList();

    private List<CompensatoryRestObligation> AllObligations(Guid clientId) =>
        _context.CompensatoryRestObligation.Where(o => o.ClientId == clientId).ToList();

    private Klacks.Api.Domain.Models.Schedules.Work SeedWork(Guid clientId, DateOnly date, TimeOnly start, TimeOnly end)
    {
        var work = new Klacks.Api.Domain.Models.Schedules.Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = Guid.NewGuid(),
            CurrentDate = date,
            StartTime = start,
            EndTime = end,
            WorkTime = 8m,
        };
        _context.Work.Add(work);
        _context.SaveChanges();
        return work;
    }

    private void SetMinRestHours(double hours)
    {
        var policy = new SchedulingPolicy(
            TimeSpan.FromHours(hours),
            TimeSpan.FromHours(24),
            999,
            TimeSpan.FromHours(168),
            0);
        _policyResolver.GetForClientAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>()).Returns(policy);
    }

    private void SetEnabled(bool enabled) =>
        _settingsReader.GetSetting(SettingKeys.ComplianceCompensatoryRestEnabled)
            .Returns(new SettingsModel { Type = SettingKeys.ComplianceCompensatoryRestEnabled, Value = enabled ? "true" : "false" });

    private void SetDeadlineDays(int days) =>
        _settingsReader.GetSetting(SettingKeys.ComplianceCompensatoryRestDeadlineDays)
            .Returns(new SettingsModel { Type = SettingKeys.ComplianceCompensatoryRestDeadlineDays, Value = days.ToString() });
}
