// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ScheduledTaskRunner: it fires due tasks, skips stale ones, runs skills under the
/// owner's identity with the autonomy gate bypassed, delivers live or via a durable pending note, and
/// records the outcome; a lost claim does nothing.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Scheduling;
using Klacks.Api.Domain.Constants;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Assistant.Scheduling;

[TestFixture]
public class ScheduledTaskRunnerTests
{
    private IScheduledTaskRepository _repository = null!;
    private ISkillExecutor _skillExecutor = null!;
    private IAssistantNotificationService _notification = null!;
    private IPendingUserNoteRepository _pendingNotes = null!;
    private IAgentRepository _agentRepository = null!;
    private ScheduledTaskRunner _runner = null!;

    private static readonly Guid Owner = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IScheduledTaskRepository>();
        _skillExecutor = Substitute.For<ISkillExecutor>();
        _notification = Substitute.For<IAssistantNotificationService>();
        _pendingNotes = Substitute.For<IPendingUserNoteRepository>();
        _agentRepository = Substitute.For<IAgentRepository>();

        _repository.TryClaimAsync(Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>())
            .Returns(new Agent { Id = Guid.NewGuid() });

        _runner = new ScheduledTaskRunner(
            _repository,
            _skillExecutor,
            _notification,
            _pendingNotes,
            _agentRepository,
            Substitute.For<ILogger<ScheduledTaskRunner>>());
    }

    private ScheduledTask Reminder(DateTime nextRunUtc) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Weekly coverage",
        CronExpression = "0 8 * * 1",
        TimeZoneId = "Europe/Zurich",
        ActionType = ScheduledTaskActionTypes.Reminder,
        MessageText = "Check next week's coverage",
        OwnerUserId = Owner,
        OwnerUserName = "alice",
        IsEnabled = true,
        NextRunUtc = nextRunUtc
    };

    private void Due(params ScheduledTask[] tasks) =>
        _repository.GetDueAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(tasks.ToList());

    [Test]
    public async Task RunDueAsync_FiresReminder_DeliversLive_AndRecordsOk()
    {
        var task = Reminder(DateTime.UtcNow.AddMinutes(-1));
        Due(task);
        _notification.IsUserConnected(Owner.ToString()).Returns(true);

        await _runner.RunDueAsync();

        await _notification.Received(1).SendProactiveMessageAsync(
            Owner.ToString(), Arg.Is<string>(m => m.Contains("Check next week's coverage")));
        await _repository.Received(1).UpdateAsync(
            Arg.Is<ScheduledTask>(t => t.LastStatus == ScheduledTaskRunStatus.Ok && t.RunCount == 1 && t.NextRunUtc != null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunDueAsync_OfflineOwner_StashesPendingNote()
    {
        var task = Reminder(DateTime.UtcNow.AddMinutes(-1));
        Due(task);
        _notification.IsUserConnected(Owner.ToString()).Returns(false);

        await _runner.RunDueAsync();

        await _notification.DidNotReceive().SendProactiveMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        await _pendingNotes.Received(1).AddAsync(
            Arg.Is<PendingUserNote>(n => n.UserId == Owner && n.Content.Contains("Check next week's coverage")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunDueAsync_StaleOccurrence_SkippedWithoutDelivery()
    {
        var task = Reminder(DateTime.UtcNow.AddHours(-1));
        Due(task);
        _notification.IsUserConnected(Owner.ToString()).Returns(true);

        await _runner.RunDueAsync();

        await _notification.DidNotReceive().SendProactiveMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        await _repository.Received(1).UpdateAsync(
            Arg.Is<ScheduledTask>(t => t.LastStatus == ScheduledTaskRunStatus.Skipped && t.RunCount == 0),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunDueAsync_SkillAction_RunsUnderOwnerWithGateBypassed()
    {
        var task = Reminder(DateTime.UtcNow.AddMinutes(-1));
        task.ActionType = ScheduledTaskActionTypes.Skill;
        task.SkillName = "get_user_context";
        task.MessageText = null;
        task.OwnerPermissionsCsv = "CanViewClients,CanEditClients";
        Due(task);
        _notification.IsUserConnected(Owner.ToString()).Returns(true);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "Report ready"));

        await _runner.RunDueAsync();

        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Is<SkillInvocation>(i => i.SkillName == "get_user_context"),
            Arg.Is<SkillExecutionContext>(c =>
                c.BypassAutonomyGate &&
                c.UserId == Owner &&
                c.UserPermissions.Contains("CanViewClients")),
            Arg.Any<CancellationToken>());
        await _notification.Received(1).SendProactiveMessageAsync(
            Owner.ToString(), Arg.Is<string>(m => m.Contains("Report ready")));
    }

    [Test]
    public async Task RunDueAsync_LostClaim_DoesNothing()
    {
        var task = Reminder(DateTime.UtcNow.AddMinutes(-1));
        Due(task);
        _repository.TryClaimAsync(Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        await _runner.RunDueAsync();

        await _notification.DidNotReceive().SendProactiveMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<ScheduledTask>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunDueAsync_MaxRunsReached_DisablesTask()
    {
        var task = Reminder(DateTime.UtcNow.AddMinutes(-1));
        task.MaxRuns = 1;
        Due(task);
        _notification.IsUserConnected(Owner.ToString()).Returns(true);

        await _runner.RunDueAsync();

        await _repository.Received(1).UpdateAsync(
            Arg.Is<ScheduledTask>(t => !t.IsEnabled && t.NextRunUtc == null && t.RunCount == 1),
            Arg.Any<CancellationToken>());
    }
}
