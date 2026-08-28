// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the admin gate and the HTTP status mapping of the learning review endpoints. The scheme
/// assertion is not decoration: AddIdentity overrides the runtime default to cookie authentication, so a
/// role gate without an explicit JWT scheme answers 401 to every JWT caller instead of enforcing the role.
/// The status mapping is guarded because the frontend card is built against it - a conflict that arrives
/// as 400 would be shown as a validation error rather than as the duplicate it is.
/// </summary>
namespace Klacks.UnitTest.Controllers.Assistant;

using System.Reflection;
using Klacks.Api.Application.Commands.Assistant.Learning;
using Klacks.Api.Application.DTOs.Assistant.Learning;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.Assistant;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class KlacksyLearningControllerAuthorizationTests
{
    private static AuthorizeAttribute ControllerAuthorize =>
        typeof(KlacksyLearningController)
            .GetCustomAttribute<AuthorizeAttribute>(inherit: false)
            .ShouldNotBeNull("the learning review surface must carry its own [Authorize].");

    [Test]
    public void TheController_IsRestrictedToAdministrators()
    {
        ControllerAuthorize.Roles.ShouldBe(Roles.Admin);
    }

    [Test]
    public void TheController_PinsTheJwtScheme()
    {
        ControllerAuthorize.AuthenticationSchemes.ShouldBe(
            JwtBearerDefaults.AuthenticationScheme,
            "AddIdentity overrides the runtime default to cookie authentication, so a bare role gate " +
            "401s every JWT caller.");
    }

    [Test]
    public void EveryAction_InheritsTheGateInsteadOfOverridingIt()
    {
        var overriding = typeof(KlacksyLearningController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttribute<AuthorizeAttribute>(inherit: false) != null
                || method.GetCustomAttribute<AllowAnonymousAttribute>(inherit: false) != null)
            .Select(method => method.Name)
            .ToList();

        overriding.ShouldBeEmpty("no action may weaken or restate the controller-wide admin gate.");
    }

    [Test]
    public async Task AMissingPhrase_Answers404()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteLearnedPhraseCommand>(), Arg.Any<CancellationToken>())
            .Returns(LearningMutationResult.NotFound());

        var result = await new KlacksyLearningController(mediator)
            .DeletePhrase(Guid.NewGuid(), CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Test]
    public async Task ADuplicatePhrase_Answers409()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<UpdateLearnedPhraseCommand>(), Arg.Any<CancellationToken>())
            .Returns(LearningMutationResult.Duplicate());

        var result = await new KlacksyLearningController(mediator)
            .UpdatePhrase(Guid.NewGuid(), new UpdateLearnedPhraseRequest("zeige umsatz", null), CancellationToken.None);

        result.ShouldBeOfType<ConflictObjectResult>();
    }

    [Test]
    public async Task AnInvalidPhrase_Answers400()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<UpdateLearnedPhraseCommand>(), Arg.Any<CancellationToken>())
            .Returns(LearningMutationResult.Invalid("too short"));

        var result = await new KlacksyLearningController(mediator)
            .UpdatePhrase(Guid.NewGuid(), new UpdateLearnedPhraseRequest("ab", null), CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    // The manual trigger is the one endpoint that changes the system without a body, so its admin gate is
    // the only thing between an anonymous request and a run that rewrites the knowledge index.
    [Test]
    public async Task TheManualRun_ReportsWhetherARunWasStarted()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<RunSkillLearningCommand>(), Arg.Any<CancellationToken>())
            .Returns(new SkillLearningRunResponse(false, "A learning run is already in progress."));

        var result = await new KlacksyLearningController(mediator).Run(CancellationToken.None);

        var payload = result.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<SkillLearningRunResponse>();
        payload.Started.ShouldBeFalse();
        payload.Reason.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task ASuccessfulMutation_Answers204()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DismissUnfulfillableWishCommand>(), Arg.Any<CancellationToken>())
            .Returns(LearningMutationResult.Success());

        var result = await new KlacksyLearningController(mediator)
            .DismissUnfulfillable(Guid.NewGuid(), CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
    }
}
