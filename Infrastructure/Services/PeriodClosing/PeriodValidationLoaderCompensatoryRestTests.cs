// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests the deliberate CQRS side effect of PeriodValidationLoader (K12): the close-load reconciles
/// each client's compensatory-rest obligations before evaluating them, so a period never re-edited
/// after a shortfall does not surface an obligation that has since been compensated.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Scheduling;
using Klacks.Api.Infrastructure.Services.PeriodClosing;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Services.PeriodClosing;

[TestFixture]
public class PeriodValidationLoaderCompensatoryRestTests
{
    private static readonly DateOnly From = new(2026, 6, 1);
    private static readonly DateOnly To = new(2026, 6, 30);

    private DataBaseContext _context = null!;
    private PeriodValidationLoader _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);

        var policy = new SchedulingPolicy(
            TimeSpan.FromHours(11), TimeSpan.FromHours(24), 999, TimeSpan.FromHours(168), 0);
        var policyResolver = Substitute.For<ISchedulingPolicyResolver>();
        policyResolver.GetForClientAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>()).Returns(policy);
        policyResolver.GetForClientsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>())
            .Returns(new Dictionary<Guid, SchedulingPolicy>());

        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(SettingKeys.ComplianceCompensatoryRestEnabled)
            .Returns(new SettingsModel { Type = SettingKeys.ComplianceCompensatoryRestEnabled, Value = "true" });
        settingsReader.GetSetting(SettingKeys.ComplianceCompensatoryRestDeadlineDays)
            .Returns(new SettingsModel { Type = SettingKeys.ComplianceCompensatoryRestDeadlineDays, Value = "14" });

        var enforcementResolver = Substitute.For<IComplianceEnforcementResolver>();
        enforcementResolver.GetModeAsync(ComplianceRuleNames.CompensatoryRest).Returns(RuleEnforcementMode.Warn);

        var repository = new CompensatoryRestObligationRepository(_context);
        var timelineService = new TimelineCalculationService(
            Options.Create(new ScheduleTimeOptions()), NullLogger<TimelineCalculationService>.Instance);

        var reconciler = new CompensatoryRestObligationReconciler(
            _context, repository, policyResolver, timelineService, settingsReader);
        var evaluator = new CompensatoryRestEvaluator(repository, enforcementResolver, settingsReader);

        var periodCapEvaluator = Substitute.For<IPeriodCapEvaluator>();
        periodCapEvaluator.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        var restDayRotationEvaluator = Substitute.For<IRestDayRotationEvaluator>();
        restDayRotationEvaluator.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        var counterRuleEvaluator = Substitute.For<ICounterRuleEvaluator>();
        counterRuleEvaluator.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());

        _sut = new PeriodValidationLoader(
            _context,
            timelineService,
            policyResolver,
            periodCapEvaluator,
            restDayRotationEvaluator,
            counterRuleEvaluator,
            reconciler,
            evaluator);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task LoadAsync_ReconcilesBeforeEvaluate_FulfilledObligationNoLongerSurfaces()
    {
        var clientId = Guid.NewGuid();
        _context.Client.Add(new Client { Id = clientId, Name = "Anna", FirstName = "A" });
        // Shortfall gap of 8h on Jun10, then a 44h compensatory rest before the Jun24 deadline.
        SeedWork(clientId, new(2026, 6, 10), new TimeOnly(8, 0), new TimeOnly(20, 0));
        SeedWork(clientId, new(2026, 6, 11), new TimeOnly(4, 0), new TimeOnly(12, 0));
        SeedWork(clientId, new(2026, 6, 13), new TimeOnly(8, 0), new TimeOnly(16, 0));

        // A still-open obligation that was never re-reconciled after the compensatory rest was planned.
        _context.CompensatoryRestObligation.Add(new CompensatoryRestObligation
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            TriggerDate = new(2026, 6, 10),
            RestGapStart = new DateOnly(2026, 6, 10).ToDateTime(new TimeOnly(20, 0)),
            ShortfallHours = 3m,
            StandardRestHours = 11m,
            DueDate = new(2026, 6, 24),
            FulfilledOn = null,
        });
        await _context.SaveChangesAsync();

        var issues = await _sut.LoadAsync(From, To, null);

        issues.ShouldNotContain(i =>
            i.Code == "CompensatoryRestDue" || i.Code == "CompensatoryRestOverdue");
        var obligation = _context.CompensatoryRestObligation.Single(o => o.ClientId == clientId);
        obligation.FulfilledOn.ShouldBe(new DateOnly(2026, 6, 11));
    }

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
}
