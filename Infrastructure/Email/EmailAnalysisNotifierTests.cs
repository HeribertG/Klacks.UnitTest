// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for EmailAnalysisNotifier — verifies planner/admin audience union, live delivery
/// to connected users, durable PendingUserNote stashing for offline users and that a missing
/// default agent or a per-user failure never aborts the batch.
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

    private static readonly Guid PlannerGuid = Guid.NewGuid();
    private static readonly Guid AdminGuid = Guid.NewGuid();
    private static readonly string Planner = PlannerGuid.ToString();
    private static readonly string Admin = AdminGuid.ToString();

    [SetUp]
    public void SetUp()
    {
        _audienceResolver = Substitute.For<IPlanningAudienceResolver>();
        _notificationService = Substitute.For<IAssistantNotificationService>();
        _pendingNotes = Substitute.For<IPendingUserNoteRepository>();
        _agentRepository = Substitute.For<IAgentRepository>();

        _audienceResolver.GetPlanningUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { Planner });
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { Admin });
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>())
            .Returns(new Agent { Id = Guid.NewGuid(), Name = "Klacksy" });

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
        _notificationService.IsUserConnected(Arg.Any<string>()).Returns(true);

        await _notifier.NotifyAsync(Email(), Analysis());

        await _notificationService.Received(1).SendProactiveMessageAsync(
            Planner, Arg.Is<string>(m => m.Contains("Krankmeldung")), null, null);
        await _notificationService.Received(1).SendProactiveMessageAsync(
            Admin, Arg.Any<string>(), null, null);
        await _pendingNotes.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Test]
    public async Task OfflineRecipient_GetsPendingNote_WithEmailAnalysisTopic()
    {
        _notificationService.IsUserConnected(Planner).Returns(false);
        _notificationService.IsUserConnected(Admin).Returns(true);

        await _notifier.NotifyAsync(Email(), Analysis());

        await _pendingNotes.Received(1).AddAsync(
            Arg.Is<PendingUserNote>(n =>
                n.UserId == PlannerGuid &&
                n.Topic == "email-analysis" &&
                n.Content.Contains("Mitarbeiter meldet sich")),
            Arg.Any<CancellationToken>());
        await _notificationService.Received(1).SendProactiveMessageAsync(Admin, Arg.Any<string>(), null, null);
    }

    [Test]
    public async Task PlannerWhoIsAlsoAdmin_NotifiedOnlyOnce()
    {
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { Planner });
        _notificationService.IsUserConnected(Arg.Any<string>()).Returns(true);

        await _notifier.NotifyAsync(Email(), Analysis());

        await _notificationService.Received(1).SendProactiveMessageAsync(
            Planner, Arg.Any<string>(), null, null);
    }

    [Test]
    public async Task NoDefaultAgent_OfflineUserSkipped_NoException()
    {
        _notificationService.IsUserConnected(Arg.Any<string>()).Returns(false);
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns((Agent?)null);

        await _notifier.NotifyAsync(Email(), Analysis());

        await _pendingNotes.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Test]
    public async Task FailureForOneUser_DoesNotAbortOthers()
    {
        _notificationService.IsUserConnected(Arg.Any<string>()).Returns(true);
        _notificationService.SendProactiveMessageAsync(Planner, Arg.Any<string>(), null, null)
            .Returns<Task>(_ => throw new InvalidOperationException("hub down"));

        await _notifier.NotifyAsync(Email(), Analysis());

        await _notificationService.Received(1).SendProactiveMessageAsync(Admin, Arg.Any<string>(), null, null);
    }

    [Test]
    public async Task PeriodRange_AppearsInMessage()
    {
        _notificationService.IsUserConnected(Arg.Any<string>()).Returns(true);
        var analysis = Analysis();
        analysis.UntilDate = new DateOnly(2026, 7, 12);

        await _notifier.NotifyAsync(Email(), analysis);

        await _notificationService.Received(1).SendProactiveMessageAsync(
            Planner, Arg.Is<string>(m => m.Contains("2026-07-09") && m.Contains("2026-07-12")), null, null);
    }
}
