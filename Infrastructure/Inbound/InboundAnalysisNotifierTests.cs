// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for InboundAnalysisNotifier — verifies planner/admin audience union, live delivery
/// to connected users, durable PendingUserNote stashing before every send, acknowledgement of
/// exactly that note after a successful live send (no double relay), retention of the note when
/// the send fails despite a positive presence report, that a missing default agent or a
/// per-user failure never aborts the batch, and the channel-neutral message rendering (chat-bubble
/// icon for non-email sources, omitted Subject line when the source carries no subject).
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Inbound;
using Klacks.Api.Infrastructure.Inbound;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Inbound;

[TestFixture]
public class InboundAnalysisNotifierTests
{
    private IPlanningAudienceResolver _audienceResolver = null!;
    private IAssistantNotificationService _notificationService = null!;
    private IPendingUserNoteRepository _pendingNotes = null!;
    private IAgentRepository _agentRepository = null!;
    private InboundAnalysisNotifier _notifier = null!;
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

        _notifier = new InboundAnalysisNotifier(
            _audienceResolver, _notificationService, _pendingNotes, _agentRepository,
            Substitute.For<ILogger<InboundAnalysisNotifier>>());
    }

    private static InboundSource Source() => new(
        Guid.NewGuid(), InboundSourceKind.Email, "Email",
        "Max Muster (worker@example.com)", "Krankmeldung", "body", DateTime.UtcNow);

    private static InboundAnalysis Analysis() => new()
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

        await _notifier.NotifyAsync(Source(), Analysis());

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

        await _notifier.NotifyAsync(Source(), Analysis());

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

        await _notifier.NotifyAsync(Source(), Analysis());

        var plannerNote = _stashedNotes.Single(n => n.UserId == PlannerGuid);
        plannerNote.Id.ShouldNotBe(Guid.Empty);
        await _pendingNotes.Received(1).MarkDeliveredAsync(
            AgentGuid,
            PlannerGuid,
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(plannerNote.Id)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OfflineRecipient_GetsPendingNote_WithInboundAnalysisTopic()
    {
        _notificationService.IsUserConnectedAsync(Planner).Returns(false);
        _notificationService.IsUserConnectedAsync(Admin).Returns(true);

        await _notifier.NotifyAsync(Source(), Analysis());

        await _pendingNotes.Received(1).AddAsync(
            Arg.Is<PendingUserNote>(n =>
                n.UserId == PlannerGuid &&
                n.Topic == "inbound-analysis" &&
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

        await _notifier.NotifyAsync(Source(), Analysis());

        await _notificationService.Received(1).SendProactiveMessageAsync(
            Planner, Arg.Any<string>(), null, null);
    }

    [Test]
    public async Task NoDefaultAgent_OfflineUserSkipped_NoException()
    {
        _notificationService.IsUserConnectedAsync(Arg.Any<string>()).Returns(false);
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns((Agent?)null);

        await _notifier.NotifyAsync(Source(), Analysis());

        await _pendingNotes.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Test]
    public async Task FailureForOneUser_DoesNotAbortOthers()
    {
        _notificationService.IsUserConnectedAsync(Arg.Any<string>()).Returns(true);
        _notificationService.SendProactiveMessageAsync(Planner, Arg.Any<string>(), null, null)
            .Returns<Task>(_ => throw new InvalidOperationException("hub down"));

        await _notifier.NotifyAsync(Source(), Analysis());

        await _notificationService.Received(1).SendProactiveMessageAsync(Admin, Arg.Any<string>(), null, null);
    }

    [Test]
    public async Task PeriodRange_AppearsInMessage()
    {
        _notificationService.IsUserConnectedAsync(Arg.Any<string>()).Returns(true);
        var analysis = Analysis();
        analysis.UntilDate = new DateOnly(2026, 7, 12);

        await _notifier.NotifyAsync(Source(), analysis);

        await _notificationService.Received(1).SendProactiveMessageAsync(
            Planner, Arg.Is<string>(m => m.Contains("2026-07-09") && m.Contains("2026-07-12")), null, null);
    }

    [Test]
    public async Task AvailabilityAnnouncement_LabelAppearsInMessage()
    {
        _notificationService.IsUserConnectedAsync(Arg.Any<string>()).Returns(true);
        var analysis = Analysis();
        analysis.Intent = EmailIntent.AvailabilityAnnouncement;

        await _notifier.NotifyAsync(Source(), analysis);

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

        await _notifier.NotifyAsync(Source(), analysis);

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

        await _notifier.NotifyAsync(Source(), analysis);

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

        await _notifier.NotifyAsync(Source(), analysis);

        await _notificationService.Received(1).SendProactiveMessageAsync(
            Planner, Arg.Is<string>(m => m.Contains("Planning commands: EARLY, -NIGHT")), null, null);
    }

    [Test]
    public async Task MessengerSource_UsesChatBubbleIcon_NotEmailIcon()
    {
        _notificationService.IsUserConnectedAsync(Arg.Any<string>()).Returns(true);
        var source = Source() with { SourceKind = InboundSourceKind.Messenger, Channel = "Messenger:Telegram" };

        await _notifier.NotifyAsync(source, Analysis());

        await _notificationService.Received(1).SendProactiveMessageAsync(
            Planner, Arg.Is<string>(m => m.Contains("💬") && !m.Contains("📧")), null, null);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task BlankSubject_OmitsSubjectLineFromMessage(string? subject)
    {
        _notificationService.IsUserConnectedAsync(Arg.Any<string>()).Returns(true);
        var source = Source() with { Subject = subject };

        await _notifier.NotifyAsync(source, Analysis());

        await _notificationService.Received(1).SendProactiveMessageAsync(
            Planner, Arg.Is<string>(m => !m.Contains("Subject:")), null, null);
    }

    [Test]
    public async Task ClarificationContext_IsAppendedAtTheEnd()
    {
        _notificationService.IsUserConnectedAsync(Arg.Any<string>()).Returns(true);

        await _notifier.NotifyAsync(Source(), Analysis(), clarificationContext: "💬 Answer to Klacksy's question");

        await _notificationService.Received(1).SendProactiveMessageAsync(
            Planner, Arg.Is<string>(m => m.EndsWith("💬 Answer to Klacksy's question")), null, null);
    }

    [Test]
    public async Task NotifyMessageAsync_StashesAndDeliversTheTextToPlannersAndAdmins()
    {
        _notificationService.IsUserConnectedAsync(Planner).Returns(true);
        _notificationService.IsUserConnectedAsync(Admin).Returns(false);

        await _notifier.NotifyMessageAsync("⏰ **Question unanswered** — Anna Muster");

        _stashedNotes.Count.ShouldBe(2);
        _stashedNotes.ShouldAllBe(n => n.Content == "⏰ **Question unanswered** — Anna Muster" && n.Topic == "inbound-analysis");
        await _notificationService.Received(1).SendProactiveMessageAsync(
            Planner, "⏰ **Question unanswered** — Anna Muster", null, null);
        await _notificationService.DidNotReceive().SendProactiveMessageAsync(
            Admin, "⏰ **Question unanswered** — Anna Muster", null, null);
    }

    [Test]
    public async Task NotifyMessageAsync_BlankText_DoesNothing()
    {
        await _notifier.NotifyMessageAsync("   ");

        await _pendingNotes.DidNotReceive().AddAsync(Arg.Any<PendingUserNote>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AssumedDate_PeriodLineIsMarkedAsAssumed()
    {
        _notificationService.IsUserConnectedAsync(Arg.Any<string>()).Returns(true);
        var analysis = Analysis();
        analysis.DateAssumed = true;

        await _notifier.NotifyAsync(Source(), analysis);

        await _notificationService.Received(1).SendProactiveMessageAsync(
            Planner, Arg.Is<string>(m => m.Contains("Period: 2026-07-09 (assumed: received day)")), null, null);
    }

    [Test]
    public async Task StatedDate_PeriodLineCarriesNoAssumedMarker()
    {
        _notificationService.IsUserConnectedAsync(Arg.Any<string>()).Returns(true);

        await _notifier.NotifyAsync(Source(), Analysis());

        await _notificationService.Received(1).SendProactiveMessageAsync(
            Planner, Arg.Is<string>(m => m.Contains("Period: 2026-07-09") && !m.Contains("assumed")), null, null);
    }
}
