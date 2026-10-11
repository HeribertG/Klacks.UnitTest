// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for GeocodingController: Search is a POST (the search term travels in the body, never in the URL and so
/// never in the reverse proxy's access log), forwards query and language to SearchGeocodingQuery, returns 200 OK
/// with the status-bearing resource (also for Disabled), is rate limited with the geo-proxy policy (20 per minute and
/// user) and has no role check.
/// </summary>
namespace Klacks.UnitTest.Controllers.Geocoding;

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Geocoding;
using Klacks.Api.Application.Queries.Geocoding;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Klacks.Api.Presentation.Controllers.UserBackend.Geocoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class GeocodingControllerTests
{
    private IMediator _mediator = null!;
    private GeocodingController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _controller = new GeocodingController(_mediator);
    }

    [Test]
    public async Task Search_ForwardsQueryAndLanguage_AndReturnsOk()
    {
        var expected = new GeocodingSearchResource
        {
            Status = GeocodingSearchOutcome.Found,
            Latitude = 47.37,
            Longitude = 8.54,
            DisplayName = "Zürich, Schweiz",
        };
        _mediator.Send(Arg.Any<SearchGeocodingQuery>(), Arg.Any<CancellationToken>()).Returns(expected);

        var result = await _controller.Search(
            new GeocodingSearchRequest { Query = "Zürich", Language = "de" }, CancellationToken.None);

        var okResult = result.Result.ShouldBeOfType<OkObjectResult>();
        okResult.Value.ShouldBe(expected);
        await _mediator.Received(1).Send(
            Arg.Is<SearchGeocodingQuery>(q => q.Query == "Zürich" && q.Language == "de"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Search_Disabled_IsStillOk()
    {
        _mediator.Send(Arg.Any<SearchGeocodingQuery>(), Arg.Any<CancellationToken>())
            .Returns(new GeocodingSearchResource { Status = GeocodingSearchOutcome.Disabled });

        var result = await _controller.Search(new GeocodingSearchRequest { Query = "Zürich" }, CancellationToken.None);

        var okResult = result.Result.ShouldBeOfType<OkObjectResult>();
        okResult.Value.ShouldBeOfType<GeocodingSearchResource>().Status.ShouldBe(GeocodingSearchOutcome.Disabled);
    }

    [Test]
    public async Task Search_MissingQuery_IsForwardedAsEmptyStringForValidation()
    {
        _mediator.Send(Arg.Any<SearchGeocodingQuery>(), Arg.Any<CancellationToken>())
            .Returns(new GeocodingSearchResource());

        await _controller.Search(new GeocodingSearchRequest(), CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<SearchGeocodingQuery>(q => q.Query == string.Empty && q.Language == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Search_MissingBody_IsForwardedAsEmptyStringForValidation()
    {
        _mediator.Send(Arg.Any<SearchGeocodingQuery>(), Arg.Any<CancellationToken>())
            .Returns(new GeocodingSearchResource());

        await _controller.Search(null, CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<SearchGeocodingQuery>(q => q.Query == string.Empty && q.Language == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public void Search_IsAPostOnTheContractRouteWithTheTermInTheBody()
    {
        var method = typeof(GeocodingController).GetMethod(nameof(GeocodingController.Search))!;

        method.GetCustomAttributes(typeof(HttpGetAttribute), inherit: false).ShouldBeEmpty();
        method.GetCustomAttributes(typeof(HttpPostAttribute), inherit: false)
            .Cast<HttpPostAttribute>()
            .ShouldHaveSingleItem()
            .Template.ShouldBe("Search");
        typeof(BaseController).GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>()
            .ShouldHaveSingleItem()
            .Template.ShouldBe("api/backend/[controller]");

        var bodyParameter = method.GetParameters().Single(parameter => parameter.ParameterType == typeof(GeocodingSearchRequest));
        bodyParameter.GetCustomAttributes(typeof(FromBodyAttribute), inherit: false).ShouldHaveSingleItem();
        method.GetParameters()
            .SelectMany(parameter => parameter.GetCustomAttributes(typeof(FromQueryAttribute), inherit: false))
            .ShouldBeEmpty();
    }

    [Test]
    public void SearchRequest_DeserializesTheCamelCaseContract()
    {
        var request = System.Text.Json.JsonSerializer.Deserialize<GeocodingSearchRequest>(
            "{\"query\":\"Zürich\",\"language\":\"de-CH\"}",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        request.Query.ShouldBe("Zürich");
        request.Language.ShouldBe("de-CH");
    }

    [Test]
    public void GeoProxyPolicy_AllowsTwentyRequestsPerMinuteAndUser()
    {
        RateLimitingPolicies.GeoProxyPermitLimit.ShouldBe(20);
        RateLimitingPolicies.DefaultWindow.ShouldBe(TimeSpan.FromMinutes(1));
    }

    [Test]
    public void Search_IsRateLimitedAndHasNoRoleRestriction()
    {
        typeof(GeocodingController).IsSubclassOf(typeof(BaseController)).ShouldBeTrue();
        typeof(GeocodingController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false).ShouldBeEmpty();

        var method = typeof(GeocodingController).GetMethod(nameof(GeocodingController.Search))!;
        method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false).ShouldBeEmpty();
        method.GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: false)
            .Cast<EnableRateLimitingAttribute>()
            .ShouldHaveSingleItem()
            .PolicyName.ShouldBe(RateLimitingPolicies.GeoProxy);
    }
}
