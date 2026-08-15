// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ScheduledTaskRunner: it fires due tasks, skips stale ones, runs skills under the
/// owner's identity with the autonomy gate bypassed, always stashes a durable pending note and only
/// acknowledges it after a successful live send, and records the outcome; a lost claim does nothing. Skill actions the unattended policy refuses disable
/// the task instead of running, while reminders never consult the policy at all.
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
    private IUnattendedSkillPolicy _unattendedPolicy = null!;
    private IInternalTokenIssuer _tokenIssuer = null!;
    private ScheduledTaskRunner _runner = null!;
    private List<PendingUserNote> _stashedNotes = null!;

    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid AgentGuid = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IScheduledTaskRepository>();
        _skillExecutor = Substitute.For<ISkillExecutor>();
        _notification = Substitute.For<IAssistantNotificationService>();
        _pendingNotes = Substitute.For<IPendingUserNoteRepository>();
        _agentRepository = Substitute.For<IAgentRepository>();
        _unattendedPolicy = Substitute.For<IUnattendedSkillPolicy>();
        _tokenIssuer = Substitute.For<IInternalTokenIssuer>();

        _stashedNotes = new List<PendingUserNote>();
        _pendingNotes.When(r => r.AddAsync(Arg.Any<PendingUserNote>(), Arg.Any<CancellationToken>()))
            .Do(ci => _stashedNotes.Add(ci.ArgAt<PendingUserNote>(0)));

        _repository.TryClaimAsync(Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>())
            .Returns(new Agent { Id = AgentGuid });
        _unattendedPolicy.Decide(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>())
            .Returns(UnattendedSkillDecision.Allow());
        _tokenIssuer.IssueForOwnerAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(InternalTokenResult.Issued(new BearerToken("owner-jwt"), new[] { Roles.Authorised }));

        _runner = new ScheduledTaskRunner(
            _repository,
            _skillExecutor,
            _notification,
            _pendingNotes,
            _agentRepository,
            _unattendedPolicy,
            _tokenIssuer,
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
        _notification.IsUserConnectedAsync(Owner.ToString()).Returns(true);

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
        _notification.IsUserConnectedAsync(Owner.ToString()).Returns(false);

        await _runner.RunDueAsync();

        await _notification.DidNotReceive().SendProactiveMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        await _pendingNotes.Received(1).AddAsync(
            Arg.Is<PendingUserNote>(n => n.UserId == Owner && n.Content.Contains("Check next week's coverage")),
            Arg.Any<CancellationToken>());
        await _pendingNotes.DidNotReceive().MarkDeliveredAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunDueAsync_OwnerReportedConnected_ButLiveSendFails_KeepsTheNotePending()
    {
        var task = Reminder(DateTime.UtcNow.AddMinutes(-1));
        Due(task);
        _notification.IsUserConnectedAsync(Owner.ToString()).Returns(true);
        _notification.SendProactiveMessageAsync(Owner.ToString(), Arg.Any<string>())
            .Returns<Task>(_ => throw new InvalidOperationException("stale presence, no live connection"));

        await _runner.RunDueAsync();

        _stashedNotes.Count.ShouldBe(1);
        _stashedNotes[0].UserId.ShouldBe(Owner);
        _stashedNotes[0].Content.ShouldContain("Check next week's coverage");
        await _pendingNotes.DidNotReceive().MarkDeliveredAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunDueAsync_ConnectedOwner_HasExactlyThatStashedNoteAcknowledged()
    {
        var task = Reminder(DateTime.UtcNow.AddMinutes(-1));
        Due(task);
        _notification.IsUserConnectedAsync(Owner.ToString()).Returns(true);

        await _runner.RunDueAsync();

        var note = _stashedNotes.Single(n => n.UserId == Owner);
        note.Id.ShouldNotBe(Guid.Empty);
        await _pendingNotes.Received(1).MarkDeliveredAsync(
            AgentGuid,
            Owner,
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(note.Id)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunDueAsync_StaleOccurrence_SkippedWithoutDelivery()
    {
        var task = Reminder(DateTime.UtcNow.AddHours(-1));
        Due(task);
        _notification.IsUserConnectedAsync(Owner.ToString()).Returns(true);

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
        _notification.IsUserConnectedAsync(Owner.ToString()).Returns(true);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "Report ready"));

        await _runner.RunDueAsync();

        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Is<SkillInvocation>(i => i.SkillName == "get_user_context"),
            Arg.Is<SkillExecutionContext>(c =>
                c.BypassAutonomyGate &&
                c.UserId == Owner &&
                c.AccessToken!.Value == "owner-jwt" &&
                c.UserPermissions.Contains("CanViewClients")),
            Arg.Any<CancellationToken>());
        await _notification.Received(1).SendProactiveMessageAsync(
            Owner.ToString(), Arg.Is<string>(m => m.Contains("Report ready")));
    }

    [Test]
    public async Task RunDueAsync_SkillAction_RightsComeFromCurrentRoles_NotTheFrozenCsv()
    {
        var task = Reminder(DateTime.UtcNow.AddMinutes(-1));
        task.ActionType = ScheduledTaskActionTypes.Skill;
        task.SkillName = "get_user_context";
        task.MessageText = null;
        // The frozen CSV still claims admin rights; the owner has since been downgraded to User.
        task.OwnerPermissionsCsv = "Admin,CanDeleteClients";
        Due(task);
        _notification.IsUserConnectedAsync(Owner.ToString()).Returns(true);
        _tokenIssuer.IssueForOwnerAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(InternalTokenResult.Issued(new BearerToken("owner-jwt"), new[] { Roles.User }));
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "done"));

        await _runner.RunDueAsync();

        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Any<SkillInvocation>(),
            Arg.Is<SkillExecutionContext>(c =>
                !c.UserPermissions.Contains(Roles.Admin) &&
                !c.UserPermissions.Contains(Permissions.CanDeleteClients) &&
                c.UserPermissions.Contains(Permissions.CanViewClients)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunDueAsync_NoTokenForOwner_ReportsTheReasonAndKeepsTheTaskEnabled()
    {
        var task = Reminder(DateTime.UtcNow.AddMinutes(-1));
        task.ActionType = ScheduledTaskActionTypes.Skill;
        task.SkillName = "get_user_context";
        task.MessageText = null;
        Due(task);
        _notification.IsUserConnectedAsync(Owner.ToString()).Returns(true);
        _tokenIssuer.IssueForOwnerAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(InternalTokenResult.Refused("the owner account is locked out"));

        await _runner.RunDueAsync();

        await _skillExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
        await _repository.Received(1).UpdateAsync(
            Arg.Is<ScheduledTask>(t =>
                t.LastStatus == ScheduledTaskRunStatus.Error &&
                t.IsEnabled &&
                t.NextRunUtc != null),
            Arg.Any<CancellationToken>());
        await _notification.Received(1).SendProactiveMessageAsync(
            Owner.ToString(), Arg.Is<string>(m => m.Contains("locked out")));
    }

    [Test]
    public async Task RunDueAsync_Reminder_NeedsNoToken()
    {
        var task = Reminder(DateTime.UtcNow.AddMinutes(-1));
        Due(task);
        _notification.IsUserConnectedAsync(Owner.ToString()).Returns(true);

        await _runner.RunDueAsync();

        await _tokenIssuer.DidNotReceive().IssueForOwnerAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
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
    public async Task RunDueAsync_SkillAction_RefusedByPolicy_DisablesTaskWithoutRunningTheSkill()
    {
        var task = Reminder(DateTime.UtcNow.AddMinutes(-1));
        task.ActionType = ScheduledTaskActionTypes.Skill;
        task.SkillName = "delete_client";
        task.MessageText = null;
        task.OwnerPermissionsCsv = null;
        Due(task);
        _notification.IsUserConnectedAsync(Owner.ToString()).Returns(true);
        _unattendedPolicy.Decide(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>())
            .Returns(UnattendedSkillDecision.Deny("Owner permissions were never frozen."));

        await _runner.RunDueAsync();

        await _skillExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
        await _repository.Received(1).UpdateAsync(
            Arg.Is<ScheduledTask>(t =>
                t.LastStatus == ScheduledTaskRunStatus.Error &&
                !t.IsEnabled &&
                t.NextRunUtc == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunDueAsync_SkillAction_RefusedByPolicy_TellsTheOwnerWhy()
    {
        var task = Reminder(DateTime.UtcNow.AddMinutes(-1));
        task.ActionType = ScheduledTaskActionTypes.Skill;
        task.SkillName = "delete_client";
        task.MessageText = null;
        Due(task);
        _notification.IsUserConnectedAsync(Owner.ToString()).Returns(true);
        _unattendedPolicy.Decide(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>())
            .Returns(UnattendedSkillDecision.Deny("Skill 'delete_client' is now classified as sensitive."));

        await _runner.RunDueAsync();

        await _notification.Received(1).SendProactiveMessageAsync(
            Owner.ToString(), Arg.Is<string>(m => m.Contains("classified as sensitive")));
    }

    [Test]
    public async Task RunDueAsync_Reminder_WithoutFrozenPermissions_StillFires()
    {
        var task = Reminder(DateTime.UtcNow.AddMinutes(-1));
        task.OwnerPermissionsCsv = null;
        Due(task);
        _notification.IsUserConnectedAsync(Owner.ToString()).Returns(true);

        await _runner.RunDueAsync();

        _unattendedPolicy.DidNotReceive().Decide(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>());
        await _repository.Received(1).UpdateAsync(
            Arg.Is<ScheduledTask>(t => t.LastStatus == ScheduledTaskRunStatus.Ok && t.IsEnabled),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunDueAsync_MaxRunsReached_DisablesTask()
    {
        var task = Reminder(DateTime.UtcNow.AddMinutes(-1));
        task.MaxRuns = 1;
        Due(task);
        _notification.IsUserConnectedAsync(Owner.ToString()).Returns(true);

        await _runner.RunDueAsync();

        await _repository.Received(1).UpdateAsync(
            Arg.Is<ScheduledTask>(t => !t.IsEnabled && t.NextRunUtc == null && t.RunCount == 1),
            Arg.Any<CancellationToken>());
    }
}
