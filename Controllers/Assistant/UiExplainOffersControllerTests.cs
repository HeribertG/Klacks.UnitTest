// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for UiExplainOffersController and its two handlers (T5): the endpoints are pinned to the JWT scheme and
/// the assistant policy (planner floor, authenticated user required), the user id always comes from the token - the
/// request carries none - a token without a user account gets 400 (never 401, which logs the SPA out), and an unknown
/// topic or outcome gets 400 without touching the service.
/// </summary>

namespace Klacks.UnitTest.Controllers.Assistant;

using System.Reflection;
using System.Security.Claims;
using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.Assistant;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

[TestFixture]
public class UiExplainOffersControllerTests
{
    private const string Topic = UiExplainOfferTopics.ShiftHolidayOptionsKey;

    private static readonly string TokenUserId = Guid.NewGuid().ToString();

    private IUiExplainOfferService _offers = null!;

    [SetUp]
    public void SetUp()
    {
        _offers = Substitute.For<IUiExplainOfferService>();
        _offers.GetStateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new UiExplainOfferState(true, true));
        _offers.RecordReactionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => UiExplainOfferOutcomes.IsKnown(call.ArgAt<string>(2)) && UiExplainOfferTopics.Find(call.ArgAt<string>(1)) != null);
    }

    private UiExplainOffersController Controller(string? nameIdentifier)
    {
        var mediator = Substitute.For<IMediator>();
        var stateHandler = new GetUiExplainOfferStateQueryHandler(_offers);
        var reactionHandler = new RecordUiExplainOfferReactionCommandHandler(_offers);
        mediator.Send(Arg.Any<GetUiExplainOfferStateQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => stateHandler.Handle(call.ArgAt<GetUiExplainOfferStateQuery>(0), CancellationToken.None));
        mediator.Send(Arg.Any<RecordUiExplainOfferReactionCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => reactionHandler.Handle(call.ArgAt<RecordUiExplainOfferReactionCommand>(0), CancellationToken.None));

        var claims = nameIdentifier == null ? Array.Empty<Claim>() : [new Claim(ClaimTypes.NameIdentifier, nameIdentifier)];
        return new UiExplainOffersController(mediator)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")) }
            }
        };
    }

    [Test]
    public void Controller_PinsTheJwtScheme_AndTheAssistantPolicy()
    {
        var authorize = typeof(UiExplainOffersController).GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();

        authorize.Count.ShouldBe(1);
        authorize[0].AuthenticationSchemes.ShouldBe(JwtBearerDefaults.AuthenticationScheme);
        authorize[0].Policy.ShouldBe(AuthorizationPolicies.RequireAssistant);
        typeof(UiExplainOffersController).GetCustomAttribute<AllowAnonymousAttribute>().ShouldBeNull();
    }

    [Test]
    public void Requests_CarryNoUserId()
    {
        typeof(UiExplainOfferReactionRequest).GetProperties().Select(property => property.Name)
            .ShouldBe([nameof(UiExplainOfferReactionRequest.Outcome)]);
    }

    [Test]
    public async Task GetState_UsesTheTokenUser()
    {
        var result = await Controller(TokenUserId).GetState(Topic);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var resource = ok.Value.ShouldBeOfType<UiExplainOfferStateResource>();
        resource.TopicKey.ShouldBe(Topic);
        resource.ShouldOffer.ShouldBeTrue();
        await _offers.Received(1).GetStateAsync(TokenUserId, Topic, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetState_UnknownTopic_IsBadRequest_WithoutServiceCall()
    {
        var result = await Controller(TokenUserId).GetState("sealing");

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
        await _offers.DidNotReceive().GetStateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestCase(null)]
    [TestCase("not-a-guid")]
    public async Task NoUserAccountInToken_IsBadRequest_NotUnauthorized(string? nameIdentifier)
    {
        var controller = Controller(nameIdentifier);

        (await controller.GetState(Topic)).Result.ShouldBeOfType<BadRequestObjectResult>();
        (await controller.RecordReaction(Topic, new UiExplainOfferReactionRequest { Outcome = UiExplainOfferOutcomes.Accepted }))
            .ShouldBeOfType<BadRequestObjectResult>();
        await _offers.DidNotReceive().RecordReactionAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RecordReaction_UsesTheTokenUser_AndAnswersNoContent()
    {
        var result = await Controller(TokenUserId)
            .RecordReaction(Topic, new UiExplainOfferReactionRequest { Outcome = UiExplainOfferOutcomes.Later });

        result.ShouldBeOfType<NoContentResult>();
        await _offers.Received(1).RecordReactionAsync(TokenUserId, Topic, UiExplainOfferOutcomes.Later, Arg.Any<CancellationToken>());
    }

    [TestCase(Topic, "maybe")]
    [TestCase("sealing", UiExplainOfferOutcomes.Accepted)]
    public async Task RecordReaction_UnknownTopicOrOutcome_IsBadRequest(string topic, string outcome)
    {
        var result = await Controller(TokenUserId).RecordReaction(topic, new UiExplainOfferReactionRequest { Outcome = outcome });

        result.ShouldBeOfType<BadRequestObjectResult>();
    }
}
