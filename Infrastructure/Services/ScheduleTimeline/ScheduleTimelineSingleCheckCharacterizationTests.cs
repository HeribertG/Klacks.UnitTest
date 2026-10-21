// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for the single (client/day) check of ScheduleTimelineBackgroundService, written against
/// the unsplit service before its split: entry order, evaluator anchors, notification order and flags, the
/// travel-time pairing and the swallowed travel-time failure.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Schedules;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Shouldly;
using static Klacks.UnitTest.Infrastructure.Services.ScheduleTimeline.ScheduleTimelineTestHost;

namespace Klacks.UnitTest.Infrastructure.Services.ScheduleTimeline;

[TestFixture]
public class ScheduleTimelineSingleCheckCharacterizationTests
{
    private const string EmployeeName = "Muster Anna";
    private const int BoundaryDays = 14;

    private static readonly DateOnly Tuesday = new(2026, 3, 3);

    private ScheduleTimelineTestHost _host = null!;
    private Guid _employeeId;
    private Guid _shiftZurich;
    private Guid _shiftBern;

    [SetUp]
    public async Task SetUp()
    {
        _host = new ScheduleTimelineTestHost();
        _employeeId = await _host.AddEmployeeAsync("Muster", "Anna");
        _shiftZurich = await _host.AddCustomerShiftAsync("Bahnhofstrasse 1", "8001", "Zürich");
        _shiftBern = await _host.AddCustomerShiftAsync("Bundesplatz 3", "3003", "Bern");
    }

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    [Test]
    public async Task SingleCheck_ReportsEntriesInTheFixedEvaluatorOrderAndFlagsAPartialRefresh()
    {
        var first = await _host.AddWorkAsync(_employeeId, _shiftZurich, Tuesday, new TimeOnly(8, 0), new TimeOnly(12, 0));
        await _host.AddWorkAsync(_employeeId, _shiftBern, Tuesday, new TimeOnly(12, 20), new TimeOnly(16, 0));
        _host.Travel.IsApiKeyConfiguredAsync().Returns(true);
        _host.Travel.CalculateTravelTimeAsync(Arg.Any<Address>(), Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(TimeSpan.FromMinutes(30));
        _host.Eligibility.BuildAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<IReadOnlyCollection<EligibilitySlot>>(),
                Arg.Any<IReadOnlySet<(string AgentId, Guid ShiftId, DateOnly Date)>?>(),
                Arg.Any<CancellationToken>())
            .Returns(MatrixWithMissingGap(first));

        _host.Service.QueueCheck(_employeeId, Tuesday, null);
        await _host.StartAsync();
        var validations = await _host.WaitForValidationsAsync(1);

        var notification = validations.ShouldHaveSingleItem();
        notification.IsFullRefresh.ShouldBeFalse();
        notification.CheckedClientId.ShouldBe(_employeeId);
        notification.CheckedDate.ShouldBe(Tuesday);
        notification.AnalyseToken.ShouldBeNull();
        notification.Entries.Select(e => e.Comment).ShouldBe(
        [
            QualificationValidationKeys.Missing,
            PeriodCapMarker,
            RestDayRotationMarker,
            CounterRuleMarker,
            RestrictedWindowMarker,
            CompensatoryRestMarker,
            HolidayWorkMarker,
            PlanningDayMarker,
            ScheduleValidationKeys.TravelTimeError,
        ]);

        var travel = notification.Entries.Last();
        travel.Type.ShouldBe(ScheduleValidationType.Error);
        travel.ClientName.ShouldBe(EmployeeName);
        travel.Date.ShouldBe(Tuesday);
        travel.CommentParams.ShouldNotBeNull();
        travel.CommentParams!["gapMinutes"].ShouldBe("20");
        travel.CommentParams["travelMinutes"].ShouldBe("30");

        var collisions = _host.Collisions.ShouldHaveSingleItem();
        collisions.IsFullRefresh.ShouldBeFalse();
        collisions.CheckedClientId.ShouldBe(_employeeId);
        collisions.CheckedDate.ShouldBe(Tuesday);
    }

    [Test]
    public async Task SingleCheck_UsesTheEditedDayAsAnchorAndReconcilesTheBoundaryWindowAroundIt()
    {
        await _host.AddWorkAsync(_employeeId, _shiftZurich, Tuesday, new TimeOnly(8, 0), new TimeOnly(16, 0));

        _host.Service.QueueCheck(_employeeId, Tuesday, null);
        await _host.StartAsync();
        await _host.WaitForValidationsAsync(1);

        await _host.Policies.Received(1).GetForClientAsync(_employeeId, Tuesday);
        await _host.PeriodCap.Received(1).EvaluateAsync(_employeeId, EmployeeName, Tuesday, null, Arg.Any<CancellationToken>());
        await _host.RestDayRotation.Received(1).EvaluateAsync(_employeeId, EmployeeName, Tuesday, null, Arg.Any<CancellationToken>());
        await _host.CounterRule.Received(1).EvaluateAsync(_employeeId, EmployeeName, Tuesday, null, Arg.Any<CancellationToken>());
        await _host.RestrictedWindow.Received(1).EvaluateAsync(_employeeId, EmployeeName, Tuesday, null, Arg.Any<CancellationToken>());
        await _host.CompensatoryReconciler.Received(1).ReconcileAsync(
            _employeeId, Tuesday.AddDays(-BoundaryDays), Tuesday.AddDays(BoundaryDays), null, Arg.Any<CancellationToken>());
        await _host.CompensatoryEvaluator.Received(1).EvaluateAsync(_employeeId, EmployeeName, Tuesday, null, Arg.Any<CancellationToken>());
        await _host.HolidayWork.Received(1).EvaluateAsync(
            _employeeId, EmployeeName, Arg.Is<IReadOnlyCollection<DateOnly>>(d => d.SequenceEqual(new[] { Tuesday })), Arg.Any<CancellationToken>());
        await _host.PlanningRules.Received(1).EvaluateDayAsync(_employeeId, EmployeeName, Tuesday, null, Arg.Any<CancellationToken>());
        await _host.RestrictedWindow.DidNotReceiveWithAnyArgs().EvaluateRangeAsync(default, default!, default, default);
        _host.Store.Received(1).SetTimeline(_employeeId, Arg.Is<ClientTimeline>(t => t.Blocks.Count == 1));
    }

    [Test]
    public async Task SingleCheck_StoresTheTimelineFirstReconcilesBeforeEvaluatingAndEscalatesBetweenTheTwoNotifications()
    {
        await _host.AddWorkAsync(_employeeId, _shiftZurich, Tuesday, new TimeOnly(8, 0), new TimeOnly(16, 0));

        _host.Service.QueueCheck(_employeeId, Tuesday, null);
        await _host.StartAsync();
        await _host.WaitForValidationsAsync(1);

        Received.InOrder(() =>
        {
            _host.Store.SetTimeline(_employeeId, Arg.Any<ClientTimeline>());
            _host.Policies.GetForClientAsync(_employeeId, Tuesday);
            _host.PeriodCap.EvaluateAsync(_employeeId, EmployeeName, Tuesday, null, Arg.Any<CancellationToken>());
            _host.CompensatoryReconciler.ReconcileAsync(_employeeId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), null, Arg.Any<CancellationToken>());
            _host.CompensatoryEvaluator.EvaluateAsync(_employeeId, EmployeeName, Tuesday, null, Arg.Any<CancellationToken>());
            _host.PlanningRules.EvaluateDayAsync(_employeeId, EmployeeName, Tuesday, null, Arg.Any<CancellationToken>());
            _host.Notifications.NotifyCollisionsDetected(Arg.Any<CollisionListNotificationDto>());
            _host.Escalation.EscalateBlockedViolationsAsync(Arg.Any<List<ScheduleValidationNotificationDto>>());
            _host.Notifications.NotifyScheduleValidationsDetected(Arg.Any<ScheduleValidationListNotificationDto>());
        });
    }

    [Test]
    public async Task SingleCheck_DayWithoutWork_SkipsTheHolidayEvaluator()
    {
        await _host.AddWorkAsync(_employeeId, _shiftZurich, Tuesday.AddDays(1), new TimeOnly(8, 0), new TimeOnly(16, 0));

        _host.Service.QueueCheck(_employeeId, Tuesday, null);
        await _host.StartAsync();
        var validations = await _host.WaitForValidationsAsync(1);

        await _host.HolidayWork.DidNotReceiveWithAnyArgs().EvaluateAsync(default, default!, default!);
        validations.ShouldHaveSingleItem().Entries.Select(e => e.Comment).ShouldNotContain(HolidayWorkMarker);
    }

    [Test]
    public async Task SingleCheck_WithoutRoutingKey_AddsOneInfoEntryAndNeverAsksForATravelTime()
    {
        await _host.AddWorkAsync(_employeeId, _shiftZurich, Tuesday, new TimeOnly(6, 0), new TimeOnly(8, 0));
        await _host.AddWorkAsync(_employeeId, _shiftBern, Tuesday, new TimeOnly(9, 0), new TimeOnly(11, 0));
        await _host.AddWorkAsync(_employeeId, _shiftZurich, Tuesday, new TimeOnly(12, 0), new TimeOnly(14, 0));
        _host.Travel.IsApiKeyConfiguredAsync().Returns(false);

        _host.Service.QueueCheck(_employeeId, Tuesday, null);
        await _host.StartAsync();
        var validations = await _host.WaitForValidationsAsync(1);

        var info = validations.ShouldHaveSingleItem().Entries
            .Where(e => e.Comment == ScheduleValidationKeys.TravelTimeNoApiKey)
            .ShouldHaveSingleItem();
        info.Type.ShouldBe(ScheduleValidationType.Info);
        info.Date.ShouldBe(Tuesday);
        await _host.Travel.DidNotReceiveWithAnyArgs().CalculateTravelTimeAsync(default!, default!, default);
    }

    [Test]
    public async Task SingleCheck_GapBelowOneAndAHalfTravelTimes_IsAWarningAndAWideGapIsSilent()
    {
        await _host.AddWorkAsync(_employeeId, _shiftZurich, Tuesday, new TimeOnly(6, 0), new TimeOnly(8, 0));
        await _host.AddWorkAsync(_employeeId, _shiftBern, Tuesday, new TimeOnly(8, 40), new TimeOnly(10, 0));
        await _host.AddWorkAsync(_employeeId, _shiftZurich, Tuesday, new TimeOnly(11, 0), new TimeOnly(13, 0));
        _host.Travel.IsApiKeyConfiguredAsync().Returns(true);
        _host.Travel.CalculateTravelTimeAsync(Arg.Any<Address>(), Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(TimeSpan.FromMinutes(30));

        _host.Service.QueueCheck(_employeeId, Tuesday, null);
        await _host.StartAsync();
        var validations = await _host.WaitForValidationsAsync(1);

        var travelEntries = validations.ShouldHaveSingleItem().Entries
            .Where(e => e.Comment is ScheduleValidationKeys.TravelTimeError or ScheduleValidationKeys.TravelTimeWarning)
            .ToList();
        var warning = travelEntries.ShouldHaveSingleItem();
        warning.Comment.ShouldBe(ScheduleValidationKeys.TravelTimeWarning);
        warning.Type.ShouldBe(ScheduleValidationType.Warning);
        warning.CommentParams!["gapMinutes"].ShouldBe("40");
        warning.CommentParams["travelMinutes"].ShouldBe("30");
        await _host.Travel.ReceivedWithAnyArgs(2).CalculateTravelTimeAsync(default!, default!, default);
    }

    [Test]
    public async Task SingleCheck_TravelTimeFailure_IsSwallowedAndTheCheckStillReports()
    {
        await _host.AddWorkAsync(_employeeId, _shiftZurich, Tuesday, new TimeOnly(8, 0), new TimeOnly(12, 0));
        await _host.AddWorkAsync(_employeeId, _shiftBern, Tuesday, new TimeOnly(12, 20), new TimeOnly(16, 0));
        _host.Travel.IsApiKeyConfiguredAsync().ThrowsAsync(new InvalidOperationException("routing settings unreadable"));

        _host.Service.QueueCheck(_employeeId, Tuesday, null);
        await _host.StartAsync();
        var validations = await _host.WaitForValidationsAsync(1);

        validations.ShouldHaveSingleItem().Entries.Select(e => e.Comment).ShouldContain(PlanningDayMarker);
        _host.Collisions.Count.ShouldBe(1);
    }

    private EligibilityMatrix MatrixWithMissingGap(Work work)
        => new()
        {
            Ineligible = new HashSet<(string, Guid, DateOnly)>(),
            Gaps = new Dictionary<(string, Guid, DateOnly), IReadOnlyList<QualificationGap>>
            {
                [(work.ClientId.ToString(), work.ShiftId, work.CurrentDate)] =
                [
                    new QualificationGap(Guid.NewGuid(), QualificationGapReason.Missing, QualificationLevel.Basic),
                ],
            },
            QualificationInfo = new Dictionary<Guid, QualificationInfo>(),
            ShiftNames = new Dictionary<Guid, string>(),
        };
}
