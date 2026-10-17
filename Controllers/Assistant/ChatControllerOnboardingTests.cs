// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Onboarding endpoints of the chat controller: the save takes the account id from the token, never from the body;
/// a refused setup save answers 403 through the pinned JWT scheme (never a cookie redirect, never 401); the "I already
/// know Klacks" endpoint answers 204 when stored, 409 without an agent and 400 without a usable account id.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Interfaces.Klacksy;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.Assistant;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace Klacks.UnitTest.Controllers.Assistant;

[TestFixture]
public class ChatControllerOnboardingTests
{
    private const string CallerId = "11111111-1111-1111-1111-111111111111";
    private const string ForeignId = "22222222-2222-2222-2222-222222222222";

    private IMediator _mediator = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
    }

    [Test]
    public async Task SaveOnboardingState_TakesTheAccountIdFromTheToken()
    {
        SaveOnboardingStateCommand? sent = null;
        _mediator.Send(Arg.Do<SaveOnboardingStateCommand>(c => sent = c), Arg.Any<CancellationToken>())
            .Returns(new OnboardingResource { Kind = OnboardingKind.Intro });

        var result = await Controller().SaveOnboardingState(
            new SaveOnboardingStateCommand { Kind = OnboardingKind.Intro, Status = "offered", UserId = ForeignId },
            CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        sent.ShouldNotBeNull();
        sent!.UserId.ShouldBe(CallerId);
    }

    [Test]
    public async Task SaveOnboardingState_RefusedSetupSave_Answers403WithTheJwtScheme()
    {
        _mediator.Send(Arg.Any<SaveOnboardingStateCommand>(), Arg.Any<CancellationToken>())
            .Returns((OnboardingResource?)null);

        var result = await Controller().SaveOnboardingState(
            new SaveOnboardingStateCommand { Kind = OnboardingKind.Setup, Status = "completed" },
            CancellationToken.None);

        var forbid = result.Result.ShouldBeOfType<ForbidResult>();
        forbid.AuthenticationSchemes.ShouldBe(new[] { JwtBearerDefaults.AuthenticationScheme });
    }

    [TestCase(ExplainDepthOverrideOutcome.Stored, StatusCodes.Status204NoContent)]
    [TestCase(ExplainDepthOverrideOutcome.NoAgent, StatusCodes.Status409Conflict)]
    [TestCase(ExplainDepthOverrideOutcome.NoUser, StatusCodes.Status400BadRequest)]
    public async Task SetAdvancedExplainDepth_MapsTheOutcome(ExplainDepthOverrideOutcome outcome, int statusCode)
    {
        SetAdvancedExplainDepthCommand? sent = null;
        _mediator.Send(Arg.Do<SetAdvancedExplainDepthCommand>(c => sent = c), Arg.Any<CancellationToken>())
            .Returns(outcome);

        var result = await Controller().SetAdvancedExplainDepth(CancellationToken.None);

        result.ShouldBeAssignableTo<IStatusCodeActionResult>()!.StatusCode.ShouldBe(statusCode);
        sent!.UserId.ShouldBe(CallerId);
    }

    private ChatController Controller()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, CallerId) }, "Test"));

        return new ChatController(
            Substitute.For<ILogger<ChatController>>(),
            _mediator,
            Substitute.For<IAgentSkillRepository>(),
            Substitute.For<IAgentRepository>(),
            Substitute.For<ILLMStreamingOrchestrator>(),
            Substitute.For<ISkillCacheService>(),
            Substitute.For<IUtteranceNormalizer>(),
            Substitute.For<INavigationTargetMatcher>(),
            Substitute.For<INavigationTargetCacheService>(),
            Substitute.For<INavigationFeedbackLogger>(),
            Substitute.For<INavigationMissDetector>(),
            Substitute.For<ILLMRepository>(),
            Substitute.For<IUserActivityTracker>(),
            Substitute.For<INavigationEntityRouteGuard>(),
            Substitute.For<IActiveTurnRegistry>(),
            Substitute.For<IInterruptedTurnFinalizer>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };
    }
}
