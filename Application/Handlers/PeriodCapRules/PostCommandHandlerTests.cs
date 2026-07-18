// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the PeriodCapRule PostCommandHandler: field validation (two mutually exclusive modes,
/// defined enum values) runs before persistence, and that new rows always get a fresh Id and an empty
/// ImportSourceKey/ImportContentHash regardless of what the caller sends.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Handlers.PeriodCapRules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.PeriodCapRules;

[TestFixture]
public class PostCommandHandlerTests
{
    private IPeriodCapRuleRepository _repository = null!;
    private ScheduleMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private PostCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IPeriodCapRuleRepository>();
        _mapper = new ScheduleMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _handler = new PostCommandHandler(_repository, _mapper, _unitOfWork, Substitute.For<ILogger<PostCommandHandler>>());
    }

    private static PeriodCapRuleResource ValidResource() => new()
    {
        Period = PeriodCapPeriod.Month,
        Scope = PeriodCapScope.TotalHours,
        CapHours = 45m,
    };

    [Test]
    public async Task Handle_ValidResource_PersistsAndReturnsResource()
    {
        var result = await _handler.Handle(new PostCommand<PeriodCapRuleResource>(ValidResource()), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.CapHours.ShouldBe(45m);
        _repository.Received(1).Add(Arg.Any<PeriodCapRule>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_ValidResource_AssignsNewId()
    {
        PeriodCapRule? added = null;
        _repository.When(r => r.Add(Arg.Any<PeriodCapRule>())).Do(ci => added = ci.Arg<PeriodCapRule>());

        await _handler.Handle(new PostCommand<PeriodCapRuleResource>(ValidResource()), CancellationToken.None);

        added.ShouldNotBeNull();
        added!.Id.ShouldNotBe(Guid.Empty);
    }

    [Test]
    public async Task Handle_ValidResource_ForcesEmptyImportKeys()
    {
        PeriodCapRule? added = null;
        _repository.When(r => r.Add(Arg.Any<PeriodCapRule>())).Do(ci => added = ci.Arg<PeriodCapRule>());

        await _handler.Handle(new PostCommand<PeriodCapRuleResource>(ValidResource()), CancellationToken.None);

        added.ShouldNotBeNull();
        added!.ImportSourceKey.ShouldBe(string.Empty);
        added.ImportContentHash.ShouldBe(string.Empty);
    }

    [Test]
    public async Task Handle_ValidRollingResource_Persists()
    {
        var resource = ValidResource();
        resource.CapHours = 0m;
        resource.RollingWindowWeeks = 17;
        resource.MaxAverageWeeklyHours = 48m;

        var result = await _handler.Handle(new PostCommand<PeriodCapRuleResource>(resource), CancellationToken.None);

        result.ShouldNotBeNull();
        _repository.Received(1).Add(Arg.Any<PeriodCapRule>());
    }

    [Test]
    public async Task Handle_NeitherMode_ThrowsInvalidRequest_NoPersist()
    {
        var resource = ValidResource();
        resource.CapHours = 0m;

        var ex = await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PostCommand<PeriodCapRuleResource>(resource), CancellationToken.None));

        ex.Message.ShouldBe("A period cap rule must define either a fixed-period cap (CapHours) or a rolling-average cap (RollingWindowWeeks + MaxAverageWeeklyHours).");
        _repository.DidNotReceive().Add(Arg.Any<PeriodCapRule>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_BothModes_ThrowsInvalidRequest_NoPersist()
    {
        var resource = ValidResource();
        resource.CapHours = 45m;
        resource.RollingWindowWeeks = 17;
        resource.MaxAverageWeeklyHours = 48m;

        var ex = await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PostCommand<PeriodCapRuleResource>(resource), CancellationToken.None));

        ex.Message.ShouldBe("A period cap rule is either fixed-period (CapHours) or rolling-average (RollingWindowWeeks + MaxAverageWeeklyHours), not both.");
        _repository.DidNotReceive().Add(Arg.Any<PeriodCapRule>());
    }

    [Test]
    public async Task Handle_UndefinedPeriod_ThrowsInvalidRequest()
    {
        var resource = ValidResource();
        resource.Period = (PeriodCapPeriod)999;

        var ex = await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PostCommand<PeriodCapRuleResource>(resource), CancellationToken.None));

        ex.Message.ShouldBe("Period must be a defined period cap period.");
        _repository.DidNotReceive().Add(Arg.Any<PeriodCapRule>());
    }

    [Test]
    public async Task Handle_UndefinedScope_ThrowsInvalidRequest()
    {
        var resource = ValidResource();
        resource.Scope = (PeriodCapScope)999;

        var ex = await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PostCommand<PeriodCapRuleResource>(resource), CancellationToken.None));

        ex.Message.ShouldBe("Scope must be a defined period cap scope.");
        _repository.DidNotReceive().Add(Arg.Any<PeriodCapRule>());
    }

    [Test]
    public async Task Handle_CustomWeeksWithinBounds_Persists()
    {
        var resource = ValidResource();
        resource.Period = PeriodCapPeriod.CustomWeeks;
        resource.CustomPeriodWeeks = 17;

        var result = await _handler.Handle(new PostCommand<PeriodCapRuleResource>(resource), CancellationToken.None);

        result.ShouldNotBeNull();
        _repository.Received(1).Add(Arg.Any<PeriodCapRule>());
    }

    [Test]
    public async Task Handle_NullResource_ThrowsInvalidRequest()
    {
        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PostCommand<PeriodCapRuleResource>(null!), CancellationToken.None));
    }
}
