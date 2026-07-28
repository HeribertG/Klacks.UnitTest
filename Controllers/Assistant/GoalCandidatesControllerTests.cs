// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GoalCandidatesController — covers the Phase 2 read-only inbox contract: both
/// endpoints return 403 for a caller IPlanningAudienceResolver does not report as a planner, the list
/// endpoint defaults the status filter to Proposed (never null) when the query string omits it, the
/// decision endpoint returns 400 for a decision value other than approved/rejected, 404 when the
/// handler reports the candidate was not found/not owned/already decided, and the caller's role
/// claims are expanded into permissions and forwarded on the decision command.
/// </summary>

namespace Klacks.UnitTest.Controllers.Assistant;

using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

[TestFixture]
public class GoalCandidatesControllerTests
{
    private const string PlannerUserId = "planner-user";
    private const string NonPlannerUserId = "non-planner-user";
    private static readonly Guid CandidateId = Guid.NewGuid();

    private IMediator _mediator = null!;
    private IPlanningAudienceResolver _planningAudienceResolver = null!;
    private GoalCandidatesController _controller = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        _planningAudienceResolver = Substitute.For<IPlanningAudienceResolver>();
        _planningAudienceResolver.GetPlanningUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PlannerUserId });

        _controller = new GoalCandidatesController(_mediator, _planningAudienceResolver);
        SetUser(PlannerUserId);
    }

    private void SetUser(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };
    }

    [Test]
    public async Task GetCandidates_CallerIsNotPlanner_ReturnsForbidden()
    {
        SetUser(NonPlannerUserId);

        var result = await _controller.GetCandidates(null, null, CancellationToken.None);

        result.Result.ShouldBeOfType<ForbidResult>();
        await _mediator.DidNotReceive().Send(Arg.Any<GetGoalCandidatesQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetDecision_CallerIsNotPlanner_ReturnsForbidden()
    {
        SetUser(NonPlannerUserId);

        var result = await _controller.SetDecision(
            CandidateId, new SetGoalCandidateDecisionRequest { Decision = GoalCandidateStatus.Approved }, CancellationToken.None);

        result.ShouldBeOfType<ForbidResult>();
        await _mediator.DidNotReceive().Send(Arg.Any<SetGoalCandidateDecisionCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetCandidates_NoStatusGiven_SendsProposedNotNullToQuery()
    {
        _mediator.Send(Arg.Any<GetGoalCandidatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<GoalCandidateDto>());

        await _controller.GetCandidates(null, null, CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<GetGoalCandidatesQuery>(q => q.Status == GoalCandidateStatus.Proposed && q.UserId == PlannerUserId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetDecision_InvalidDecisionValue_ReturnsBadRequestWithoutCallingMediator()
    {
        var result = await _controller.SetDecision(
            CandidateId, new SetGoalCandidateDecisionRequest { Decision = "maybe" }, CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
        await _mediator.DidNotReceive().Send(Arg.Any<SetGoalCandidateDecisionCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetDecision_HandlerReturnsFalse_ReturnsNotFound()
    {
        _mediator.Send(Arg.Any<SetGoalCandidateDecisionCommand>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _controller.SetDecision(
            CandidateId, new SetGoalCandidateDecisionRequest { Decision = GoalCandidateStatus.Approved }, CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Test]
    public async Task SetDecision_HandlerReturnsTrue_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<SetGoalCandidateDecisionCommand>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await _controller.SetDecision(
            CandidateId, new SetGoalCandidateDecisionRequest { Decision = GoalCandidateStatus.Rejected }, CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
    }

    [Test]
    public async Task SetDecision_RoleClaimsArePresent_ExpandedPermissionsAreForwardedOnCommand()
    {
        SetUser(PlannerUserId, Roles.Admin);
        _mediator.Send(Arg.Any<SetGoalCandidateDecisionCommand>(), Arg.Any<CancellationToken>()).Returns(true);

        await _controller.SetDecision(
            CandidateId, new SetGoalCandidateDecisionRequest { Decision = GoalCandidateStatus.Approved }, CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<SetGoalCandidateDecisionCommand>(c =>
                c.Id == CandidateId &&
                c.UserId == PlannerUserId &&
                c.Decision == GoalCandidateStatus.Approved &&
                c.UserPermissions.Contains(Roles.Admin) &&
                c.UserPermissions.Contains(Permissions.CanEditClients)),
            Arg.Any<CancellationToken>());
    }
}
