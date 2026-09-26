// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins that the Seal endpoint never forwards an ActingAdminUserId, whatever the bound command carries.
/// The serializer already ignores the property; this is the second, serializer-independent layer, so a
/// future switch of the JSON stack or a custom model binder cannot open a way to hand the handler a
/// borrowed admin identity.
/// </summary>

using Klacks.Api.Application.Commands.PeriodClosing;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.UserBackend.PeriodClosing;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class PeriodClosingControllerSealTests
{
    [Test]
    public async Task Seal_CommandCarryingAnActingAdmin_IsSentWithoutIt()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ClosePeriodByGroupCommand>(), Arg.Any<CancellationToken>()).Returns(3);
        var controller = new PeriodClosingController(mediator);
        var groupId = Guid.NewGuid();
        var command = new ClosePeriodByGroupCommand(
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 31),
            groupId,
            "close",
            ActingAdminUserId: Guid.NewGuid());

        await controller.Seal(command);

        await mediator.Received(1).Send(
            Arg.Is<ClosePeriodByGroupCommand>(sent =>
                sent.ActingAdminUserId == null
                && sent.GroupId == groupId
                && sent.StartDate == command.StartDate
                && sent.EndDate == command.EndDate),
            Arg.Any<CancellationToken>());
    }
}
