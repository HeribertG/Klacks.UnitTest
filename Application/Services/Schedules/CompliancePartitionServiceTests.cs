// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CompliancePartitionService: clean batch accepts everything in one check, greedy
/// per-row fallback blocks exactly the row that flips an aggregate check to Error (deterministic
/// client/date/start order), clean clients are accepted wholesale without per-row checks, hard
/// blocking is never overridable, an authorized batch override accepts everything with an audit log,
/// and ReportableConflicts carries the warnings plus overridden errors.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class CompliancePartitionServiceTests
{
    private const string CollisionKey = "schedule.error-list.collision";
    private const string WeeklyOvertimeKey = "schedule.error-list.weekly-overtime";
    private const string RestViolationKey = "schedule.error-list.rest-violation";
    private const string EnforcementRuleParamKey = "enforcementRule";
    private const string MinRestHoursRule = "minRestHours";

    private static readonly Guid ClientA = Guid.NewGuid();
    private static readonly Guid ClientB = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 4, 6);

    private IPreCommitConflictChecker _checker = null!;
    private ISupervisorOverrideAuthorizer _authorizer = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private ILogger<CompliancePartitionService> _logger = null!;
    private CompliancePartitionService _service = null!;

    [SetUp]
    public void Setup()
    {
        _checker = Substitute.For<IPreCommitConflictChecker>();
        _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(PreCommitCheckResult.Empty);
        _authorizer = Substitute.For<ISupervisorOverrideAuthorizer>();
        _authorizer.IsAuthorizedAsync(Arg.Any<bool>()).Returns(false);
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _logger = Substitute.For<ILogger<CompliancePartitionService>>();

        _service = new CompliancePartitionService(_checker, _authorizer, _httpContextAccessor, _logger);
    }

    private static PlannedWorkRow Row(Guid clientId, DateOnly date, int startHour = 8)
        => new(clientId, date, new TimeOnly(startHour, 0), new TimeOnly(startHour + 8, 0));

    private static ScheduleValidationNotificationDto ErrorEntry(Guid clientId, string comment, bool overridable = false)
        => new()
        {
            Type = ScheduleValidationType.Error,
            ClientId = clientId,
            Date = Day,
            Comment = comment,
            CommentParams = overridable
                ? new Dictionary<string, string> { [EnforcementRuleParamKey] = MinRestHoursRule }
                : new Dictionary<string, string>(),
        };

    private static ScheduleValidationNotificationDto WarningEntry(Guid clientId, string comment)
        => new()
        {
            Type = ScheduleValidationType.Warning,
            ClientId = clientId,
            Date = Day,
            Comment = comment,
        };

    private Task<CompliancePartitionResult> Partition(bool overrideBlock, params PlannedWorkRow[] rows)
        => _service.PartitionAsync(rows, analyseToken: null, overrideBlock, CancellationToken.None);

    private void AssertOverrideAuditLogged()
        => _logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Compliance override")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

    [Test]
    public async Task EmptyRows_ReturnsEmptyResult_WithoutCheck()
    {
        var result = await Partition(overrideBlock: false);

        result.AcceptedIndexes.ShouldBeEmpty();
        result.BlockedIndexes.ShouldBeEmpty();
        result.ReportableConflicts.ShouldBeEmpty();
        result.OverrideApplied.ShouldBeFalse();
        await _checker.DidNotReceive().CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CleanBatch_AcceptsAll_InSingleCheck_AndSurfacesWarnings()
    {
        _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([WarningEntry(ClientA, WeeklyOvertimeKey)]));

        var result = await Partition(overrideBlock: false, Row(ClientA, Day), Row(ClientB, Day));

        result.AcceptedIndexes.ShouldBe(new[] { 0, 1 });
        result.BlockedIndexes.ShouldBeEmpty();
        result.ReportableConflicts.Single().Comment.ShouldBe(WeeklyOvertimeKey);
        result.OverrideApplied.ShouldBeFalse();
        await _checker.Received(1).CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AggregateGreedy_AcceptsFirstRow_BlocksTheRowThatFlipsTheCheck()
    {
        // The mock flips to Error once a trial contains two or more rows of the same client
        // (aggregate semantics). Rows are passed in REVERSE date order to prove the deterministic
        // client/date sort decides which row survives: the earlier day is accepted, the later blocked.
        _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var rows = ci.Arg<IReadOnlyList<PlannedWorkRow>>();
                return rows.GroupBy(r => r.ClientId).Any(g => g.Count() >= 2)
                    ? new PreCommitCheckResult([ErrorEntry(ClientA, WeeklyOvertimeKey)])
                    : PreCommitCheckResult.Empty;
            });

        var laterDay = Row(ClientA, Day.AddDays(1));
        var earlierDay = Row(ClientA, Day);
        var result = await Partition(overrideBlock: false, laterDay, earlierDay);

        result.AcceptedIndexes.ShouldBe(new[] { 1 });
        var blocked = result.BlockedIndexes.ShouldHaveSingleItem();
        blocked.Index.ShouldBe(0);
        blocked.ReasonKey.ShouldBe(WeeklyOvertimeKey);
        result.OverrideApplied.ShouldBeFalse();
    }

    [Test]
    public async Task GreedyFallback_AcceptsCleanClientsWholesale_WithoutPerRowChecks()
    {
        _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var rows = ci.Arg<IReadOnlyList<PlannedWorkRow>>();
                var conflicts = new List<ScheduleValidationNotificationDto>();
                if (rows.Count(r => r.ClientId == ClientA) >= 2)
                {
                    conflicts.Add(ErrorEntry(ClientA, WeeklyOvertimeKey));
                }
                if (rows.Any(r => r.ClientId == ClientB))
                {
                    conflicts.Add(WarningEntry(ClientB, RestViolationKey));
                }
                return new PreCommitCheckResult(conflicts);
            });

        var result = await Partition(
            overrideBlock: false,
            Row(ClientA, Day),
            Row(ClientA, Day.AddDays(1)),
            Row(ClientB, Day));

        result.AcceptedIndexes.ShouldBe(new[] { 0, 2 });
        result.BlockedIndexes.ShouldHaveSingleItem().Index.ShouldBe(1);
        result.ReportableConflicts.Single().ClientId.ShouldBe(ClientB);
        // One batch check plus one trial per violating-client row; the clean client is never re-checked.
        await _checker.Received(3).CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _checker.DidNotReceive().CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => rows.Count == 1 && rows[0].ClientId == ClientB),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HardBlocking_IsNeverOverridable_EvenWhenAuthorized()
    {
        _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([ErrorEntry(ClientA, CollisionKey)]));
        _authorizer.IsAuthorizedAsync(true).Returns(true);

        var result = await Partition(overrideBlock: true, Row(ClientA, Day));

        result.AcceptedIndexes.ShouldBeEmpty();
        var blocked = result.BlockedIndexes.ShouldHaveSingleItem();
        blocked.ReasonKey.ShouldBe(CollisionKey);
        result.OverrideApplied.ShouldBeFalse();
    }

    [Test]
    public async Task AuthorizedOverride_AcceptsAll_MarksOverride_LogsAudit_AndReportsOverriddenError()
    {
        _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult(
            [
                ErrorEntry(ClientA, RestViolationKey, overridable: true),
                WarningEntry(ClientB, WeeklyOvertimeKey),
            ]));
        _authorizer.IsAuthorizedAsync(true).Returns(true);

        var result = await Partition(overrideBlock: true, Row(ClientA, Day), Row(ClientB, Day));

        result.AcceptedIndexes.ShouldBe(new[] { 0, 1 });
        result.BlockedIndexes.ShouldBeEmpty();
        result.OverrideApplied.ShouldBeTrue();
        result.ReportableConflicts.Count.ShouldBe(2);
        result.ReportableConflicts.ShouldContain(c => c.Comment == RestViolationKey && c.Type == ScheduleValidationType.Error);
        result.ReportableConflicts.ShouldContain(c => c.Comment == WeeklyOvertimeKey && c.Type == ScheduleValidationType.Warning);
        AssertOverrideAuditLogged();
    }

    [Test]
    public async Task UnauthorizedOverridableBlock_IsBlocked_NotOverridden()
    {
        _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([ErrorEntry(ClientA, RestViolationKey, overridable: true)]));
        _authorizer.IsAuthorizedAsync(Arg.Any<bool>()).Returns(false);

        var result = await Partition(overrideBlock: true, Row(ClientA, Day));

        result.AcceptedIndexes.ShouldBeEmpty();
        result.BlockedIndexes.ShouldHaveSingleItem().ReasonKey.ShouldBe(RestViolationKey);
        result.OverrideApplied.ShouldBeFalse();
    }

    [Test]
    public async Task MixedHardAndOverridable_GreedyStillOverridesTheOverridableClient()
    {
        // ClientA collides (structural, never overridable); ClientB blocks only via a Block-mode
        // escalation. With an authorized override the batch path is unavailable (hard blocking is
        // present), but the greedy per-row path must still override ClientB's row and block only A.
        _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var rows = ci.Arg<IReadOnlyList<PlannedWorkRow>>();
                var conflicts = new List<ScheduleValidationNotificationDto>();
                if (rows.Any(r => r.ClientId == ClientA))
                {
                    conflicts.Add(ErrorEntry(ClientA, CollisionKey));
                }
                if (rows.Any(r => r.ClientId == ClientB))
                {
                    conflicts.Add(ErrorEntry(ClientB, RestViolationKey, overridable: true));
                }
                return new PreCommitCheckResult(conflicts);
            });
        _authorizer.IsAuthorizedAsync(true).Returns(true);

        var result = await Partition(overrideBlock: true, Row(ClientA, Day), Row(ClientB, Day));

        result.AcceptedIndexes.ShouldBe(new[] { 1 });
        result.BlockedIndexes.ShouldHaveSingleItem().ReasonKey.ShouldBe(CollisionKey);
        result.OverrideApplied.ShouldBeTrue();
        result.ReportableConflicts.ShouldContain(c => c.ClientId == ClientB && c.Comment == RestViolationKey);
        AssertOverrideAuditLogged();
    }
}
