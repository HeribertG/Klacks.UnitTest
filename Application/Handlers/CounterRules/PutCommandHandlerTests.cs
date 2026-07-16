// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the CounterRule PutCommandHandler: field validation, NotFound on unknown Id, and the
/// invariant that ImportSourceKey/ImportContentHash are never modified by an update (the region-setup
/// re-import relies on the stored hash staying stale for a customer-edited row so it detects the edit).
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Handlers.CounterRules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.CounterRules;

[TestFixture]
public class PutCommandHandlerTests
{
    private ICounterRuleRepository _repository = null!;
    private ScheduleMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private PutCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<ICounterRuleRepository>();
        _mapper = new ScheduleMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _handler = new PutCommandHandler(_repository, _mapper, _unitOfWork, Substitute.For<ILogger<PutCommandHandler>>());
    }

    private static CounterRuleResource ValidResource(Guid id) => new()
    {
        Id = id,
        EventType = CounterEventType.WorkedDayInWeek,
        Period = CounterPeriod.Week,
        Threshold = 6,
    };

    [Test]
    public async Task Handle_ExistingRule_UpdatesEditableFields()
    {
        var id = Guid.NewGuid();
        var existing = new CounterRule
        {
            Id = id,
            EventType = CounterEventType.NightShift,
            Period = CounterPeriod.Year,
            Threshold = 25,
            ImportSourceKey = string.Empty,
            ImportContentHash = string.Empty,
        };
        _repository.GetAsync(id).Returns(existing);

        var result = await _handler.Handle(new PutCommand<CounterRuleResource>(ValidResource(id)), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.EventType.ShouldBe(CounterEventType.WorkedDayInWeek);
        result.Period.ShouldBe(CounterPeriod.Week);
        result.Threshold.ShouldBe(6);
        _repository.Received(1).Update(existing);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_ImportSourcedRule_LeavesImportKeysUntouched()
    {
        var id = Guid.NewGuid();
        var existing = new CounterRule
        {
            Id = id,
            EventType = CounterEventType.NightShift,
            Period = CounterPeriod.Year,
            Threshold = 25,
            ImportSourceKey = "region-setup:counter.nightShift",
            ImportContentHash = "original-hash",
        };
        _repository.GetAsync(id).Returns(existing);

        await _handler.Handle(new PutCommand<CounterRuleResource>(ValidResource(id)), CancellationToken.None);

        existing.ImportSourceKey.ShouldBe("region-setup:counter.nightShift");
        existing.ImportContentHash.ShouldBe("original-hash");
    }

    [Test]
    public async Task Handle_UnknownId_ThrowsKeyNotFound_NoPersist()
    {
        var id = Guid.NewGuid();
        _repository.GetAsync(id).Returns((CounterRule?)null);

        await Should.ThrowAsync<KeyNotFoundException>(
            () => _handler.Handle(new PutCommand<CounterRuleResource>(ValidResource(id)), CancellationToken.None));

        _repository.DidNotReceive().Update(Arg.Any<CounterRule>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_ThresholdZero_ThrowsInvalidRequest_NoLookup()
    {
        var id = Guid.NewGuid();
        var resource = ValidResource(id);
        resource.Threshold = 0;

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PutCommand<CounterRuleResource>(resource), CancellationToken.None));

        await _repository.DidNotReceive().GetAsync(Arg.Any<Guid>());
    }

    [Test]
    public async Task Handle_ShiftExceedingHours_WithoutHoursThreshold_ThrowsInvalidRequest()
    {
        var id = Guid.NewGuid();
        var resource = ValidResource(id);
        resource.EventType = CounterEventType.ShiftExceedingHours;
        resource.HoursThreshold = null;

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PutCommand<CounterRuleResource>(resource), CancellationToken.None));

        _repository.DidNotReceive().Update(Arg.Any<CounterRule>());
    }
}
