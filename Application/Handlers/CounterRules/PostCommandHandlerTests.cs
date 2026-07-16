// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the CounterRule PostCommandHandler: field validation (Threshold, HoursThreshold for
/// ShiftExceedingHours, defined enum values) and that new rows always get an empty
/// ImportSourceKey/ImportContentHash regardless of what the caller sends.
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
public class PostCommandHandlerTests
{
    private ICounterRuleRepository _repository = null!;
    private ScheduleMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private PostCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<ICounterRuleRepository>();
        _mapper = new ScheduleMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _handler = new PostCommandHandler(_repository, _mapper, _unitOfWork, Substitute.For<ILogger<PostCommandHandler>>());
    }

    private static CounterRuleResource ValidResource() => new()
    {
        EventType = CounterEventType.NightShift,
        Period = CounterPeriod.Year,
        Threshold = 25,
    };

    [Test]
    public async Task Handle_ValidResource_PersistsAndReturnsResource()
    {
        var result = await _handler.Handle(new PostCommand<CounterRuleResource>(ValidResource()), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Threshold.ShouldBe(25);
        _repository.Received(1).Add(Arg.Any<CounterRule>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_ValidResource_ForcesEmptyImportKeys()
    {
        CounterRule? added = null;
        _repository.When(r => r.Add(Arg.Any<CounterRule>())).Do(ci => added = ci.Arg<CounterRule>());

        await _handler.Handle(new PostCommand<CounterRuleResource>(ValidResource()), CancellationToken.None);

        added.ShouldNotBeNull();
        added!.ImportSourceKey.ShouldBe(string.Empty);
        added.ImportContentHash.ShouldBe(string.Empty);
    }

    [Test]
    public async Task Handle_ThresholdZero_ThrowsInvalidRequest_NoPersist()
    {
        var resource = ValidResource();
        resource.Threshold = 0;

        var ex = await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PostCommand<CounterRuleResource>(resource), CancellationToken.None));

        ex.Message.ShouldBe("Threshold must be greater than zero.");
        _repository.DidNotReceive().Add(Arg.Any<CounterRule>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_ShiftExceedingHours_WithoutHoursThreshold_ThrowsInvalidRequest()
    {
        var resource = ValidResource();
        resource.EventType = CounterEventType.ShiftExceedingHours;
        resource.HoursThreshold = null;

        var ex = await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PostCommand<CounterRuleResource>(resource), CancellationToken.None));

        ex.Message.ShouldBe("HoursThreshold is required and must be greater than zero when EventType is ShiftExceedingHours.");
        _repository.DidNotReceive().Add(Arg.Any<CounterRule>());
    }

    [Test]
    public async Task Handle_ShiftExceedingHours_WithZeroHoursThreshold_ThrowsInvalidRequest()
    {
        var resource = ValidResource();
        resource.EventType = CounterEventType.ShiftExceedingHours;
        resource.HoursThreshold = 0;

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PostCommand<CounterRuleResource>(resource), CancellationToken.None));

        _repository.DidNotReceive().Add(Arg.Any<CounterRule>());
    }

    [Test]
    public async Task Handle_ShiftExceedingHours_WithPositiveHoursThreshold_Persists()
    {
        var resource = ValidResource();
        resource.EventType = CounterEventType.ShiftExceedingHours;
        resource.HoursThreshold = 13m;

        var result = await _handler.Handle(new PostCommand<CounterRuleResource>(resource), CancellationToken.None);

        result.ShouldNotBeNull();
        _repository.Received(1).Add(Arg.Any<CounterRule>());
    }

    [Test]
    public async Task Handle_UndefinedEventType_ThrowsInvalidRequest()
    {
        var resource = ValidResource();
        resource.EventType = (CounterEventType)999;

        var ex = await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PostCommand<CounterRuleResource>(resource), CancellationToken.None));

        ex.Message.ShouldBe("EventType must be a defined counter event type.");
        _repository.DidNotReceive().Add(Arg.Any<CounterRule>());
    }

    [Test]
    public async Task Handle_UndefinedEnforcement_ThrowsInvalidRequest()
    {
        var resource = ValidResource();
        resource.Enforcement = (RuleEnforcementMode)999;

        var ex = await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PostCommand<CounterRuleResource>(resource), CancellationToken.None));

        ex.Message.ShouldBe("Enforcement must be a defined enforcement mode or null.");
        _repository.DidNotReceive().Add(Arg.Any<CounterRule>());
    }

    [Test]
    public async Task Handle_ValidEnforcementOverride_MapsOntoEntity()
    {
        var resource = ValidResource();
        resource.Enforcement = RuleEnforcementMode.Block;
        CounterRule? added = null;
        _repository.When(r => r.Add(Arg.Any<CounterRule>())).Do(ci => added = ci.Arg<CounterRule>());

        await _handler.Handle(new PostCommand<CounterRuleResource>(resource), CancellationToken.None);

        added.ShouldNotBeNull();
        added!.Enforcement.ShouldBe(RuleEnforcementMode.Block);
    }

    [Test]
    public async Task Handle_NullEnforcement_Persists()
    {
        var resource = ValidResource();
        resource.Enforcement = null;
        CounterRule? added = null;
        _repository.When(r => r.Add(Arg.Any<CounterRule>())).Do(ci => added = ci.Arg<CounterRule>());

        await _handler.Handle(new PostCommand<CounterRuleResource>(resource), CancellationToken.None);

        added.ShouldNotBeNull();
        added!.Enforcement.ShouldBeNull();
    }

    [Test]
    public async Task Handle_UndefinedPeriod_ThrowsInvalidRequest()
    {
        var resource = ValidResource();
        resource.Period = (CounterPeriod)999;

        var ex = await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PostCommand<CounterRuleResource>(resource), CancellationToken.None));

        ex.Message.ShouldBe("Period must be a defined counter period.");
        _repository.DidNotReceive().Add(Arg.Any<CounterRule>());
    }

    [Test]
    public async Task Handle_NullResource_ThrowsInvalidRequest()
    {
        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PostCommand<CounterRuleResource>(null!), CancellationToken.None));
    }
}
