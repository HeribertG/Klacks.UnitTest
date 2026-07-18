// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Queries.SchedulingRules;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.UserBackend.Scheduling;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class SchedulingRulesControllerTests
{
    private IMediator _mediator = null!;
    private SchedulingRulesController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _controller = new SchedulingRulesController(_mediator, Substitute.For<ILogger<SchedulingRulesController>>());
    }

    [Test]
    public async Task GetAll_WithoutActiveIndustriesOnly_SendsUnfilteredAdminListQuery()
    {
        _mediator.Send(Arg.Any<ListQuery<SchedulingRuleResource>>())
            .Returns(new List<SchedulingRuleResource>());

        await _controller.GetAll();

        await _mediator.Received(1).Send(Arg.Any<ListQuery<SchedulingRuleResource>>());
        await _mediator.DidNotReceive().Send(Arg.Any<SelectionListQuery>());
    }

    [Test]
    public async Task GetAll_WithActiveIndustriesOnly_SendsFilteredSelectionListQuery()
    {
        _mediator.Send(Arg.Any<SelectionListQuery>())
            .Returns(new List<SchedulingRuleResource>());

        await _controller.GetAll(activeIndustriesOnly: true);

        await _mediator.Received(1).Send(Arg.Any<SelectionListQuery>());
        await _mediator.DidNotReceive().Send(Arg.Any<ListQuery<SchedulingRuleResource>>());
    }
}
