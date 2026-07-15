// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for RestDayRotationEvaluator (K10): free-weekday counting over the trailing window,
/// cross-midnight spillover, membership clamp, planned-slot projection and block-mode escalation.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class RestDayRotationEvaluatorTests
{
    private static readonly DateOnly Sunday1 = new(2026, 6, 21);
    private static readonly DateOnly Sunday2 = new(2026, 6, 28);
    private static readonly DateOnly Sunday3 = new(2026, 7, 5);
    private static readonly DateOnly Sunday4 = new(2026, 7, 12);

    private DataBaseContext _context = null!;
    private IRestDayRotationRuleRepository _ruleRepository = null!;
    private IComplianceEnforcementResolver _enforcementResolver = null!;
    private IClientMembershipStartResolver _membershipStartResolver = null!;
    private IClientContractDataProvider _contractDataProvider = null!;
    private RestDayRotationEvaluator _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);

        _ruleRepository = Substitute.For<IRestDayRotationRuleRepository>();
        _ruleRepository.GetAllActiveAsync().Returns(new List<RestDayRotationRule>());

        _enforcementResolver = Substitute.For<IComplianceEnforcementResolver>();
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.RestDayRotation).Returns(RuleEnforcementMode.Warn);

        _membershipStartResolver = Substitute.For<IClientMembershipStartResolver>();
        _membershipStartResolver.GetValidFromAsync(Arg.Any<Guid>()).Returns((DateOnly?)null);

        _contractDataProvider = Substitute.For<IClientContractDataProvider>();
        _contractDataProvider
            .GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData());

        _sut = new RestDayRotationEvaluator(_ruleRepository, _context, _enforcementResolver, _membershipStartResolver, _contractDataProvider);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task EvaluateAsync_NoActiveRules_ReturnsEmpty()
    {
        var result = await _sut.EvaluateAsync(Guid.NewGuid(), "Anna", Sunday4);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_AllSundaysWorked_ReportsViolation()
    {
        StubRule(DayOfWeek.Sunday, minFree: 2, windowWeeks: 4);
        var clientId = Guid.NewGuid();
        foreach (var sunday in new[] { Sunday1, Sunday2, Sunday3, Sunday4 })
        {
            SeedWork(clientId, sunday, new TimeOnly(8, 0), new TimeOnly(16, 0));
        }

        var result = await _sut.EvaluateAsync(clientId, "Anna", Sunday4);

        var entry = result.ShouldHaveSingleItem();
        entry.Type.ShouldBe(ScheduleValidationType.Warning);
        entry.Comment.ShouldBe(ScheduleValidationKeys.RestDayRotation);
        entry.Date.ShouldBe(Sunday4);
        entry.CommentParams["actualFree"].ShouldBe("0");
        entry.CommentParams["minFree"].ShouldBe("2");
        entry.CommentParams["windowWeeks"].ShouldBe("4");
        entry.CommentParams["dayOfWeek"].ShouldBe("Sunday");
    }

    [Test]
    public async Task EvaluateAsync_EnoughFreeSundays_ReturnsEmpty()
    {
        StubRule(DayOfWeek.Sunday, minFree: 2, windowWeeks: 4);
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Sunday1, new TimeOnly(8, 0), new TimeOnly(16, 0));
        SeedWork(clientId, Sunday2, new TimeOnly(8, 0), new TimeOnly(16, 0));

        var result = await _sut.EvaluateAsync(clientId, "Anna", Sunday4);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_CrossMidnightSaturdayShift_OccupiesTheSunday()
    {
        StubRule(DayOfWeek.Sunday, minFree: 2, windowWeeks: 4);
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Sunday1, new TimeOnly(8, 0), new TimeOnly(16, 0));
        SeedWork(clientId, Sunday2, new TimeOnly(8, 0), new TimeOnly(16, 0));
        SeedWork(clientId, Sunday3.AddDays(-1), new TimeOnly(22, 0), new TimeOnly(7, 0));

        var result = await _sut.EvaluateAsync(clientId, "Anna", Sunday4);

        var entry = result.ShouldHaveSingleItem();
        entry.CommentParams["actualFree"].ShouldBe("1");
    }

    [Test]
    public async Task EvaluateAsync_WindowReachesBeforeMembershipStart_SkipsEvaluation()
    {
        StubRule(DayOfWeek.Sunday, minFree: 2, windowWeeks: 4);
        var clientId = Guid.NewGuid();
        _membershipStartResolver.GetValidFromAsync(clientId).Returns(Sunday2);
        foreach (var sunday in new[] { Sunday2, Sunday3, Sunday4 })
        {
            SeedWork(clientId, sunday, new TimeOnly(8, 0), new TimeOnly(16, 0));
        }

        var result = await _sut.EvaluateAsync(clientId, "Anna", Sunday4);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_BlockMode_EscalatesToErrorWithEnforcementTag()
    {
        StubRule(DayOfWeek.Sunday, minFree: 1, windowWeeks: 2);
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.RestDayRotation).Returns(RuleEnforcementMode.Block);
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Sunday3, new TimeOnly(8, 0), new TimeOnly(16, 0));
        SeedWork(clientId, Sunday4, new TimeOnly(8, 0), new TimeOnly(16, 0));

        var result = await _sut.EvaluateAsync(clientId, "Anna", Sunday4);

        var entry = result.ShouldHaveSingleItem();
        entry.Type.ShouldBe(ScheduleValidationType.Error);
        entry.CommentParams[ComplianceRuleNames.EnforcementRuleParamKey].ShouldBe(ComplianceRuleNames.RestDayRotation);
    }

    [Test]
    public async Task EvaluatePlannedAsync_PlannedWorkOnLastFreeSunday_ReportsViolation()
    {
        StubRule(DayOfWeek.Sunday, minFree: 2, windowWeeks: 4);
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Sunday1, new TimeOnly(8, 0), new TimeOnly(16, 0));
        SeedWork(clientId, Sunday2, new TimeOnly(8, 0), new TimeOnly(16, 0));
        SeedWork(clientId, Sunday3, new TimeOnly(8, 0), new TimeOnly(16, 0));

        var result = await _sut.EvaluatePlannedAsync(
            clientId,
            "Anna",
            [(Sunday4, new TimeOnly(8, 0), new TimeOnly(16, 0))]);

        var entry = result.ShouldHaveSingleItem();
        entry.CommentParams["actualFree"].ShouldBe("0");
    }

    [Test]
    public async Task EvaluatePlannedAsync_OnlyCrossMidnightSaturdaySlot_ChecksTheOccupiedSundayWindow()
    {
        // Regression: the window anchor must follow the OCCUPIED day, not the slot's start day - a
        // Saturday night shift spilling into the last free Sunday must be reported even though the
        // planned slot itself is dated Saturday. Only Sunday2/Sunday3 are pre-occupied so the baseline
        // windows are still compliant and the single reported violation is attributable to the planned
        // cross-midnight slot alone.
        StubRule(DayOfWeek.Sunday, minFree: 2, windowWeeks: 4);
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Sunday2, new TimeOnly(8, 0), new TimeOnly(16, 0));
        SeedWork(clientId, Sunday3, new TimeOnly(8, 0), new TimeOnly(16, 0));

        var result = await _sut.EvaluatePlannedAsync(
            clientId,
            "Anna",
            [(Sunday4.AddDays(-1), new TimeOnly(22, 0), new TimeOnly(7, 0))]);

        var entry = result.ShouldHaveSingleItem();
        entry.Date.ShouldBe(Sunday4);
        entry.CommentParams["actualFree"].ShouldBe("1");
    }

    [Test]
    public async Task EvaluatePlannedAsync_WeekdayPlacementLeavingSundaysFree_ReturnsEmpty()
    {
        StubRule(DayOfWeek.Sunday, minFree: 2, windowWeeks: 4);
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Sunday1, new TimeOnly(8, 0), new TimeOnly(16, 0));
        SeedWork(clientId, Sunday2, new TimeOnly(8, 0), new TimeOnly(16, 0));

        var result = await _sut.EvaluatePlannedAsync(
            clientId,
            "Anna",
            [(Sunday4.AddDays(-3), new TimeOnly(8, 0), new TimeOnly(16, 0))]);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_IndustryScopedRule_AppliesOnlyToMatchingContractRule()
    {
        var boundRuleId = Guid.NewGuid();
        StubRule(DayOfWeek.Sunday, minFree: 2, windowWeeks: 4, schedulingRuleId: boundRuleId);
        var clientId = Guid.NewGuid();
        foreach (var sunday in new[] { Sunday1, Sunday2, Sunday3, Sunday4 })
        {
            SeedWork(clientId, sunday, new TimeOnly(8, 0), new TimeOnly(16, 0));
        }

        _contractDataProvider
            .GetEffectiveContractDataAsync(clientId, Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData { SchedulingRuleId = Guid.NewGuid() });
        (await _sut.EvaluateAsync(clientId, "Anna", Sunday4)).ShouldBeEmpty();

        _contractDataProvider
            .GetEffectiveContractDataAsync(clientId, Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData { SchedulingRuleId = boundRuleId });
        (await _sut.EvaluateAsync(clientId, "Anna", Sunday4)).ShouldHaveSingleItem();
    }

    private void StubRule(DayOfWeek dayOfWeek, int minFree, int windowWeeks, Guid? schedulingRuleId = null)
    {
        _ruleRepository.GetAllActiveAsync().Returns(new List<RestDayRotationRule>
        {
            new()
            {
                Id = Guid.NewGuid(),
                DayOfWeek = dayOfWeek,
                MinFreeCount = minFree,
                WindowWeeks = windowWeeks,
                SchedulingRuleId = schedulingRuleId,
            },
        });
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
