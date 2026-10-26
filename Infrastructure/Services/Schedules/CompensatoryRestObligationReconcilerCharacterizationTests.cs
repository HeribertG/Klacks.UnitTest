// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for CompensatoryRestObligationReconciler guards not pinned elsewhere: a missing or
/// non-positive deadline-days setting turns the pass into a no-op, and a short rest gap whose trigger lies
/// before the check window never creates an obligation.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Interfaces.Settings;
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
public class CompensatoryRestObligationReconcilerCharacterizationTests
{
    private static readonly DateOnly Jun1 = new(2026, 6, 1);

    private DataBaseContext _context = null!;
    private ISettingsReader _settingsReader = null!;
    private CompensatoryRestObligationReconciler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);

        var policyResolver = Substitute.For<ISchedulingPolicyResolver>();
        policyResolver.GetForClientAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>()).Returns(new SchedulingPolicy(
            TimeSpan.FromHours(11),
            TimeSpan.FromHours(24),
            999,
            TimeSpan.FromHours(168),
            0));

        _settingsReader = Substitute.For<ISettingsReader>();
        _settingsReader.GetSetting(SettingKeys.ComplianceCompensatoryRestEnabled)
            .Returns(new SettingsModel { Type = SettingKeys.ComplianceCompensatoryRestEnabled, Value = "true" });
        SetDeadlineDays("3");

        var timelineService = new TimelineCalculationService(
            Options.Create(new ScheduleTimeOptions()),
            NullLogger<TimelineCalculationService>.Instance);

        _sut = new CompensatoryRestObligationReconciler(
            _context,
            new CompensatoryRestObligationRepository(_context),
            policyResolver,
            timelineService,
            _settingsReader);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [TestCase("0")]
    [TestCase("-2")]
    [TestCase("abc")]
    public async Task Reconcile_NonPositiveOrUnparsableDeadlineDays_IsNoOp(string deadlineDays)
    {
        SetDeadlineDays(deadlineDays);
        var clientId = SeedShortRestGap();

        await _sut.ReconcileAsync(clientId, Jun1.AddDays(-10), Jun1.AddDays(30), null);

        AllObligations(clientId).ShouldBeEmpty();
    }

    [Test]
    public async Task Reconcile_ShortGapTriggeredBeforeTheCheckWindow_CreatesNoObligation()
    {
        var clientId = SeedShortRestGap();

        await _sut.ReconcileAsync(clientId, Jun1.AddDays(1), Jun1.AddDays(30), null);

        AllObligations(clientId).ShouldBeEmpty();
    }

    [Test]
    public async Task Reconcile_ShortGapTriggeredInsideTheCheckWindow_CreatesOneObligation()
    {
        var clientId = SeedShortRestGap();

        await _sut.ReconcileAsync(clientId, Jun1, Jun1.AddDays(30), null);

        var obligation = AllObligations(clientId).ShouldHaveSingleItem();
        obligation.TriggerDate.ShouldBe(Jun1);
        obligation.DueDate.ShouldBe(Jun1.AddDays(3));
        obligation.IsDeleted.ShouldBeFalse();
    }

    private Guid SeedShortRestGap()
    {
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Jun1, new TimeOnly(8, 0), new TimeOnly(20, 0));
        SeedWork(clientId, Jun1.AddDays(1), new TimeOnly(4, 0), new TimeOnly(12, 0));
        return clientId;
    }

    private List<CompensatoryRestObligation> AllObligations(Guid clientId) =>
        _context.CompensatoryRestObligation.Where(o => o.ClientId == clientId).ToList();

    private void SeedWork(Guid clientId, DateOnly date, TimeOnly start, TimeOnly end)
    {
        _context.Work.Add(new Klacks.Api.Domain.Models.Schedules.Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = Guid.NewGuid(),
            CurrentDate = date,
            StartTime = start,
            EndTime = end,
            WorkTime = 8m,
        });
        _context.SaveChanges();
    }

    private void SetDeadlineDays(string value) =>
        _settingsReader.GetSetting(SettingKeys.ComplianceCompensatoryRestDeadlineDays)
            .Returns(new SettingsModel { Type = SettingKeys.ComplianceCompensatoryRestDeadlineDays, Value = value });
}
