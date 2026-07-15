// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for PreCommitConflictChecker using an in-memory DataBaseContext, the real
/// TimelineCalculationService and a stubbed scheduling policy. Verifies that a planned placement's
/// NEW conflicts are detected (collision = blocking, rest = warning), that a clean placement returns
/// nothing, and that a pre-existing violation is NOT attributed to the placement (before/after diff).
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class PreCommitConflictCheckerTests
{
    private static readonly DateOnly Day = new(2026, 3, 10);
    private static readonly Guid ClientA = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private PreCommitConflictChecker _checker = null!;
    private IComplianceEnforcementResolver _enforcementResolver = null!;
    private ISettingsReader _settingsReader = null!;
    private IPeriodCapEvaluator _periodCapEvaluator = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        var timeOptions = Options.Create(new ScheduleTimeOptions());
        var timelineCalculator = new TimelineCalculationService(
            timeOptions, Substitute.For<ILogger<TimelineCalculationService>>());

        var resolver = Substitute.For<ISchedulingPolicyResolver>();
        resolver.GetForClientAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>())
            .Returns(new SchedulingPolicy(
                MinRestHours: TimeSpan.FromHours(11),
                MaxDailyHours: TimeSpan.FromHours(10),
                MaxConsecutiveDays: 6,
                MaxWeeklyHours: TimeSpan.FromHours(50),
                MinRestDays: 2));

        _enforcementResolver = Substitute.For<IComplianceEnforcementResolver>();
        _enforcementResolver.GetModeAsync(Arg.Any<string>()).Returns(RuleEnforcementMode.Warn);

        _settingsReader = Substitute.For<ISettingsReader>();

        _periodCapEvaluator = Substitute.For<IPeriodCapEvaluator>();
        _periodCapEvaluator
            .EvaluatePlannedAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<(DateOnly Date, decimal Hours)>>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns([]);

        _checker = new PreCommitConflictChecker(_context, timelineCalculator, resolver, _enforcementResolver, _settingsReader, _periodCapEvaluator);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private void SeedWork(Guid clientId, DateOnly date, TimeOnly start, TimeOnly end)
    {
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = Guid.NewGuid(),
            CurrentDate = date,
            StartTime = start,
            EndTime = end,
            WorkTime = 8,
            ParentWorkId = null,
            AnalyseToken = null
        });
        _context.SaveChanges();
    }

    private static PlannedWorkRow Row(TimeOnly start, TimeOnly end, DateOnly? date = null)
        => new(ClientA, date ?? Day, start, end);

    [Test]
    public async Task Collision_IsDetectedAsBlocking()
    {
        SeedWork(ClientA, Day, new TimeOnly(8, 0), new TimeOnly(16, 0));

        var result = await _checker.CheckAsync([Row(new TimeOnly(12, 0), new TimeOnly(20, 0))]);

        result.HasAny.ShouldBeTrue();
        result.HasBlocking.ShouldBeTrue();
        result.NewConflicts.ShouldContain(c =>
            c.Comment == "schedule.error-list.collision" && c.Type == ScheduleValidationType.Error);
    }

    [Test]
    public async Task RestViolation_IsWarningNotBlocking()
    {
        SeedWork(ClientA, Day, new TimeOnly(6, 0), new TimeOnly(14, 0));

        var result = await _checker.CheckAsync([Row(new TimeOnly(20, 0), new TimeOnly(23, 0))]);

        result.HasAny.ShouldBeTrue();
        result.HasBlocking.ShouldBeFalse();
        result.NewConflicts.ShouldContain(c =>
            c.Comment == "schedule.error-list.rest-violation" && c.Type == ScheduleValidationType.Warning);
    }

    [Test]
    public async Task CleanPlacement_ReturnsNoConflicts()
    {
        SeedWork(ClientA, Day, new TimeOnly(6, 0), new TimeOnly(14, 0));

        var result = await _checker.CheckAsync([Row(new TimeOnly(8, 0), new TimeOnly(16, 0), Day.AddDays(5))]);

        result.HasAny.ShouldBeFalse();
        result.NewConflicts.ShouldBeEmpty();
    }

    [Test]
    public async Task PreExistingViolation_IsNotAttributedToPlacement()
    {
        // Two already-colliding works on Day → the collision exists in the baseline.
        SeedWork(ClientA, Day, new TimeOnly(8, 0), new TimeOnly(16, 0));
        SeedWork(ClientA, Day, new TimeOnly(12, 0), new TimeOnly(20, 0));

        // Place an unrelated, conflict-free work far away.
        var result = await _checker.CheckAsync([Row(new TimeOnly(8, 0), new TimeOnly(16, 0), Day.AddDays(10))]);

        result.HasAny.ShouldBeFalse();
        result.NewConflicts.ShouldBeEmpty();
    }

    [Test]
    public async Task EmptyInput_ReturnsEmpty()
    {
        var result = await _checker.CheckAsync(Array.Empty<PlannedWorkRow>());

        result.HasAny.ShouldBeFalse();
    }

    [Test]
    public async Task AggregateWorsening_ReportedAsNew_ButStillNonBlocking()
    {
        // Documents the known diff behaviour for aggregate checks: the week (Mon 02 - Sat 07) is
        // already over MaxWeeklyHours (6 x 9h = 54h > 50h) in the baseline. Placing a 7th same-week
        // work (Sunday 08) pushes the bucket to 62h; because the key includes actualHours, the
        // worsened weekly-overtime is reported as NEW. It stays a Warning, so it never blocks.
        var monday = new DateOnly(2026, 3, 2);
        for (var i = 0; i < 6; i++)
        {
            SeedWork(ClientA, monday.AddDays(i), new TimeOnly(6, 0), new TimeOnly(15, 0));
        }

        var sunday = monday.AddDays(6);
        var result = await _checker.CheckAsync(
            [new PlannedWorkRow(ClientA, sunday, new TimeOnly(8, 0), new TimeOnly(16, 0))]);

        result.HasBlocking.ShouldBeFalse();
        result.NewConflicts.ShouldContain(c =>
            c.Comment == "schedule.error-list.weekly-overtime" && c.Type == ScheduleValidationType.Warning);
    }

    [Test]
    public async Task ScenarioRows_DoNotSeeRealWorks()
    {
        // A real work exists on Day; checking a scenario placement (analyseToken set) must not collide
        // with the real work, because scenario mode only loads rows with that token.
        SeedWork(ClientA, Day, new TimeOnly(8, 0), new TimeOnly(16, 0));

        var result = await _checker.CheckAsync(
            [Row(new TimeOnly(12, 0), new TimeOnly(20, 0))], analyseToken: Guid.NewGuid());

        result.HasAny.ShouldBeFalse();
    }

    [Test]
    public async Task BlockMode_EscalatesRestViolation_ToOverridableError()
    {
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.MinRestHours).Returns(RuleEnforcementMode.Block);
        SeedWork(ClientA, Day, new TimeOnly(6, 0), new TimeOnly(14, 0));

        var result = await _checker.CheckAsync([Row(new TimeOnly(20, 0), new TimeOnly(23, 0))]);

        result.HasBlocking.ShouldBeTrue();
        result.HasHardBlocking.ShouldBeFalse();
        result.HasOverridableBlocking.ShouldBeTrue();
        result.NewConflicts.ShouldContain(c =>
            c.Comment == "schedule.error-list.rest-violation"
            && c.Type == ScheduleValidationType.Error
            && c.CommentParams[ComplianceRuleNames.EnforcementRuleParamKey] == ComplianceRuleNames.MinRestHours);
    }

    [Test]
    public async Task WarnMode_LeavesRestViolation_AsNonOverridableWarning()
    {
        SeedWork(ClientA, Day, new TimeOnly(6, 0), new TimeOnly(14, 0));

        var result = await _checker.CheckAsync([Row(new TimeOnly(20, 0), new TimeOnly(23, 0))]);

        result.HasBlocking.ShouldBeFalse();
        result.HasOverridableBlocking.ShouldBeFalse();
    }

    [Test]
    public async Task Collision_IsNeverOverridable_EvenWhenBlockModeConfigured()
    {
        _enforcementResolver.GetModeAsync(Arg.Any<string>()).Returns(RuleEnforcementMode.Block);
        SeedWork(ClientA, Day, new TimeOnly(8, 0), new TimeOnly(16, 0));

        var result = await _checker.CheckAsync([Row(new TimeOnly(12, 0), new TimeOnly(20, 0))]);

        result.HasHardBlocking.ShouldBeTrue();
    }

    [Test]
    public async Task ExpiringSoonMandatoryQualification_AddsWarning_NeverBlocks()
    {
        var qualificationId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        _settingsReader.GetSetting(SettingKeys.QualificationExpiryWarningDays)
            .Returns(new Klacks.Api.Domain.Models.Settings.Settings { Type = SettingKeys.QualificationExpiryWarningDays, Value = "30" });

        _context.ShiftRequiredQualification.Add(new Klacks.Api.Domain.Models.Associations.ShiftRequiredQualification
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            QualificationId = qualificationId,
            IsMandatory = true,
            MinLevel = QualificationLevel.Basic
        });
        _context.ClientQualification.Add(new Klacks.Api.Domain.Models.Associations.ClientQualification
        {
            Id = Guid.NewGuid(),
            ClientId = ClientA,
            QualificationId = qualificationId,
            Level = QualificationLevel.Basic,
            ValidUntil = Day.AddDays(10)
        });
        _context.SaveChanges();

        var row = new PlannedWorkRow(ClientA, Day, new TimeOnly(8, 0), new TimeOnly(16, 0), shiftId);
        var result = await _checker.CheckAsync([row]);

        result.HasBlocking.ShouldBeFalse();
        result.NewConflicts.ShouldContain(c =>
            c.Comment == QualificationValidationKeys.ExpiringSoon && c.Type == ScheduleValidationType.Warning);
    }

    [Test]
    public async Task PeriodCapBreach_FromEvaluator_IsSurfacedAsWarning()
    {
        var periodCapEntry = new ScheduleValidationNotificationDto
        {
            Type = ScheduleValidationType.Warning,
            ClientId = ClientA,
            Date = Day,
            Comment = ScheduleValidationKeys.PeriodCap,
            CommentParams = new Dictionary<string, string> { ["actualHours"] = "210.0", ["capHours"] = "200" }
        };
        _periodCapEvaluator
            .EvaluatePlannedAsync(
                ClientA,
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<(DateOnly Date, decimal Hours)>>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns([periodCapEntry]);

        var result = await _checker.CheckAsync([Row(new TimeOnly(8, 0), new TimeOnly(16, 0), Day.AddDays(20))]);

        result.HasBlocking.ShouldBeFalse();
        result.NewConflicts.ShouldContain(c => c.Comment == ScheduleValidationKeys.PeriodCap && c.Type == ScheduleValidationType.Warning);
    }

    [Test]
    public async Task PeriodCapBreach_BlockEnforcementFromEvaluator_IsOverridableBlocking()
    {
        var periodCapEntry = new ScheduleValidationNotificationDto
        {
            Type = ScheduleValidationType.Error,
            ClientId = ClientA,
            Date = Day,
            Comment = ScheduleValidationKeys.PeriodCap,
            CommentParams = new Dictionary<string, string>
            {
                ["actualHours"] = "210.0",
                ["capHours"] = "200",
                [ComplianceRuleNames.EnforcementRuleParamKey] = ComplianceRuleNames.PeriodCap
            }
        };
        _periodCapEvaluator
            .EvaluatePlannedAsync(
                ClientA,
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<(DateOnly Date, decimal Hours)>>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns([periodCapEntry]);

        var result = await _checker.CheckAsync([Row(new TimeOnly(8, 0), new TimeOnly(16, 0), Day.AddDays(20))]);

        result.HasBlocking.ShouldBeTrue();
        result.HasOverridableBlocking.ShouldBeTrue();
        result.HasHardBlocking.ShouldBeFalse();
    }
}
