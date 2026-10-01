// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// GET api/backend/Groups/tree feeds every group picker. Owner decision 2026-10-01: a non-admin is offered only
/// the groups they can see, because a write into any other group is refused. The scoping itself lives in
/// GetGroupTreeQueryHandler; this pins that the endpoint asks for it.
/// </summary>

using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Queries.Groups;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.UserBackend.Associations;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Controllers.Groups;

[TestFixture]
public class GroupsControllerTreeVisibilityTests
{
    [Test]
    public async Task GetTree_AsksForTheCallersVisibilityScope()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetGroupTreeQuery>(), Arg.Any<CancellationToken>()).Returns(new GroupTreeResource());
        var controller = new GroupsController(mediator, Substitute.For<ILogger<GroupsController>>());
        var rootId = Guid.NewGuid();

        await controller.GetTree(rootId);

        await mediator.Received(1).Send(
            Arg.Is<GetGroupTreeQuery>(q => q.ApplyVisibilityScope && q.RootId == rootId),
            Arg.Any<CancellationToken>());
    }
}
