// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for EmailAnalysisNotifier — verifies planner/admin audience union, live delivery
/// to connected users, durable PendingUserNote stashing before every send, acknowledgement of
/// exactly that note after a successful live send (no double relay), retention of the note when
/// the send fails despite a positive presence report, and that a missing default agent or a
/// per-user failure never aborts the batch.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Email;
using Klacks.Api.Infrastructure.Email;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Email;

[TestFixture]
public class EmailAnalysisNotifierTests
{
    private IPlanningAudienceResolver _audienceResolver = null!;
    private IAssistantNotificationService _notificationService = null!;
    private IPendingUserNoteRepository _pendingNotes = null!;
    private IAgentRepository _agentRepository = null!;
    private EmailAnalysisNotifier _notifier = null!;
    private List<PendingUserNote> _stashedNotes = null!;

    private static readonly Guid PlannerGuid = Guid.NewGuid();
    private static readonly Guid AdminGuid = Guid.NewGuid();
    private static readonly Guid AgentGuid = Guid.NewGuid();
    private static readonly string Planner = PlannerGuid.ToString();
    private static readonly string Admin = AdminGuid.ToString();

    [SetUp]
    public void SetUp()
    {
        _audienceResolver = Substitute.For<IPlanningAudienceResolver>();
        _notificationService = Substitute.For<IAssistantNotificationService>();
        _pendingNotes = Substitute.For<IPendingUserNoteRepository>();
        _agentRepository = Substitute.For<IAgentRepository>();

        _stashedNotes = new List<PendingUserNote>();
        _pendingNotes.When(r => r.AddAsync(Arg.Any<PendingUserNote>(), Arg.Any<CancellationToken>()))
            .Do(ci => _stashedNotes.Add(ci.ArgAt<PendingUserNote>(0)));

        _audienceResolver.GetPlanningUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { Planner });
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { Admin });
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>())
            .Returns(new Agent { Id = AgentGuid, Name = "Klacksy" });

        _notifier = new EmailAnalysisNotifier(
            _audienceResolver, _notificationService, _pendingNotes, _agentRepository,
            Substitute.For<ILogger<EmailAnalysisNotifier>>());
    }

    private static ReceivedEmail Email() => new()
    {
        Id = Guid.NewGuid(),
        FromAddress = "worker@example.com",
        FromName = "Max Muster",
        Subject = "Krankmeldung"
    };

    private static EmailAnalysis Analysis() => new()
    {
        Intent = EmailIntent.WorkCancellation,
        Summary = "Mitarbeiter meldet sich für morgen krank.",
        FromDate = new DateOnly(2026, 7, 9),
        UntilDate = new DateOnly(2026, 7, 9)
    };

    [Test]
    public async Task ConnectedRecipients_GetProactiveMessage()
    {
        _notificationService.IsUserConnectedAsync(Arg.Any<string>()).Returns(true);

        await _notifier.NotifyAsync(Email(), Analysis());

        await _notificationService.Received(1).SendProactiveMessageAsync(
            Planner, Arg.Is<string>(m => m.Contains("Krankmeldung")), null, null);
        await _notificationService.Received(1).SendProactiveMessageAsync(
            Admin, Arg.Any<string>(), null, null);
        await _pendingNotes.Received(2).AddAsync(Arg.Any<PendingUserNote>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RecipientReportedConnected_ButLiveSendFails_KeepsTheNotePending()
    {
        _notificationService.IsUserConnectedAsync(Arg.Any<string>()).Returns(true);
        _notificationService.SendProactiveMessageAsync(Planner, Arg.Any<string>(), null, null)
            .Returns<Task>(_ => throw new InvalidOperationException("stale presence, no live connection"));

        await _notifier.NotifyAsync(Email(), Analysis());

        var plannerNotes = _stashedNotes.Where(n => n.UserId == PlannerGuid).ToList();
        plannerNotes.Count.ShouldBe(1);
        plannerNotes[0].Content.ShouldContain("Mitarbeiter meldet sich");
        await _pendingNotes.DidNotReceive().MarkDeliveredAsync(
            Arg.Any<Guid>(), PlannerGuid, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ConnectedRecipient_HasExactlyThatStashedNoteAcknowledged()
    {
        _notificationService.IsUserConnectedAsync(Arg.Any<string>()).Returns(true);

        await _notifier.NotifyAsync(Email(), Analysis());

        var plannerNote = _stashedNotes.Single(n => n.UserId == PlannerGuid);
        plannerNote.Id.ShouldNotBe(Guid.Empty);
        await _pendingNotes.Received(1).MarkDeliveredAsync(
            AgentGuid,
            PlannerGuid,
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(plannerNote.Id)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OfflineRecipient_GetsPendingNote_WithEmailAnalysisTopic()
    {
        _notificationService.IsUserConnectedAsync(Planner).Returns(false);
        _notificationService.IsUserConnectedAsync(Admin).Returns(true);

        await _notifier.NotifyAsync(Email(), Analysis());

        await _pendingNotes.Received(1).AddAsync(
            Arg.Is<PendingUserNote>(n =>
                n.UserId == PlannerGuid &&
                n.Topic == "email-analysis" &&
                n.Content.Contains("Mitarbeiter meldet sich")),
            Arg.Any<CancellationToken>());
        await _notificationService.Received(1).SendProactiveMessageAsync(Admin, Arg.Any<string>(), null, null);
        await _pendingNotes.DidNotReceive().MarkDeliveredAsync(
            Arg.Any<Guid>(), PlannerGuid, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PlannerWhoIsAlsoAdmin_NotifiedOnlyOnce()
    {
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { Planner });
        _notificationService.IsUserConnectedAsync(Arg.Any<string>()).Returns(true);

        await _notifier.NotifyAsync(Email(), Analysis());

        await _notificationService.Received(1).SendProactiveMessageAsync(
            Planner, Arg.Any<string>(), null, null);
    }

    [Test]
    public async Task NoDefaultAgent_OfflineUserSkipped_NoException()
    {
        _notificationService.IsUserConnectedAsync(Arg.Any<string>()).Returns(false);
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns((Agent?)null);

        await _notifier.NotifyAsync(Email(), Analysis());

        await _pendingNotes.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Test]
    public async Task FailureForOneUser_DoesNotAbortOthers()
    {
        _notificationService.IsUserConnectedAsync(Arg.Any<string>()).Returns(true);
        _notificationService.SendProactiveMessageAsync(Planner, Arg.Any<string>(), null, null)
            .Returns<Task>(_ => throw new InvalidOperationException("hub down"));

        await _notifier.NotifyAsync(Email(), Analysis());

        await _notificationService.Received(1).SendProactiveMessageAsync(Admin, Arg.Any<string>(), null, null);
    }

    [Test]
    public async Task PeriodRange_AppearsInMessage()
    {
        _notificationService.IsUserConnectedAsync(Arg.Any<string>()).Returns(true);
        var analysis = Analysis();
        analysis.UntilDate = new DateOnly(2026, 7, 12);

        await _notifier.NotifyAsync(Email(), analysis);

        await _notificationService.Received(1).SendProactiveMessageAsync(
            Planner, Arg.Is<string>(m => m.Contains("2026-07-09") && m.Contains("2026-07-12")), null, null);
    }

    [Test]
    public async Task AvailabilityAnnouncement_LabelAppearsInMessage()
    {
        _notificationService.IsUserConnectedAsync(Arg.Any<string>()).Returns(true);
        var analysis = Analysis();
        analysis.Intent = EmailIntent.AvailabilityAnnouncement;

        await _notifier.NotifyAsync(Email(), analysis);

        await _notificationService.Received(1).SendProactiveMessageAsync(
            Planner, Arg.Is<string>(m => m.Contains("Availability announcement")), null, null);
    }

    [Test]
    public async Task HourWindowAndWeekdays_AppearInMessage()
    {
        _notificationService.IsUserConnectedAsync(Arg.Any<string>()).Returns(true);
        var analysis = Analysis();
        analysis.Intent = EmailIntent.AvailabilityAnnouncement;
        analysis.StartHour = 8;
        analysis.EndHour = 16;
        analysis.Weekdays = "1,2";

        await _notifier.NotifyAsync(Email(), analysis);

        await _notificationService.Received(1).SendProactiveMessageAsync(
            Planner,
            Arg.Is<string>(m => m.Contains("Hours: 8-16") && m.Contains("Weekdays: Mon, Tue")),
            null, null);
    }

    [Test]
    public async Task ShiftPreference_LabelAppearsInMessage()
    {
        _notificationService.IsUserConnectedAsync(Arg.Any<string>()).Returns(true);
        var analysis = Analysis();
        analysis.Intent = EmailIntent.ShiftPreference;

        await _notifier.NotifyAsync(Email(), analysis);

        await _notificationService.Received(1).SendProactiveMessageAsync(
            Planner, Arg.Is<string>(m => m.Contains("Shift preference")), null, null);
    }

    [Test]
    public async Task ScheduleCommands_AppearInMessage()
    {
        _notificationService.IsUserConnectedAsync(Arg.Any<string>()).Returns(true);
        var analysis = Analysis();
        analysis.Intent = EmailIntent.ShiftPreference;
        analysis.ScheduleCommands = "EARLY,-NIGHT";

        await _notifier.NotifyAsync(Email(), analysis);

        await _notificationService.Received(1).SendProactiveMessageAsync(
            Planner, Arg.Is<string>(m => m.Contains("Planning commands: EARLY, -NIGHT")), null, null);
    }
}
