// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the CounterRule DeleteCommandHandler: soft-delete on success, NotFound on unknown Id.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Handlers.CounterRules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.CounterRules;

[TestFixture]
public class DeleteCommandHandlerTests
{
    private ICounterRuleRepository _repository = null!;
    private ScheduleMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private DeleteCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<ICounterRuleRepository>();
        _mapper = new ScheduleMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _handler = new DeleteCommandHandler(_repository, _mapper, _unitOfWork, Substitute.For<ILogger<DeleteCommandHandler>>());
    }

    [Test]
    public async Task Handle_ExistingRule_DeletesAndReturnsResource()
    {
        var id = Guid.NewGuid();
        var existing = new CounterRule
        {
            Id = id,
            EventType = CounterEventType.NightShift,
            Period = CounterPeriod.Year,
            Threshold = 25,
        };
        _repository.DeleteAsync(id).Returns(existing);

        var result = await _handler.Handle(new DeleteCommand<CounterRuleResource>(id), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Id.ShouldBe(id);
        await _repository.Received(1).DeleteAsync(id);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_UnknownId_ThrowsKeyNotFound_NoCommit()
    {
        var id = Guid.NewGuid();
        _repository.DeleteAsync(id).Returns((CounterRule?)null);

        await Should.ThrowAsync<KeyNotFoundException>(
            () => _handler.Handle(new DeleteCommand<CounterRuleResource>(id), CancellationToken.None));

        await _unitOfWork.DidNotReceive().CompleteAsync();
    }
}
