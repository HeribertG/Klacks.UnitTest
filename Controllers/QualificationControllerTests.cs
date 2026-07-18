// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Queries.Qualifications;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.UserBackend.Settings;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class QualificationControllerTests
{
    private IMediator _mediator = null!;
    private QualificationController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _controller = new QualificationController(_mediator, Substitute.For<ILogger<QualificationController>>());
    }

    [Test]
    public async Task GetQualificationList_WithoutActiveIndustriesOnly_SendsUnfilteredAdminListQuery()
    {
        _mediator.Send(Arg.Any<ListQuery>()).Returns(new List<Qualification>());

        await _controller.GetQualificationList();

        await _mediator.Received(1).Send(Arg.Any<ListQuery>());
        await _mediator.DidNotReceive().Send(Arg.Any<SelectionListQuery>());
    }

    [Test]
    public async Task GetQualificationList_WithActiveIndustriesOnly_SendsFilteredSelectionListQuery()
    {
        _mediator.Send(Arg.Any<SelectionListQuery>()).Returns(new List<Qualification>());

        await _controller.GetQualificationList(activeIndustriesOnly: true);

        await _mediator.Received(1).Send(Arg.Any<SelectionListQuery>());
        await _mediator.DidNotReceive().Send(Arg.Any<ListQuery>());
    }
}
