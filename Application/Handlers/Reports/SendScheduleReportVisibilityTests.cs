// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Until 2026-10-01 the schedule report could be emailed to any client id, which also revealed the client's
/// address in the response. A client hidden by group visibility must now be answered exactly like a client
/// without an email address: its communications are not read and nothing is sent.
/// </summary>

using Klacks.Api.Application.Commands.Reports;
using Klacks.Api.Application.Handlers.Reports;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Reports;

[TestFixture]
public class SendScheduleReportVisibilityTests
{
    private const string NoEmailMessage = "No email address found for client";

    [Test]
    public async Task Handle_HiddenClient_AnsweredLikeClientWithoutEmail_NothingSent()
    {
        var hiddenClientId = Guid.NewGuid();
        var communicationRepository = Substitute.For<ICommunicationRepository>();
        communicationRepository.GetClient(hiddenClientId).Returns(new List<Communication>
        {
            new() { ClientId = hiddenClientId, Type = CommunicationTypeEnum.PrivateMail, Value = "hidden@example.com" },
        });
        var scheduleEmailService = Substitute.For<IScheduleEmailService>();
        var scheduleChangeTracker = Substitute.For<IScheduleChangeTracker>();
        var handler = new SendScheduleReportCommandHandler(
            communicationRepository,
            TestGroupWriteVisibility.ClientsHidden(hiddenClientId),
            scheduleEmailService,
            scheduleChangeTracker,
            Substitute.For<ILogger<SendScheduleReportCommandHandler>>());

        var result = await handler.Handle(
            new SendScheduleReportCommand(hiddenClientId, "Hidden", "2026-03-01", "2026-03-31", [], "report.pdf"),
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldBe(NoEmailMessage);
        result.ClientEmail.ShouldBeNull();
        await communicationRepository.DidNotReceive().GetClient(Arg.Any<Guid>());
        await scheduleEmailService.DidNotReceiveWithAnyArgs()
            .SendScheduleEmailAsync(default!, default!, default!, default!, default!, default!);
    }
}
