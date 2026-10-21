// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for the range check of ScheduleTimelineBackgroundService, written against the unsplit
/// service before its split: per-client entry order, the deliberately different evaluator anchors, the policy
/// fallback, the post-loop entries, the full-refresh flags and the travel-time lookup failing the whole job.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Scheduling;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Shouldly;
using static Klacks.UnitTest.Infrastructure.Services.ScheduleTimeline.ScheduleTimelineTestHost;

namespace Klacks.UnitTest.Infrastructure.Services.ScheduleTimeline;

[TestFixture]
public class ScheduleTimelineRangeCheckCharacterizationTests
{
    private const string EmployeeName = "Muster Anna";
    private const string ColleagueName = "Beispiel Ben";
    private const int BoundaryDays = 14;

    private static readonly DateOnly Monday = new(2026, 3, 2);
    private static readonly DateOnly Sunday = Monday.AddDays(6);

    private ScheduleTimelineTestHost _host = null!;
    private Guid _employeeId;
    private Guid _colleagueId;
    private Guid _shiftZurich;
    private Guid _shiftBern;

    [SetUp]
    public async Task SetUp()
    {
        _host = new ScheduleTimelineTestHost();
        _employeeId = await _host.AddEmployeeAsync("Muster", "Anna");
        _colleagueId = await _host.AddEmployeeAsync("Beispiel", "Ben");
        _shiftZurich = await _host.AddCustomerShiftAsync("Bahnhofstrasse 1", "8001", "Zürich");
        _shiftBern = await _host.AddCustomerShiftAsync("Bundesplatz 3", "3003", "Bern");
    }

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    [Test]
    public async Task RangeCheck_ReportsPerClientEntriesThenPlanningRulesLastAndFlagsAFullRefresh()
    {
        await _host.AddWorkAsync(_employeeId, _shiftZurich, Monday, new TimeOnly(8, 0), new TimeOnly(12, 0));
        await _host.AddWorkAsync(_employeeId, _shiftBern, Monday, new TimeOnly(12, 20), new TimeOnly(16, 0));
        await _host.AddWorkAsync(_colleagueId, _shiftZurich, Monday.AddDays(2), new TimeOnly(8, 0), new TimeOnly(16, 0));
        _host.Travel.IsApiKeyConfiguredAsync().Returns(true);
        _host.Travel.CalculateTravelTimeAsync(Arg.Any<Address>(), Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(TimeSpan.FromMinutes(30));

        _host.Service.QueueRangeCheck(Monday, Sunday, null);
        await _host.StartAsync();
        var validations = await _host.WaitForValidationsAsync(1);

        var notification = validations.ShouldHaveSingleItem();
        notification.IsFullRefresh.ShouldBeTrue();
        notification.CheckedClientId.ShouldBeNull();
        notification.CheckedDate.ShouldBeNull();
        notification.Entries.Where(e => e.ClientId == _employeeId).Select(e => e.Comment).ShouldBe(
        [
            PeriodCapMarker,
            RestDayRotationMarker,
            CounterRuleMarker,
            RestrictedWindowMarker,
            CompensatoryRestMarker,
            ScheduleValidationKeys.TravelTimeError,
            HolidayWorkMarker,
        ]);
        notification.Entries.Where(e => e.ClientId == _colleagueId).Select(e => e.Comment).ShouldBe(
        [
            PeriodCapMarker,
            RestDayRotationMarker,
            CounterRuleMarker,
            RestrictedWindowMarker,
            CompensatoryRestMarker,
            HolidayWorkMarker,
        ]);
        notification.Entries.Last().Comment.ShouldBe(PlanningRangeMarker);
        notification.Entries.Count.ShouldBe(14);

        var collisions = _host.Collisions.ShouldHaveSingleItem();
        collisions.IsFullRefresh.ShouldBeTrue();
        collisions.CheckedClientId.ShouldBeNull();
    }

    [Test]
    public async Task RangeCheck_AnchorsPeriodCapAtTheStartAndTheTrailingEvaluatorsAtTheEnd()
    {
        await _host.AddWorkAsync(_employeeId, _shiftZurich, Monday.AddDays(1), new TimeOnly(8, 0), new TimeOnly(16, 0));

        _host.Service.QueueRangeCheck(Monday, Sunday, null);
        await _host.StartAsync();
        await _host.WaitForValidationsAsync(1);

        await _host.PeriodCap.Received(1).EvaluateAsync(_employeeId, EmployeeName, Monday, null, Arg.Any<CancellationToken>());
        await _host.RestDayRotation.Received(1).EvaluateAsync(_employeeId, EmployeeName, Sunday, null, Arg.Any<CancellationToken>());
        await _host.CounterRule.Received(1).EvaluateAsync(_employeeId, EmployeeName, Sunday, null, Arg.Any<CancellationToken>());
        await _host.RestrictedWindow.Received(1).EvaluateRangeAsync(_employeeId, EmployeeName, Monday, Sunday, null, Arg.Any<CancellationToken>());
        await _host.RestrictedWindow.DidNotReceiveWithAnyArgs().EvaluateAsync(default, default!, default);
        await _host.CompensatoryReconciler.Received(1).ReconcileAsync(
            _employeeId, Monday.AddDays(-BoundaryDays), Sunday, null, Arg.Any<CancellationToken>());
        await _host.CompensatoryEvaluator.Received(1).EvaluateAsync(_employeeId, EmployeeName, Sunday, null, Arg.Any<CancellationToken>());
        await _host.PlanningRules.Received(1).EvaluateRangeAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { _employeeId })),
            Monday, Sunday, null,
            Arg.Is<IReadOnlyDictionary<Guid, string>>(names => names[_employeeId] == EmployeeName),
            Arg.Any<CancellationToken>());
        await _host.PlanningRules.DidNotReceiveWithAnyArgs().EvaluateDayAsync(default, default!, default, default);
        _host.Store.Received(1).SetTimeline(_employeeId, Arg.Any<ClientTimeline>());
    }

    [Test]
    public async Task RangeCheck_ClientMissingFromTheBatchPolicies_FallsBackToTheSingleClientLookupAtTheStart()
    {
        await _host.AddWorkAsync(_employeeId, _shiftZurich, Monday, new TimeOnly(8, 0), new TimeOnly(16, 0));
        _host.Policies.GetForClientsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>())
            .Returns((IReadOnlyDictionary<Guid, SchedulingPolicy>)new Dictionary<Guid, SchedulingPolicy>());

        _host.Service.QueueRangeCheck(Monday, Sunday, null);
        await _host.StartAsync();
        await _host.WaitForValidationsAsync(1);

        await _host.Policies.Received(1).GetForClientsAsync(
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.SequenceEqual(new[] { _employeeId })), Monday);
        await _host.Policies.Received(1).GetForClientAsync(_employeeId, Monday);
    }

    [Test]
    public async Task RangeCheck_NotifiesCollisionsThenEscalatesThenNotifiesValidations()
    {
        await _host.AddWorkAsync(_employeeId, _shiftZurich, Monday, new TimeOnly(8, 0), new TimeOnly(16, 0));

        _host.Service.QueueRangeCheck(Monday, Sunday, null);
        await _host.StartAsync();
        await _host.WaitForValidationsAsync(1);

        Received.InOrder(() =>
        {
            _host.Store.SetTimeline(_employeeId, Arg.Any<ClientTimeline>());
            _host.CompensatoryReconciler.ReconcileAsync(_employeeId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), null, Arg.Any<CancellationToken>());
            _host.CompensatoryEvaluator.EvaluateAsync(_employeeId, EmployeeName, Sunday, null, Arg.Any<CancellationToken>());
            _host.PlanningRules.EvaluateRangeAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Monday, Sunday, null,
                Arg.Any<IReadOnlyDictionary<Guid, string>>(), Arg.Any<CancellationToken>());
            _host.Notifications.NotifyCollisionsDetected(Arg.Any<CollisionListNotificationDto>());
            _host.Escalation.EscalateBlockedViolationsAsync(Arg.Any<List<ScheduleValidationNotificationDto>>());
            _host.Notifications.NotifyScheduleValidationsDetected(Arg.Any<ScheduleValidationListNotificationDto>());
        });
    }

    [Test]
    public async Task RangeCheck_RoutingKeyLookupFailure_FailsTheWholeRangeWhileTheSingleCheckSwallowsIt()
    {
        await _host.AddWorkAsync(_employeeId, _shiftZurich, Monday, new TimeOnly(8, 0), new TimeOnly(16, 0));
        _host.Travel.IsApiKeyConfiguredAsync().ThrowsAsync(new InvalidOperationException("routing settings unreadable"));

        _host.Service.QueueRangeCheck(Monday, Sunday, null);
        _host.Service.QueueCheck(_employeeId, Monday, null);
        await _host.StartAsync();
        var validations = await _host.WaitForValidationsAsync(1);

        var only = validations.ShouldHaveSingleItem();
        only.IsFullRefresh.ShouldBeFalse();
        only.CheckedClientId.ShouldBe(_employeeId);
        _host.Collisions.ShouldHaveSingleItem().IsFullRefresh.ShouldBeFalse();
    }
}
