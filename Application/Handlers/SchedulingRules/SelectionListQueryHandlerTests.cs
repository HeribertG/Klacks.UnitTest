// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Handlers.SchedulingRules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.SchedulingRules;
using Klacks.Api.Domain.Models.Scheduling;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.SchedulingRules;

[TestFixture]
public class SelectionListQueryHandlerTests
{
    private ISchedulingRuleRepository _repository = null!;
    private IActiveIndustriesProvider _activeIndustriesProvider = null!;
    private SelectionListQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ISchedulingRuleRepository>();
        _activeIndustriesProvider = Substitute.For<IActiveIndustriesProvider>();
        _handler = new SelectionListQueryHandler(
            _repository,
            _activeIndustriesProvider,
            new ScheduleMapper(),
            Substitute.For<ILogger<SelectionListQueryHandler>>());
    }

    [Test]
    public async Task Handle_NoActiveIndustriesSetting_ReturnsUnfilteredList()
    {
        _activeIndustriesProvider.GetActiveIndustrySlugsAsync().Returns((IReadOnlyCollection<string>?)null);
        _repository.List().Returns(new List<SchedulingRule>
        {
            new() { Id = Guid.NewGuid(), Name = "Manual rule", Industry = string.Empty },
            new() { Id = Guid.NewGuid(), Name = "Security preset", Industry = "security" },
        });

        var result = await _handler.Handle(new SelectionListQuery(), CancellationToken.None);

        result.Count().ShouldBe(2);
        await _repository.DidNotReceiveWithAnyArgs().GetSelectableAsync(default!);
    }

    [Test]
    public async Task Handle_ActiveIndustriesSet_UsesFilteredRepositoryQuery()
    {
        var slugs = new[] { "healthcare" };
        _activeIndustriesProvider.GetActiveIndustrySlugsAsync().Returns(slugs);
        _repository.GetSelectableAsync(slugs).Returns(new List<SchedulingRule>
        {
            new() { Id = Guid.NewGuid(), Name = "Healthcare preset", Industry = "healthcare" },
        });

        var result = await _handler.Handle(new SelectionListQuery(), CancellationToken.None);

        result.Single().Name.ShouldBe("Healthcare preset");
        await _repository.DidNotReceive().List();
    }
}
