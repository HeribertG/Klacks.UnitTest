// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for BlockedSetupOnboardingOnPlaygroundFilter and the onboarding state endpoint it guards: with the mode on, a
/// save of the setup tour (kind "setup", any casing or spacing, or empty) is answered with HTTP 403 (errorCode
/// PLAYGROUND_FEATURE_BLOCKED) and never reaches the action; the intro kind and an unknown kind still reach the action,
/// and with the mode off every kind passes.
/// </summary>

using System.Reflection;
using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Presentation.Controllers.Assistant;
using Klacks.Api.Presentation.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class BlockedSetupOnboardingOnPlaygroundFilterTests
{
    private const string CommandArgumentName = "command";
    private const string SaveActionName = nameof(ChatController.SaveOnboardingState);

    private IPlaygroundMode _mode = null!;
    private BlockedSetupOnboardingOnPlaygroundFilter _sut = null!;
    private ActionContext _actionContext = null!;

    [SetUp]
    public void SetUp()
    {
        _mode = Substitute.For<IPlaygroundMode>();
        _sut = new BlockedSetupOnboardingOnPlaygroundFilter(_mode);
        _actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
    }

    private ActionExecutingContext Executing(string? kind) =>
        new(
            _actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>
            {
                [CommandArgumentName] = new SaveOnboardingStateCommand { Kind = kind, Status = "completed" },
            },
            new object());

    private ActionExecutionDelegate Next(Action onRun) => () =>
    {
        onRun();
        return Task.FromResult(new ActionExecutedContext(_actionContext, new List<IFilterMetadata>(), new object()));
    };

    [Test]
    public void SaveOnboardingState_CarriesTheSetupOnboardingFilter()
    {
        var action = typeof(ChatController).GetMethod(SaveActionName)!;

        var attribute = action.GetCustomAttribute<BlockedSetupOnboardingOnPlaygroundAttribute>();

        attribute.ShouldNotBeNull();
        attribute.ServiceType.ShouldBe(typeof(BlockedSetupOnboardingOnPlaygroundFilter));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase(OnboardingKind.Setup)]
    [TestCase(" SETUP ")]
    public async Task ModeOn_SetupKind_IsAnsweredForbiddenAndNeverRuns(string? kind)
    {
        _mode.IsActive.Returns(true);
        var ran = false;
        var context = Executing(kind);

        await _sut.OnActionExecutionAsync(context, Next(() => ran = true));

        ran.ShouldBeFalse();
        var result = context.Result.ShouldBeOfType<ObjectResult>();
        result.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        var problem = result.Value.ShouldBeOfType<ProblemDetails>();
        problem.Extensions[PlaygroundHardeningConstants.ErrorCodeExtension]
            .ShouldBe(PlaygroundHardeningConstants.FeatureBlockedErrorCode);
    }

    [TestCase(OnboardingKind.Intro)]
    [TestCase(" Intro ")]
    [TestCase("other")]
    public async Task ModeOn_NonSetupKind_Passes(string kind)
    {
        _mode.IsActive.Returns(true);
        var ran = false;
        var context = Executing(kind);

        await _sut.OnActionExecutionAsync(context, Next(() => ran = true));

        ran.ShouldBeTrue();
        context.Result.ShouldBeNull();
    }

    [TestCase(null)]
    [TestCase(OnboardingKind.Setup)]
    [TestCase(OnboardingKind.Intro)]
    public async Task ModeOff_EveryKind_Passes(string? kind)
    {
        _mode.IsActive.Returns(false);
        var ran = false;
        var context = Executing(kind);

        await _sut.OnActionExecutionAsync(context, Next(() => ran = true));

        ran.ShouldBeTrue();
        context.Result.ShouldBeNull();
    }
}
