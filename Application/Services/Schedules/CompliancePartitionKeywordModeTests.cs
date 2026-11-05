// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Keyword mode of the compliance partition: a day-directive conflict (FREE / EARLY / -NIGHT ...) blocks a planned row in
/// Hard mode, but only warns in Soft mode, where the wizard broke the wish on purpose. Other errors keep blocking.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.ScheduleOptimizer.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class CompliancePartitionKeywordModeTests
{
    private static readonly Guid Client = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 4, 6);

    private IPreCommitConflictChecker _checker = null!;
    private CompliancePartitionService _service = null!;

    [SetUp]
    public void Setup()
    {
        _checker = Substitute.For<IPreCommitConflictChecker>();
        var authorizer = Substitute.For<ISupervisorOverrideAuthorizer>();
        authorizer.IsAuthorizedAsync(Arg.Any<bool>()).Returns(false);
        _service = new CompliancePartitionService(
            _checker, authorizer, Substitute.For<IHttpContextAccessor>(), Substitute.For<ILogger<CompliancePartitionService>>());
    }

    [Test]
    public async Task HardMode_DayDirectiveConflict_BlocksTheRow()
    {
        ArrangeConflicts(Entry(ScheduleValidationKeys.DayDirective));

        var result = await _service.PartitionAsync([Row()], null, false, CancellationToken.None, KeywordMode.Hard);

        result.AcceptedIndexes.ShouldBeEmpty();
        result.BlockedIndexes.Single().ReasonKey.ShouldBe(ScheduleValidationKeys.DayDirective);
    }

    [Test]
    public async Task SoftMode_DayDirectiveConflict_IsWrittenAndReportedAsWarning()
    {
        ArrangeConflicts(Entry(ScheduleValidationKeys.DayDirective));

        var result = await _service.PartitionAsync([Row()], null, false, CancellationToken.None, KeywordMode.Soft);

        result.AcceptedIndexes.ShouldBe([0]);
        result.BlockedIndexes.ShouldBeEmpty();
        var reported = result.ReportableConflicts.Single();
        reported.Comment.ShouldBe(ScheduleValidationKeys.DayDirective);
        reported.Type.ShouldBe(ScheduleValidationType.Warning);
    }

    [Test]
    public async Task SoftMode_CollisionNextToADayDirective_StillBlocks()
    {
        ArrangeConflicts(Entry(ScheduleValidationKeys.DayDirective), Entry(ScheduleValidationKeys.Collision));

        var result = await _service.PartitionAsync([Row()], null, false, CancellationToken.None, KeywordMode.Soft);

        result.AcceptedIndexes.ShouldBeEmpty();
        result.BlockedIndexes.Single().ReasonKey.ShouldBe(ScheduleValidationKeys.Collision);
    }

    private void ArrangeConflicts(params ScheduleValidationNotificationDto[] entries)
        => _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult(entries));

    private static PlannedWorkRow Row() => new(Client, Day, new TimeOnly(8, 0), new TimeOnly(16, 0));

    private static ScheduleValidationNotificationDto Entry(string comment)
        => new() { Type = ScheduleValidationType.Error, ClientId = Client, Date = Day, Comment = comment };
}
