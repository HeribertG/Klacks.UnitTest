// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Regression tests for the agent-session IDOR at the controller boundary: both session endpoints must
/// stamp the calling user's NameIdentifier claim onto the dispatched query, so the repository can scope
/// the read to the owner. Without this the session id alone would decide what is returned.
/// </summary>

using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Klacks.UnitTest.Controllers.Assistant;

[TestFixture]
public class AgentsControllerSessionOwnershipTests
{
    private const string CurrentUserId = "22222222-2222-2222-2222-222222222222";

    private IMediator _mediator = null!;
    private AgentsController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<GetAgentSessionMessagesQuery>()).Returns(new List<object>());
        _mediator.Send(Arg.Any<GetAgentSessionsQuery>()).Returns(new List<object>());

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, CurrentUserId) }, "Test");

        _controller = new AgentsController(_mediator)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            }
        };
    }

    [Test]
    public async Task GetSession_PassesTheCallersUserIdToTheQuery()
    {
        var result = await _controller.GetSession(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        await _mediator.Received(1).Send(
            Arg.Is<GetAgentSessionMessagesQuery>(q => q.UserId == CurrentUserId));
    }

    [Test]
    public async Task GetSessions_PassesTheCallersUserIdToTheQuery()
    {
        var result = await _controller.GetSessions(Guid.NewGuid(), CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        await _mediator.Received(1).Send(
            Arg.Is<GetAgentSessionsQuery>(q => q.UserId == CurrentUserId));
    }
}
