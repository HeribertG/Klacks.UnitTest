// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for PeriodAutoCloseService - the autonomous period close. The property under test is that a
/// period is sealed ONLY when every gate holds (autonomy allowed, lag stored, close date reached, inside the
/// window, not sealed, has work, no errors, autonomy still allowed right before the seal) and that every
/// non-close of an armed group is reported with its cause. The evaluation reads and the fresh-scope work
/// (autonomy re-check, sealed re-check, period issues, seal, read-back) run on separate substitutes, so a
/// test can prove that the re-checks and the seal happen in the fresh scope.
/// Default scenario: company day 2026-09-02, monthly group, stored lag 1 - the August period (01.08.-31.08.)
/// has its close date on 01.09. and is due.
/// </summary>

using Klacks.Api.Application.Commands.PeriodClosing;
using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Queries.PeriodClosing;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;
using InvalidRequestException = Klacks.Api.Domain.Exceptions.InvalidRequestException;
using SettingsRow = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class PeriodAutoCloseServiceTests
{
    private static readonly Guid DecidingAdminId = Guid.Parse("3f1c9a52-0000-0000-0000-000000000001");
    private static readonly DateOnly DefaultToday = new(2026, 9, 2);
    private static readonly DateOnly AugustStart = new(2026, 8, 1);
    private static readonly DateOnly AugustEnd = new(2026, 8, 31);
    private static readonly DateOnly JulyEnd = new(2026, 7, 31);

    private IGroupRepository _groupRepository = null!;
    private IWeekConfiguration _weekConfiguration = null!;
    private ISettingsReader _settingsReader = null!;
    private IPeriodAutoCloseResolver _resolver = null!;
    private ISealedDayRepository _sealedDays = null!;
    private IScheduleActivityProbe _activityProbe = null!;
    private IPeriodAutoCloseResolver _freshResolver = null!;
    private ISealedDayRepository _freshSealedDays = null!;
    private IMediator _freshMediator = null!;
    private IServiceScopeFactory _scopeFactory = null!;
    private HashSet<(Guid? GroupId, DateOnly Date)> _sealed = null!;
    private List<PeriodAuditLog> _auditEntries = null!;
    private IPeriodAuditLogRepository _auditLog = null!;
    private IPeriodAuditLogRepository _freshAuditLog = null!;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _weekConfiguration = Substitute.For<IWeekConfiguration>();
        _weekConfiguration.GetWeekStartAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var date = call.Arg<DateOnly>();
                return date.AddDays(-(((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7));
            });
        _settingsReader = Substitute.For<ISettingsReader>();
        StubLag("1");
        _sealed = new HashSet<(Guid?, DateOnly)>();
        _auditEntries = new List<PeriodAuditLog>();
        _auditLog = AuditLogBackedByTheFakeStore();
        _freshAuditLog = AuditLogBackedByTheFakeStore();

        _resolver = Substitute.For<IPeriodAutoCloseResolver>();
        _freshResolver = Substitute.For<IPeriodAutoCloseResolver>();
        StubAutonomy(_resolver, allowed: true);
        StubAutonomy(_freshResolver, allowed: true);

        _sealedDays = SealedDaysBackedByTheFakeStore();
        _freshSealedDays = SealedDaysBackedByTheFakeStore();

        _activityProbe = Substitute.For<IScheduleActivityProbe>();
        _activityProbe.HasDirectWorkInRangeAsync(Arg.Any<Group>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _freshMediator = Substitute.For<IMediator>();
        StubIssues();
        _freshMediator.Send(Arg.Any<ClosePeriodByGroupCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                SealAsKlacksy(call.Arg<ClosePeriodByGroupCommand>());
                return 31;
            });

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IPeriodAutoCloseResolver)).Returns(_freshResolver);
        provider.GetService(typeof(ISealedDayRepository)).Returns(_freshSealedDays);
        provider.GetService(typeof(IMediator)).Returns(_freshMediator);
        provider.GetService(typeof(IPeriodAuditLogRepository)).Returns(_freshAuditLog);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        _scopeFactory.CreateScope().Returns(scope);
    }

    private ISealedDayRepository SealedDaysBackedByTheFakeStore()
    {
        var repository = Substitute.For<ISealedDayRepository>();
        repository.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var from = call.ArgAt<DateOnly>(0);
                var to = call.ArgAt<DateOnly>(1);
                var groupId = call.ArgAt<Guid?>(2);
                return _sealed
                    .Where(entry => (groupId == null || entry.GroupId == null || entry.GroupId == groupId)
                        && entry.Date >= from && entry.Date <= to)
                    .Select(entry => new SealedDay { Date = entry.Date, GroupId = entry.GroupId })
                    .ToList();
            });
        return repository;
    }

    private IPeriodAuditLogRepository AuditLogBackedByTheFakeStore()
    {
        var repository = Substitute.For<IPeriodAuditLogRepository>();
        repository.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var from = call.ArgAt<DateOnly>(0);
                var to = call.ArgAt<DateOnly>(1);
                return _auditEntries.Where(entry => entry.StartDate <= to && entry.EndDate >= from).ToList();
            });
        return repository;
    }

    /// <summary>
    /// What the real close handler leaves behind for a group-scoped seal: one day lock per day of the range and
    /// one Seal audit entry recorded under the acting admin with the autonomous actor name.
    /// </summary>
    private void SealAsKlacksy(ClosePeriodByGroupCommand command)
    {
        for (var day = command.StartDate; day <= command.EndDate; day = day.AddDays(1))
        {
            _sealed.Add((command.GroupId, day));
        }

        _auditEntries.Add(PeriodAuditLog.For(
            PeriodAuditAction.Seal, command.StartDate, command.EndDate, command.GroupId, command.Reason, 31,
            command.ActingAdminUserId!.Value.ToString(), AuditActorDefaults.AutonomousActorName));
    }

    /// <summary>A person sealing some days of the group: day locks plus the Seal entry the handler always writes.</summary>
    private void SealAsPerson(Guid? groupId, DateOnly from, DateOnly until)
    {
        for (var day = from; day <= until; day = day.AddDays(1))
        {
            _sealed.Add((groupId, day));
        }

        GivenAuditEntry(PeriodAuditAction.Seal, groupId, from, until);
    }

    private void GivenAuditEntry(PeriodAuditAction action, Guid? groupId, DateOnly? start = null, DateOnly? end = null)
    {
        _auditEntries.Add(PeriodAuditLog.For(
            action, start ?? AugustStart, end ?? AugustEnd, groupId, "manual", 31, "admin-user", "Ada Lovelace"));
    }

    private PeriodAutoCloseService CreateSut(DateOnly? today = null) =>
        new(_groupRepository,
            _weekConfiguration,
            _settingsReader,
            new FixedCompanyClock(new DateTimeOffset((today ?? DefaultToday).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))),
            _resolver,
            _sealedDays,
            _activityProbe,
            _auditLog,
            _scopeFactory,
            NullLogger<PeriodAutoCloseService>.Instance);

    private void StubLag(string? value)
    {
        _settingsReader.GetSetting(SettingKeys.PeriodCloseLagDays).Returns(Task.FromResult<SettingsRow?>(
            value == null ? null : new SettingsRow { Type = SettingKeys.PeriodCloseLagDays, Value = value }));
    }

    private static void StubAutonomy(IPeriodAutoCloseResolver resolver, bool allowed)
    {
        resolver.ResolveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new PeriodAutoCloseDecision(
                allowed ? AutonomyLevel.FullyAutonomous : AutonomyLevel.Autonomous,
                DecidingAdminId,
                allowed,
                allowed ? PeriodAutoCloseBlockedBy.None : PeriodAutoCloseBlockedBy.AdminLevel));
    }

    private void StubIssues(params ScheduleValidationType[] severities)
    {
        _freshMediator.Send(Arg.Any<GetPeriodIssuesQuery>(), Arg.Any<CancellationToken>())
            .Returns(severities.Select(severity => new PeriodIssueDto { Severity = severity }).ToList());
    }

    private List<Group> StubGroups(params Group[] groups)
    {
        _groupRepository.List().Returns(groups.ToList());
        _groupRepository.GetGroupIdsWithMembersAsync(Arg.Any<CancellationToken>())
            .Returns(groups.Select(group => group.Id).ToList());
        return groups.ToList();
    }

    private static Group MakeGroup(PaymentInterval interval = PaymentInterval.Monthly, string name = "Bern") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        PaymentInterval = interval,
        ValidFrom = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };


    private async Task AssertNothingSentToTheCloseHandlerAsync()
    {
        await _freshMediator.DidNotReceive().Send(Arg.Any<ClosePeriodByGroupCommand>(), Arg.Any<CancellationToken>());
    }

    private static PeriodAutoCloseBlockedTriggerEvent SingleBlocked(IReadOnlyList<IAgentTriggerEvent> events)
    {
        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0], Is.InstanceOf<PeriodAutoCloseBlockedTriggerEvent>());
        return (PeriodAutoCloseBlockedTriggerEvent)events[0];
    }

    [Test]
    public async Task RunAsync_AllGatesHold_SealsTheGroupPeriodUnderTheDecidingAdminAndReportsIt()
    {
        var group = StubGroups(MakeGroup())[0];

        var events = await CreateSut().RunAsync();

        await _freshMediator.Received(1).Send(
            Arg.Is<ClosePeriodByGroupCommand>(command =>
                command.StartDate == AugustStart
                && command.EndDate == AugustEnd
                && command.GroupId == group.Id
                && command.ActingAdminUserId == DecidingAdminId
                && !command.AcknowledgeViolations
                && command.AcknowledgedErrorCount == null
                && command.Reason == "Automatic close by Klacksy (fully autonomous, close lag 1 day(s))"),
            Arg.Any<CancellationToken>());
        Assert.That(events, Has.Count.EqualTo(1));
        var closed = events[0] as PeriodAutoClosedTriggerEvent;
        Assert.That(closed, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(closed!.GroupId, Is.EqualTo(group.Id));
            Assert.That(closed.PeriodStartDate, Is.EqualTo(AugustStart));
            Assert.That(closed.PeriodEndDate, Is.EqualTo(AugustEnd));
            Assert.That(closed.DecidingAdminUserId, Is.EqualTo(DecidingAdminId));
            Assert.That(closed.Kind, Is.EqualTo(AgentTriggerKinds.PeriodAutoClose));
        });
        await _freshResolver.Received(1).ResolveAsync(group.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_AutonomyNotAllowed_DoesNothingAndReportsNothing()
    {
        StubGroups(MakeGroup());
        StubAutonomy(_resolver, allowed: false);
        StubLag(null);

        var events = await CreateSut().RunAsync();

        Assert.That(events, Is.Empty);
        await AssertNothingSentToTheCloseHandlerAsync();
        await _activityProbe.DidNotReceiveWithAnyArgs().HasDirectWorkInRangeAsync(default!, default, default, default);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("abc")]
    [TestCase("-1")]
    [TestCase("32")]
    public async Task RunAsync_NoValidLagStored_NeverClosesAndReportsWhy(string? storedLag)
    {
        var group = StubGroups(MakeGroup())[0];
        StubLag(storedLag);

        var events = await CreateSut().RunAsync();

        var blocked = SingleBlocked(events);
        Assert.Multiple(() =>
        {
            Assert.That(blocked.Reason, Is.EqualTo(PeriodAutoCloseBlockReason.NoLagStored));
            Assert.That(blocked.GroupId, Is.EqualTo(group.Id));
            Assert.That(blocked.PeriodEndDate, Is.EqualTo(AugustEnd));
        });
        await AssertNothingSentToTheCloseHandlerAsync();
    }

    [Test]
    public async Task RunAsync_LagStoredButCloseDateOfTheLastPeriodNotReached_LeavesThatPeriodAlone()
    {
        var group = StubGroups(MakeGroup())[0];
        StubLag("5");
        SealAsPerson(group.Id, new DateOnly(2026, 7, 1), JulyEnd);

        var events = await CreateSut().RunAsync();

        Assert.That(events, Is.Empty);
        await AssertNothingSentToTheCloseHandlerAsync();
        await _sealedDays.DidNotReceive().GetRangeAsync(
            Arg.Any<DateOnly>(), AugustEnd, Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_TodayIsThePeriodEndWithLagZero_DoesNotCloseTheRunningPeriod()
    {
        var group = StubGroups(MakeGroup())[0];
        StubLag("0");
        SealAsPerson(group.Id, new DateOnly(2026, 7, 1), JulyEnd);

        var events = await CreateSut(AugustEnd).RunAsync();

        Assert.That(events, Is.Empty);
        await AssertNothingSentToTheCloseHandlerAsync();
    }

    [Test]
    public async Task RunAsync_LagZeroOnTheDayAfterThePeriodEnd_Closes()
    {
        StubGroups(MakeGroup());
        StubLag("0");

        var events = await CreateSut(new DateOnly(2026, 9, 1)).RunAsync();

        Assert.That(events.Single(), Is.InstanceOf<PeriodAutoClosedTriggerEvent>());
    }

    [Test]
    public async Task RunAsync_IndividualGroup_IsNeverEvaluated()
    {
        StubGroups(MakeGroup(PaymentInterval.Individual));

        var events = await CreateSut().RunAsync();

        Assert.That(events, Is.Empty);
        await _resolver.DidNotReceiveWithAnyArgs().ResolveAsync(default, default);
        await AssertNothingSentToTheCloseHandlerAsync();
    }

    [Test]
    public async Task RunAsync_PeriodAlreadySealed_IsSkippedSilentlySoItIsNeverClosedTwice()
    {
        var group = StubGroups(MakeGroup())[0];
        SealAsPerson(group.Id, AugustStart, AugustEnd);

        var events = await CreateSut().RunAsync();

        Assert.That(events, Is.Empty);
        await AssertNothingSentToTheCloseHandlerAsync();
    }

    [Test]
    public async Task RunAsync_ClosedByAPersonBetweenEvaluationAndSeal_IsNotClosedAgain()
    {
        var group = StubGroups(MakeGroup())[0];
        _freshResolver.ResolveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                SealAsPerson(group.Id, AugustStart, AugustEnd);

                return new PeriodAutoCloseDecision(AutonomyLevel.FullyAutonomous, DecidingAdminId, true, PeriodAutoCloseBlockedBy.None);
            });

        var events = await CreateSut().RunAsync();

        Assert.That(events, Is.Empty);
        await AssertNothingSentToTheCloseHandlerAsync();
    }

    [Test]
    public async Task RunAsync_NoWorkInThePeriod_IsSkippedSilently()
    {
        StubGroups(MakeGroup());
        _activityProbe.HasDirectWorkInRangeAsync(Arg.Any<Group>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var events = await CreateSut().RunAsync();

        Assert.That(events, Is.Empty);
        await AssertNothingSentToTheCloseHandlerAsync();
    }

    [Test]
    public async Task RunAsync_PeriodHoldsErrors_NeverClosesAndReportsTheCount()
    {
        StubGroups(MakeGroup());
        StubIssues(ScheduleValidationType.Error, ScheduleValidationType.Warning, ScheduleValidationType.Error);

        var events = await CreateSut().RunAsync();

        var blocked = SingleBlocked(events);
        Assert.Multiple(() =>
        {
            Assert.That(blocked.Reason, Is.EqualTo(PeriodAutoCloseBlockReason.OpenErrors));
            Assert.That(blocked.ErrorCount, Is.EqualTo(2));
        });
        await AssertNothingSentToTheCloseHandlerAsync();
    }

    [Test]
    public async Task RunAsync_OnlyWarningsAndNotes_Closes()
    {
        StubGroups(MakeGroup());
        StubIssues(ScheduleValidationType.Warning, ScheduleValidationType.Info);

        var events = await CreateSut().RunAsync();

        Assert.That(events.Single(), Is.InstanceOf<PeriodAutoClosedTriggerEvent>());
    }

    [Test]
    public async Task RunAsync_AutonomyWithdrawnRightBeforeTheSeal_DoesNotClose()
    {
        StubGroups(MakeGroup());
        StubAutonomy(_freshResolver, allowed: false);

        var events = await CreateSut().RunAsync();

        Assert.That(SingleBlocked(events).Reason, Is.EqualTo(PeriodAutoCloseBlockReason.AutonomyLowered));
        await AssertNothingSentToTheCloseHandlerAsync();
    }

    [Test]
    public async Task RunAsync_CloseDateLongerAgoThanTheWindow_IsLeftToAPerson()
    {
        StubGroups(MakeGroup());
        StubLag("0");

        var events = await CreateSut(new DateOnly(2026, 9, 5)).RunAsync();

        Assert.That(SingleBlocked(events).Reason, Is.EqualTo(PeriodAutoCloseBlockReason.CloseWindowMissed));
        await AssertNothingSentToTheCloseHandlerAsync();
    }

    [Test]
    public async Task RunAsync_LastDayOfTheWindow_StillCloses()
    {
        StubGroups(MakeGroup());
        StubLag("0");

        var events = await CreateSut(new DateOnly(2026, 9, 4)).RunAsync();

        Assert.That(events.Single(), Is.InstanceOf<PeriodAutoClosedTriggerEvent>());
    }

    [Test]
    public async Task RunAsync_SomeDaysSealedButNotTheLast_IsLeftToAPerson()
    {
        var group = StubGroups(MakeGroup())[0];
        SealAsPerson(group.Id, AugustStart, new DateOnly(2026, 8, 10));

        var events = await CreateSut().RunAsync();

        Assert.That(SingleBlocked(events).Reason, Is.EqualTo(PeriodAutoCloseBlockReason.PartiallySealed));
        await AssertNothingSentToTheCloseHandlerAsync();
    }

    [Test]
    public async Task RunAsync_HandlerRefusesWithErrors_ReportsOpenErrors()
    {
        StubGroups(MakeGroup());
        _freshMediator.Send(Arg.Any<ClosePeriodByGroupCommand>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PeriodValidationConflictException("errors", 4));

        var events = await CreateSut().RunAsync();

        var blocked = SingleBlocked(events);
        Assert.Multiple(() =>
        {
            Assert.That(blocked.Reason, Is.EqualTo(PeriodAutoCloseBlockReason.OpenErrors));
            Assert.That(blocked.ErrorCount, Is.EqualTo(4));
        });
    }

    [Test]
    public async Task RunAsync_HandlerRefusesThePermission_ReportsRefused()
    {
        StubGroups(MakeGroup());
        _freshMediator.Send(Arg.Any<ClosePeriodByGroupCommand>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidRequestException("You do not have permission to close periods."));

        var events = await CreateSut().RunAsync();

        Assert.That(SingleBlocked(events).Reason, Is.EqualTo(PeriodAutoCloseBlockReason.Refused));
    }

    [Test]
    public async Task RunAsync_ReadBackDoesNotShowTheWholePeriodSealed_ReportsNotVerified()
    {
        StubGroups(MakeGroup());
        _freshMediator.Send(Arg.Any<ClosePeriodByGroupCommand>(), Arg.Any<CancellationToken>()).Returns(0);

        var events = await CreateSut().RunAsync();

        var blocked = SingleBlocked(events);
        Assert.Multiple(() =>
        {
            Assert.That(blocked.Reason, Is.EqualTo(PeriodAutoCloseBlockReason.NotVerified));
            Assert.That(blocked.Severity, Is.EqualTo(AgentTriggerSeverity.High));
        });
    }

    [Test]
    public async Task RunAsync_FailureInTheFirstGroup_DoesNotStopTheSecond()
    {
        var groups = StubGroups(MakeGroup(name: "Broken"), MakeGroup(name: "Healthy"));
        _activityProbe.HasDirectWorkInRangeAsync(groups[0], Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database hiccup"));

        var events = await CreateSut().RunAsync();

        Assert.That(events, Has.Count.EqualTo(2));
        var failed = events.OfType<PeriodAutoCloseBlockedTriggerEvent>().Single();
        var closed = events.OfType<PeriodAutoClosedTriggerEvent>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(failed.GroupId, Is.EqualTo(groups[0].Id));
            Assert.That(failed.Reason, Is.EqualTo(PeriodAutoCloseBlockReason.Failed));
            Assert.That(closed.GroupId, Is.EqualTo(groups[1].Id));
        });
        await _freshMediator.Received(1).Send(
            Arg.Is<ClosePeriodByGroupCommand>(command => command.GroupId == groups[1].Id), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_ResolverThrows_SkipsTheGroupWithoutAFalseReport()
    {
        StubGroups(MakeGroup());
        _resolver.ResolveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("settings unreadable"));

        var events = await CreateSut().RunAsync();

        Assert.That(events, Is.Empty);
        await AssertNothingSentToTheCloseHandlerAsync();
    }

    [Test]
    public async Task RunAsync_EveryCloseIsGroupScoped_NeverGlobal()
    {
        StubGroups(MakeGroup(name: "A"), MakeGroup(name: "B"));

        await CreateSut().RunAsync();

        await _freshMediator.Received(2).Send(Arg.Any<ClosePeriodByGroupCommand>(), Arg.Any<CancellationToken>());
        await _freshMediator.DidNotReceive().Send(
            Arg.Is<ClosePeriodByGroupCommand>(command => command.GroupId == null), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_PeriodReopenedByAPerson_IsNeverClosedAgainAutomatically()
    {
        var group = StubGroups(MakeGroup())[0];
        GivenAuditEntry(PeriodAuditAction.Seal, group.Id);
        GivenAuditEntry(PeriodAuditAction.Unseal, group.Id);

        var events = await CreateSut().RunAsync();

        Assert.That(events, Is.Empty);
        await AssertNothingSentToTheCloseHandlerAsync();
    }

    [Test]
    public async Task RunAsync_InstallationWideUnsealOverlappingThePeriod_BlocksTheAutomaticClose()
    {
        StubGroups(MakeGroup());
        GivenAuditEntry(PeriodAuditAction.Unseal, null, new DateOnly(2026, 8, 20), new DateOnly(2026, 9, 5));

        var events = await CreateSut().RunAsync();

        Assert.That(events, Is.Empty);
        await AssertNothingSentToTheCloseHandlerAsync();
    }

    [Test]
    public async Task RunAsync_HistoryOfAnotherGroupOrOfOtherActions_DoesNotBlock()
    {
        StubGroups(MakeGroup());
        GivenAuditEntry(PeriodAuditAction.Unseal, Guid.NewGuid());
        GivenAuditEntry(PeriodAuditAction.ConfirmWork, null);
        GivenAuditEntry(PeriodAuditAction.Seal, null, new DateOnly(2026, 7, 1), JulyEnd);

        var events = await CreateSut().RunAsync();

        Assert.That(events.Single(), Is.InstanceOf<PeriodAutoClosedTriggerEvent>());
    }

    [Test]
    public async Task RunAsync_ReopenedBetweenEvaluationAndSeal_IsNotClosed()
    {
        var group = StubGroups(MakeGroup())[0];
        _freshResolver.ResolveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                GivenAuditEntry(PeriodAuditAction.Unseal, group.Id);
                return new PeriodAutoCloseDecision(AutonomyLevel.FullyAutonomous, DecidingAdminId, true, PeriodAutoCloseBlockedBy.None);
            });

        var events = await CreateSut().RunAsync();

        Assert.That(events, Is.Empty);
        await AssertNothingSentToTheCloseHandlerAsync();
    }

    [Test]
    public async Task RunAsync_PartlySealedByAPersonWithItsSealEntry_IsReportedAsPartiallySealed()
    {
        var group = StubGroups(MakeGroup())[0];
        SealAsPerson(group.Id, AugustStart, new DateOnly(2026, 8, 15));

        var events = await CreateSut().RunAsync();

        Assert.That(SingleBlocked(events).Reason, Is.EqualTo(PeriodAutoCloseBlockReason.PartiallySealed));
        await AssertNothingSentToTheCloseHandlerAsync();
    }

    [Test]
    public async Task RunAsync_InstallationWideDayLockInsideThePeriod_IsReportedAsPartiallySealed()
    {
        StubGroups(MakeGroup());
        SealAsPerson(null, new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 3));

        var events = await CreateSut().RunAsync();

        Assert.That(SingleBlocked(events).Reason, Is.EqualTo(PeriodAutoCloseBlockReason.PartiallySealed));
        await AssertNothingSentToTheCloseHandlerAsync();
    }

    [Test]
    public async Task RunAsync_InstallationWideLockOnTheLastDay_CountsAsClosedAndStaysSilent()
    {
        StubGroups(MakeGroup());
        SealAsPerson(null, AugustStart, AugustEnd);

        var events = await CreateSut().RunAsync();

        Assert.That(events, Is.Empty);
        await AssertNothingSentToTheCloseHandlerAsync();
    }

    [Test]
    public async Task RunAsync_ParentGroupWhoseWorkHangsOnlyOnChildGroups_IsNotClosed()
    {
        StubGroups(MakeGroup(name: "Parent"));
        _activityProbe.HasWorkInRangeAsync(Arg.Any<Group>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _activityProbe.HasDirectWorkInRangeAsync(Arg.Any<Group>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var events = await CreateSut().RunAsync();

        Assert.That(events, Is.Empty);
        await AssertNothingSentToTheCloseHandlerAsync();
    }

    [Test]
    public async Task RunAsync_ClosedByAPersonAtTheSameMoment_UniqueIndexFailureStaysSilent()
    {
        var group = StubGroups(MakeGroup())[0];
        _freshMediator.Send(Arg.Any<ClosePeriodByGroupCommand>(), Arg.Any<CancellationToken>())
            .Returns<int>(_ =>
            {
                SealAsPerson(group.Id, AugustStart, AugustEnd);
                throw new InvalidOperationException("duplicate key value violates unique constraint ix_sealed_day_date_group");
            });

        var events = await CreateSut().RunAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task RunAsync_CloseThrowsAfterItsOwnSealLanded_IsReportedAsClosed()
    {
        StubGroups(MakeGroup());
        _freshMediator.Send(Arg.Any<ClosePeriodByGroupCommand>(), Arg.Any<CancellationToken>())
            .Returns<int>(call =>
            {
                SealAsKlacksy(call.Arg<ClosePeriodByGroupCommand>());
                throw new InvalidOperationException("connection reset after commit");
            });

        var events = await CreateSut().RunAsync();

        Assert.That(events.Single(), Is.InstanceOf<PeriodAutoClosedTriggerEvent>());
    }

    [Test]
    public async Task RunAsync_CloseThrowsAndNothingIsSealed_IsReportedAsFailed()
    {
        StubGroups(MakeGroup());
        _freshMediator.Send(Arg.Any<ClosePeriodByGroupCommand>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        var events = await CreateSut().RunAsync();

        var blocked = SingleBlocked(events);
        Assert.Multiple(() =>
        {
            Assert.That(blocked.Reason, Is.EqualTo(PeriodAutoCloseBlockReason.Failed));
            Assert.That(blocked.Severity, Is.EqualTo(AgentTriggerSeverity.High));
        });
    }
}
