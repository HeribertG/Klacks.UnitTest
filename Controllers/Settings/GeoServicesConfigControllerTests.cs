// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for GeoServicesConfigController: forwards GetGeoServicesConfigQuery through the mediator and returns its
/// result as 200 OK, with no role restriction beyond BaseController's authentication.
/// </summary>
namespace Klacks.UnitTest.Controllers.Settings;

using Klacks.Api.Application.DTOs.Settings.GeoServices;
using Klacks.Api.Application.Queries.Settings.GeoServices;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Klacks.Api.Presentation.Controllers.UserBackend.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class GeoServicesConfigControllerTests
{
    private IMediator _mediator = null!;
    private GeoServicesConfigController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _controller = new GeoServicesConfigController(_mediator);
    }

    [Test]
    public async Task Get_ReturnsMediatorResultAsOk()
    {
        var expected = new GeoServicesConfigResource { NoOutboundTraffic = true };
        _mediator.Send(Arg.Any<GetGeoServicesConfigQuery>(), Arg.Any<CancellationToken>()).Returns(expected);

        var result = await _controller.Get(CancellationToken.None);

        var okResult = result.Result.ShouldBeOfType<OkObjectResult>();
        okResult.Value.ShouldBe(expected);
        await _mediator.Received(1).Send(Arg.Any<GetGeoServicesConfigQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void Controller_InheritsBaseControllerWithoutOwnRoleRestriction()
    {
        typeof(GeoServicesConfigController).IsSubclassOf(typeof(BaseController)).ShouldBeTrue();
        typeof(GeoServicesConfigController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .ShouldBeEmpty();
        typeof(GeoServicesConfigController).GetMethod(nameof(GeoServicesConfigController.Get))!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .ShouldBeEmpty();
    }
}
