// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the window parameter and the admin gate of the "Skill-Wirksamkeit" endpoint. The range check
/// has to reject before the query is sent, or an unbounded period reaches the aggregation anyway. The
/// scheme assertion is not decoration: AddIdentity overrides the runtime default to cookie
/// authentication, so a role gate without an explicit JWT scheme answers 401 to every JWT caller
/// instead of enforcing the role.
/// </summary>

using System.Reflection;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Application.Services.Assistant.Evaluation;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.Assistant;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Controllers.Assistant;

[TestFixture]
public class EvalControllerSkillEffectivenessTests
{
    private const int AcceptedWindow = 90;
    private const string ActionName = nameof(EvalController.SkillEffectiveness);

    private IMediator _mediator = null!;
    private EvalController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<GetSkillEffectivenessQuery>(), Arg.Any<CancellationToken>())
            .Returns(new SkillEffectivenessResource());

        _controller = new EvalController(
            Substitute.For<IEvalRunnerService>(),
            Substitute.For<IEvalRunRepository>(),
            _mediator,
            Substitute.For<ILogger<EvalController>>());
    }

    // The DidNotReceive assertion is what makes this a guard: without it the test would still pass if
    // the range check ran after the query had already been dispatched.
    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(SkillEffectivenessDefaults.MaxDays + 1)]
    public async Task AWindowOutsideTheAcceptedRange_Answers400AndSendsNothing(int days)
    {
        var result = await _controller.SkillEffectiveness(days, CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
        await _mediator.DidNotReceive().Send(
            Arg.Any<GetSkillEffectivenessQuery>(), Arg.Any<CancellationToken>());
    }

    [TestCase(SkillEffectivenessDefaults.MinDays)]
    [TestCase(SkillEffectivenessDefaults.MaxDays)]
    [TestCase(AcceptedWindow)]
    public async Task AWindowInsideTheAcceptedRange_IsPassedThroughUnchanged(int days)
    {
        var result = await _controller.SkillEffectiveness(days, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        SentQuery().Days.ShouldBe(days);
    }

    [Test]
    public async Task ACallWithoutAWindow_ReportsOverTheDefaultWindow()
    {
        var result = await _controller.SkillEffectiveness(null, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        SentQuery().Days.ShouldBe(SkillEffectivenessDefaults.DefaultDays);
    }

    [Test]
    public void TheController_PinsTheJwtScheme()
    {
        var controllerAuthorize = typeof(EvalController)
            .GetCustomAttribute<AuthorizeAttribute>(inherit: false)
            .ShouldNotBeNull("the evaluation surface must carry its own [Authorize].");

        controllerAuthorize.AuthenticationSchemes.ShouldBe(
            JwtBearerDefaults.AuthenticationScheme,
            "AddIdentity overrides the runtime default to cookie authentication, so a bare gate "
            + "401s every JWT caller.");
    }

    // The scorecard exposes telemetry across all users, so it is one of the actions that narrows the
    // controller-wide gate to administrators. Losing that attribute would open it to every logged-in user.
    [Test]
    public void TheScorecardAction_IsRestrictedToAdministrators()
    {
        var actionAuthorize = typeof(EvalController)
            .GetMethod(ActionName, BindingFlags.Public | BindingFlags.Instance)
            .ShouldNotBeNull()
            .GetCustomAttribute<AuthorizeAttribute>(inherit: false)
            .ShouldNotBeNull($"{ActionName} must carry its own admin gate.");

        actionAuthorize.Roles.ShouldBe(Roles.Admin);
    }

    private GetSkillEffectivenessQuery SentQuery() =>
        _mediator.ReceivedCalls()
            .SelectMany(call => call.GetArguments())
            .OfType<GetSkillEffectivenessQuery>()
            .Single();
}
