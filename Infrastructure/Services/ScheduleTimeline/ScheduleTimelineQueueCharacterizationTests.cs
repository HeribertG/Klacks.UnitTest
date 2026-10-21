// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for the queue loop of ScheduleTimelineBackgroundService, written against the unsplit
/// service before its split: drain/dedupe by semantic key, the bounded DropOldest channel, the single transient
/// retry (including how broadly "transient" is classified today), the non-transient failure that only skips its
/// own job, and a clean shutdown.
/// </summary>

using System.Net.Sockets;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Npgsql;
using NUnit.Framework;
using Shouldly;
using static Klacks.UnitTest.Infrastructure.Services.ScheduleTimeline.ScheduleTimelineTestHost;

namespace Klacks.UnitTest.Infrastructure.Services.ScheduleTimeline;

[TestFixture]
public class ScheduleTimelineQueueCharacterizationTests
{
    private const int ChannelCapacity = 500;

    private static readonly DateOnly Monday = new(2026, 3, 2);
    private static readonly DateOnly Tuesday = Monday.AddDays(1);

    private ScheduleTimelineTestHost _host = null!;
    private Guid _employeeId;
    private Guid _colleagueId;

    [SetUp]
    public async Task SetUp()
    {
        _host = new ScheduleTimelineTestHost();
        _employeeId = await _host.AddEmployeeAsync("Muster", "Anna");
        _colleagueId = await _host.AddEmployeeAsync("Beispiel", "Ben");
        var shift = await _host.AddCustomerShiftAsync("Bahnhofstrasse 1", "8001", "Zürich");
        await _host.AddWorkAsync(_employeeId, shift, Tuesday, new TimeOnly(8, 0), new TimeOnly(16, 0));
        await _host.AddWorkAsync(_colleagueId, shift, Tuesday, new TimeOnly(8, 0), new TimeOnly(16, 0));
    }

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    [Test]
    public async Task QueuedTogether_IdenticalKeysCollapseWhileDifferentKeysAllRunInFirstQueuedOrder()
    {
        var token = Guid.NewGuid();
        _host.Service.QueueCheck(_employeeId, Tuesday, null);
        _host.Service.QueueRangeCheck(Monday, Tuesday, null);
        _host.Service.QueueCheck(_employeeId, Tuesday, null);
        _host.Service.QueueCheck(_employeeId, Tuesday, token);

        await _host.StartAsync();
        var validations = await _host.WaitForValidationsAsync(3);

        validations.Select(v => (v.IsFullRefresh, v.AnalyseToken)).ShouldBe(
        [
            (false, (Guid?)null),
            (true, (Guid?)null),
            (false, (Guid?)token),
        ]);
        await _host.PeriodCap.Received(1).EvaluateAsync(_employeeId, Arg.Any<string>(), Tuesday, null, Arg.Any<CancellationToken>());
        await _host.PeriodCap.Received(1).EvaluateAsync(_employeeId, Arg.Any<string>(), Tuesday, token, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MoreRequestsThanTheChannelHolds_DropTheOldestAndKeepTheNewest()
    {
        for (var i = 0; i <= ChannelCapacity; i++)
        {
            _host.Service.QueueRangeCheck(Monday.AddDays(i), Monday.AddDays(i), Guid.NewGuid());
        }

        await _host.StartAsync();
        await _host.WaitForValidationsAsync(ChannelCapacity);

        await _host.PlanningRules.DidNotReceive().EvaluateRangeAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Monday, Arg.Any<DateOnly>(), Arg.Any<Guid?>(),
            Arg.Any<IReadOnlyDictionary<Guid, string>>(), Arg.Any<CancellationToken>());
        await _host.PlanningRules.Received(1).EvaluateRangeAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Monday.AddDays(ChannelCapacity), Arg.Any<DateOnly>(), Arg.Any<Guid?>(),
            Arg.Any<IReadOnlyDictionary<Guid, string>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TransientFailureOnce_IsRetriedInAFreshAttemptAndReportsOnce()
    {
        _host.PeriodCap.EvaluateAsync(_employeeId, Arg.Any<string>(), Tuesday, null, Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new TimeoutException("db timeout"),
                _ => Marker(PeriodCapMarker, _employeeId));

        _host.Service.QueueCheck(_employeeId, Tuesday, null);
        await _host.StartAsync();
        var validations = await _host.WaitForValidationsAsync(1);

        validations.ShouldHaveSingleItem().Entries.Select(e => e.Comment).ShouldContain(PeriodCapMarker);
        await _host.PeriodCap.Received(2).EvaluateAsync(_employeeId, Arg.Any<string>(), Tuesday, null, Arg.Any<CancellationToken>());
        _host.Collisions.Count.ShouldBe(1);
    }

    [Test]
    public async Task TransientFailureTwice_GivesUpAfterOneRetryAndTheNextJobStillRuns()
    {
        _host.PeriodCap.EvaluateAsync(_employeeId, Arg.Any<string>(), Tuesday, null, Arg.Any<CancellationToken>())
            .Throws(new TimeoutException("db timeout"));

        _host.Service.QueueCheck(_employeeId, Tuesday, null);
        _host.Service.QueueCheck(_colleagueId, Tuesday, null);
        await _host.StartAsync();
        var validations = await _host.WaitForValidationsAsync(1);

        validations.ShouldHaveSingleItem().CheckedClientId.ShouldBe(_colleagueId);
        await _host.PeriodCap.Received(2).EvaluateAsync(_employeeId, Arg.Any<string>(), Tuesday, null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NonTransientFailure_IsNotRetriedAndOnlySkipsItsOwnJob()
    {
        _host.PeriodCap.EvaluateAsync(_employeeId, Arg.Any<string>(), Tuesday, null, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("broken rule"));

        _host.Service.QueueCheck(_employeeId, Tuesday, null);
        _host.Service.QueueCheck(_colleagueId, Tuesday, null);
        await _host.StartAsync();
        var validations = await _host.WaitForValidationsAsync(1);

        validations.ShouldHaveSingleItem().CheckedClientId.ShouldBe(_colleagueId);
        await _host.PeriodCap.Received(1).EvaluateAsync(_employeeId, Arg.Any<string>(), Tuesday, null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TransientCauseWrappedInAnInnerException_IsRetried()
    {
        _host.PeriodCap.EvaluateAsync(_employeeId, Arg.Any<string>(), Tuesday, null, Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new InvalidOperationException("wrapper", new SocketException()),
                _ => Marker(PeriodCapMarker, _employeeId));

        _host.Service.QueueCheck(_employeeId, Tuesday, null);
        await _host.StartAsync();
        await _host.WaitForValidationsAsync(1);

        await _host.PeriodCap.Received(2).EvaluateAsync(_employeeId, Arg.Any<string>(), Tuesday, null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PostgresErrorOutsideTheTransientSqlStates_IsStillRetriedBecauseEveryNpgsqlExceptionCounts()
    {
        _host.PeriodCap.EvaluateAsync(_employeeId, Arg.Any<string>(), Tuesday, null, Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new PostgresException("duplicate key", "ERROR", "ERROR", "23505"),
                _ => Marker(PeriodCapMarker, _employeeId));

        _host.Service.QueueCheck(_employeeId, Tuesday, null);
        await _host.StartAsync();
        await _host.WaitForValidationsAsync(1);

        await _host.PeriodCap.Received(2).EvaluateAsync(_employeeId, Arg.Any<string>(), Tuesday, null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Stop_WhileWaitingForTheNextRequest_EndsTheLoopWithoutFaultingTheExecuteTask()
    {
        _host.Service.QueueCheck(_employeeId, Tuesday, null);
        await _host.StartAsync();
        await _host.WaitForValidationsAsync(1);

        await _host.Service.StopAsync(CancellationToken.None);

        _host.Service.ExecuteTask.ShouldNotBeNull();
        _host.Service.ExecuteTask!.Status.ShouldBe(TaskStatus.RanToCompletion);
    }
}
