// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for RestrictedTimeWindowEvaluator (K16): season membership incl. year-boundary wrap, inclusive
/// season boundary days, exclusive time-window boundaries, cross-midnight spillover, group-tag scoping
/// (case-insensitive, validity window, soft-deleted GroupItem), empty-tag-matches-all, block-mode
/// escalation and the planned-slot projection carrying ShiftId.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class RestrictedTimeWindowEvaluatorTests
{
    private DataBaseContext _context = null!;
    private IRestrictedTimeWindowRuleRepository _ruleRepository = null!;
    private IComplianceEnforcementResolver _enforcementResolver = null!;
    private RestrictedTimeWindowEvaluator _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);

        _ruleRepository = Substitute.For<IRestrictedTimeWindowRuleRepository>();
        _ruleRepository.GetAllActiveAsync().Returns(new List<RestrictedTimeWindowRule>());

        _enforcementResolver = Substitute.For<IComplianceEnforcementResolver>();
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.RestrictedTimeWindow).Returns(RuleEnforcementMode.Warn);

        _sut = new RestrictedTimeWindowEvaluator(_ruleRepository, _context, _enforcementResolver);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private void StubRule(RestrictedTimeWindowRule rule) =>
        _ruleRepository.GetAllActiveAsync().Returns(new List<RestrictedTimeWindowRule> { rule });

    private static RestrictedTimeWindowRule MiddayBan(string tag = "") => new()
    {
        Id = Guid.NewGuid(),
        SeasonFromMonth = 6,
        SeasonFromDay = 15,
        SeasonToMonth = 9,
        SeasonToDay = 15,
        DailyStart = new TimeOnly(12, 30),
        DailyEnd = new TimeOnly(15, 0),
        AppliesToGroupTag = tag,
    };

    private Guid SeedWork(Guid clientId, Guid shiftId, DateOnly date, TimeOnly start, TimeOnly end)
    {
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = date,
            StartTime = start,
            EndTime = end,
            WorkTime = 8m,
        });
        _context.SaveChanges();
        return shiftId;
    }

    private void SeedGroupLink(string groupName, Guid shiftId, bool isDeleted = false, DateTime? validFrom = null, DateTime? validUntil = null)
    {
        var groupId = Guid.NewGuid();
        _context.Group.Add(new Group { Id = groupId, Name = groupName });
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = shiftId,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            AnalyseToken = null,
            IsDeleted = isDeleted,
        });
        _context.SaveChanges();
    }

    [Test]
    public async Task EvaluateAsync_NoActiveRules_ReturnsEmpty()
    {
        var result = await _sut.EvaluateAsync(Guid.NewGuid(), "Anna", new DateOnly(2026, 7, 1));

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_InSeasonOverlapEmptyTag_ReportsWarningWithParams()
    {
        StubRule(MiddayBan());
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Guid.NewGuid(), new DateOnly(2026, 7, 1), new TimeOnly(12, 0), new TimeOnly(16, 0));

        var entry = (await _sut.EvaluateAsync(clientId, "Anna", new DateOnly(2026, 7, 1))).ShouldHaveSingleItem();

        entry.Type.ShouldBe(ScheduleValidationType.Warning);
        entry.Comment.ShouldBe(ScheduleValidationKeys.RestrictedTimeWindow);
        entry.CommentParams["dailyStart"].ShouldBe("12:30");
        entry.CommentParams["dailyEnd"].ShouldBe("15:00");
        entry.CommentParams["seasonFrom"].ShouldBe("06-15");
        entry.CommentParams["seasonTo"].ShouldBe("09-15");
    }

    [Test]
    public async Task EvaluateAsync_OutOfSeason_ReturnsEmpty()
    {
        StubRule(MiddayBan());
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Guid.NewGuid(), new DateOnly(2026, 10, 1), new TimeOnly(12, 0), new TimeOnly(16, 0));

        (await _sut.EvaluateAsync(clientId, "Anna", new DateOnly(2026, 10, 1))).ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_WorkEndsExactlyAtWindowStart_ReturnsEmpty()
    {
        StubRule(MiddayBan());
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Guid.NewGuid(), new DateOnly(2026, 7, 1), new TimeOnly(8, 0), new TimeOnly(12, 30));

        (await _sut.EvaluateAsync(clientId, "Anna", new DateOnly(2026, 7, 1))).ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_WorkStartsExactlyAtWindowEnd_ReturnsEmpty()
    {
        StubRule(MiddayBan());
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Guid.NewGuid(), new DateOnly(2026, 7, 1), new TimeOnly(15, 0), new TimeOnly(18, 0));

        (await _sut.EvaluateAsync(clientId, "Anna", new DateOnly(2026, 7, 1))).ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_SeasonStartDayInclusive_ReportsWarning()
    {
        StubRule(MiddayBan());
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Guid.NewGuid(), new DateOnly(2026, 6, 15), new TimeOnly(12, 0), new TimeOnly(16, 0));

        (await _sut.EvaluateAsync(clientId, "Anna", new DateOnly(2026, 6, 15))).ShouldHaveSingleItem();
    }

    [Test]
    public async Task EvaluateAsync_SeasonEndDayInclusive_ReportsWarning()
    {
        StubRule(MiddayBan());
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Guid.NewGuid(), new DateOnly(2026, 9, 15), new TimeOnly(12, 0), new TimeOnly(16, 0));

        (await _sut.EvaluateAsync(clientId, "Anna", new DateOnly(2026, 9, 15))).ShouldHaveSingleItem();
    }

    [Test]
    public async Task EvaluateAsync_WrapSeasonDecember_ReportsWarning()
    {
        StubRule(WinterMiddayBan());
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Guid.NewGuid(), new DateOnly(2026, 12, 20), new TimeOnly(12, 0), new TimeOnly(16, 0));

        (await _sut.EvaluateAsync(clientId, "Anna", new DateOnly(2026, 12, 20))).ShouldHaveSingleItem();
    }

    [Test]
    public async Task EvaluateAsync_WrapSeasonJanuary_ReportsWarning()
    {
        StubRule(WinterMiddayBan());
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Guid.NewGuid(), new DateOnly(2027, 1, 10), new TimeOnly(12, 0), new TimeOnly(16, 0));

        (await _sut.EvaluateAsync(clientId, "Anna", new DateOnly(2027, 1, 10))).ShouldHaveSingleItem();
    }

    [Test]
    public async Task EvaluateAsync_WrapSeasonJune_ReturnsEmpty()
    {
        StubRule(WinterMiddayBan());
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Guid.NewGuid(), new DateOnly(2026, 6, 20), new TimeOnly(12, 0), new TimeOnly(16, 0));

        (await _sut.EvaluateAsync(clientId, "Anna", new DateOnly(2026, 6, 20))).ShouldBeEmpty();
    }

    private static RestrictedTimeWindowRule WinterMiddayBan() => new()
    {
        Id = Guid.NewGuid(),
        SeasonFromMonth = 11,
        SeasonFromDay = 15,
        SeasonToMonth = 2,
        SeasonToDay = 15,
        DailyStart = new TimeOnly(12, 0),
        DailyEnd = new TimeOnly(14, 0),
        AppliesToGroupTag = string.Empty,
    };

    [Test]
    public async Task EvaluateAsync_CrossMidnightIntoNextDayWindow_ReportsWarning()
    {
        StubRule(new RestrictedTimeWindowRule
        {
            Id = Guid.NewGuid(),
            SeasonFromMonth = 1,
            SeasonFromDay = 1,
            SeasonToMonth = 12,
            SeasonToDay = 31,
            DailyStart = new TimeOnly(5, 0),
            DailyEnd = new TimeOnly(7, 0),
            AppliesToGroupTag = string.Empty,
        });
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Guid.NewGuid(), new DateOnly(2026, 7, 1), new TimeOnly(22, 0), new TimeOnly(7, 0));

        var entry = (await _sut.EvaluateAsync(clientId, "Anna", new DateOnly(2026, 7, 1))).ShouldHaveSingleItem();
        entry.Date.ShouldBe(new DateOnly(2026, 7, 1));
    }

    [Test]
    public async Task EvaluateAsync_TaggedShiftInGroupCaseInsensitive_ReportsWarning()
    {
        StubRule(MiddayBan("outdoor"));
        var clientId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        SeedGroupLink("Outdoor", shiftId);
        SeedWork(clientId, shiftId, new DateOnly(2026, 7, 1), new TimeOnly(12, 0), new TimeOnly(16, 0));

        (await _sut.EvaluateAsync(clientId, "Anna", new DateOnly(2026, 7, 1))).ShouldHaveSingleItem();
    }

    [Test]
    public async Task EvaluateAsync_TaggedShiftNotInGroup_ReturnsEmpty()
    {
        StubRule(MiddayBan("outdoor"));
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Guid.NewGuid(), new DateOnly(2026, 7, 1), new TimeOnly(12, 0), new TimeOnly(16, 0));

        (await _sut.EvaluateAsync(clientId, "Anna", new DateOnly(2026, 7, 1))).ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_TaggedShiftSoftDeletedGroupItem_ReturnsEmpty()
    {
        StubRule(MiddayBan("outdoor"));
        var clientId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        SeedGroupLink("outdoor", shiftId, isDeleted: true);
        SeedWork(clientId, shiftId, new DateOnly(2026, 7, 1), new TimeOnly(12, 0), new TimeOnly(16, 0));

        (await _sut.EvaluateAsync(clientId, "Anna", new DateOnly(2026, 7, 1))).ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_TaggedShiftLinkValidityExpiredBeforeWork_ReturnsEmpty()
    {
        StubRule(MiddayBan("outdoor"));
        var clientId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        SeedGroupLink("outdoor", shiftId, validUntil: new DateTime(2026, 6, 1));
        SeedWork(clientId, shiftId, new DateOnly(2026, 7, 1), new TimeOnly(12, 0), new TimeOnly(16, 0));

        (await _sut.EvaluateAsync(clientId, "Anna", new DateOnly(2026, 7, 1))).ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_BlockMode_ReportsErrorWithEnforcementParam()
    {
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.RestrictedTimeWindow).Returns(RuleEnforcementMode.Block);
        StubRule(MiddayBan());
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Guid.NewGuid(), new DateOnly(2026, 7, 1), new TimeOnly(12, 0), new TimeOnly(16, 0));

        var entry = (await _sut.EvaluateAsync(clientId, "Anna", new DateOnly(2026, 7, 1))).ShouldHaveSingleItem();
        entry.Type.ShouldBe(ScheduleValidationType.Error);
        entry.CommentParams[ComplianceRuleNames.EnforcementRuleParamKey].ShouldBe(ComplianceRuleNames.RestrictedTimeWindow);
    }

    [Test]
    public async Task EvaluatePlannedAsync_TaggedShiftInScope_ReportsWarning()
    {
        StubRule(MiddayBan("outdoor"));
        var shiftId = Guid.NewGuid();
        SeedGroupLink("outdoor", shiftId);

        var slots = new List<(DateOnly, TimeOnly, TimeOnly, Guid?)>
        {
            (new DateOnly(2026, 7, 1), new TimeOnly(12, 0), new TimeOnly(16, 0), shiftId),
        };
        (await _sut.EvaluatePlannedAsync(Guid.NewGuid(), "Anna", slots)).ShouldHaveSingleItem();
    }

    [Test]
    public async Task EvaluatePlannedAsync_EmptyTagMatchesSlotWithoutShiftId_ReportsWarning()
    {
        StubRule(MiddayBan());

        var slots = new List<(DateOnly, TimeOnly, TimeOnly, Guid?)>
        {
            (new DateOnly(2026, 7, 1), new TimeOnly(12, 0), new TimeOnly(16, 0), (Guid?)null),
        };
        (await _sut.EvaluatePlannedAsync(Guid.NewGuid(), "Anna", slots)).ShouldHaveSingleItem();
    }

    [Test]
    public async Task EvaluateRangeAsync_ViolationOnFirstDayOfRange_IsReported()
    {
        StubRule(MiddayBan());
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Guid.NewGuid(), new DateOnly(2026, 7, 1), new TimeOnly(12, 0), new TimeOnly(16, 0));

        var entry = (await _sut.EvaluateRangeAsync(clientId, "Anna", new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31)))
            .ShouldHaveSingleItem();
        entry.Date.ShouldBe(new DateOnly(2026, 7, 1));
    }

    [Test]
    public async Task EvaluateAsync_SingleDate_DoesNotReportViolationsOnOtherDays()
    {
        StubRule(MiddayBan());
        var clientId = Guid.NewGuid();
        SeedWork(clientId, Guid.NewGuid(), new DateOnly(2026, 7, 1), new TimeOnly(12, 0), new TimeOnly(16, 0));

        (await _sut.EvaluateAsync(clientId, "Anna", new DateOnly(2026, 7, 31))).ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_ScenarioClonedGroupItem_TaggedRuleStillFires()
    {
        // Regression: a scenario clones the shift under a FRESH id and clones its GroupItem link to that
        // clone id under the scenario token (AnalyseScenarioService.CloneShifts). The cloned work carries
        // the clone shift id + token. Reading GroupItem by shift id with NO token filter must still resolve
        // the tag - a token == null filter would blind every tagged rule in scenario mode.
        var token = Guid.NewGuid();
        StubRule(MiddayBan("outdoor"));
        var clientId = Guid.NewGuid();
        var cloneShiftId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        _context.Group.Add(new Group { Id = groupId, Name = "outdoor" });
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = cloneShiftId,
            AnalyseToken = token,
            IsDeleted = false,
        });
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = cloneShiftId,
            CurrentDate = new DateOnly(2026, 7, 1),
            StartTime = new TimeOnly(12, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            AnalyseToken = token,
        });
        _context.SaveChanges();

        (await _sut.EvaluateAsync(clientId, "Anna", new DateOnly(2026, 7, 1), token)).ShouldHaveSingleItem();
    }
}
