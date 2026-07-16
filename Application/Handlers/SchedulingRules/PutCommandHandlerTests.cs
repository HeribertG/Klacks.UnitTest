// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the SchedulingRule PutCommandHandler: a committed update of surcharge-relevant fields
/// dispatches a SchedulingRuleChangedEvent; a rename or planning-only change never dispatches.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Handlers.SchedulingRules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Events;
using Klacks.Api.Domain.Models.Scheduling;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.SchedulingRules;

[TestFixture]
public class PutCommandHandlerTests
{
    private ISchedulingRuleRepository _repository = null!;
    private ScheduleMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IDomainEventDispatcher _eventDispatcher = null!;
    private PutCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<ISchedulingRuleRepository>();
        _mapper = new ScheduleMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _eventDispatcher = Substitute.For<IDomainEventDispatcher>();

        _handler = new PutCommandHandler(
            _repository,
            _mapper,
            _unitOfWork,
            _eventDispatcher,
            Substitute.For<ILogger<PutCommandHandler>>());
    }

    [Test]
    public async Task Handle_NightRateChanged_DispatchesSchedulingRuleChangedEvent()
    {
        var ruleId = Guid.NewGuid();
        _repository.Get(ruleId).Returns(new SchedulingRule { Id = ruleId, Name = "Rule", NightRate = 0.10m });

        var resource = new SchedulingRuleResource { Id = ruleId, Name = "Rule", NightRate = 0.25m };

        await _handler.Handle(new PutCommand<SchedulingRuleResource>(resource), CancellationToken.None);

        await _unitOfWork.Received(1).CompleteAsync();
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Is<IDomainEvent>(e => e is SchedulingRuleChangedEvent && ((SchedulingRuleChangedEvent)e).RuleId == ruleId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_PlanningOnlyChange_DoesNotDispatch()
    {
        var ruleId = Guid.NewGuid();
        _repository.Get(ruleId).Returns(new SchedulingRule { Id = ruleId, Name = "Old", NightRate = 0.10m, MaxWorkDays = 5 });

        var resource = new SchedulingRuleResource { Id = ruleId, Name = "New", NightRate = 0.10m, MaxWorkDays = 6 };

        await _handler.Handle(new PutCommand<SchedulingRuleResource>(resource), CancellationToken.None);

        await _unitOfWork.Received(1).CompleteAsync();
        await _eventDispatcher.DidNotReceive().DispatchAsync(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
    }
}
