// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the PeriodCapRule PutCommandHandler: field validation, NotFound on unknown Id, and the
/// invariant that ImportSourceKey/ImportContentHash are never modified by an update (the region-setup
/// re-import relies on the stored hash staying stale for a customer-edited row so it detects the edit).
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
public class PutCommandHandlerTests
{
    private IPeriodCapRuleRepository _repository = null!;
    private ScheduleMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private PutCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IPeriodCapRuleRepository>();
        _mapper = new ScheduleMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _handler = new PutCommandHandler(_repository, _mapper, _unitOfWork, Substitute.For<ILogger<PutCommandHandler>>());
    }

    private static PeriodCapRuleResource ValidResource(Guid id) => new()
    {
        Id = id,
        Period = PeriodCapPeriod.Year,
        Scope = PeriodCapScope.OvertimeHours,
        CapHours = 180m,
    };

    [Test]
    public async Task Handle_ExistingRule_UpdatesEditableFields()
    {
        var id = Guid.NewGuid();
        var existing = new PeriodCapRule
        {
            Id = id,
            Period = PeriodCapPeriod.Month,
            Scope = PeriodCapScope.TotalHours,
            CapHours = 45m,
            ImportSourceKey = string.Empty,
            ImportContentHash = string.Empty,
        };
        _repository.GetAsync(id).Returns(existing);

        var result = await _handler.Handle(new PutCommand<PeriodCapRuleResource>(ValidResource(id)), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Period.ShouldBe(PeriodCapPeriod.Year);
        result.Scope.ShouldBe(PeriodCapScope.OvertimeHours);
        result.CapHours.ShouldBe(180m);
        _repository.Received(1).Update(existing);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_ImportSourcedRule_LeavesImportKeysUntouched()
    {
        var id = Guid.NewGuid();
        var existing = new PeriodCapRule
        {
            Id = id,
            Period = PeriodCapPeriod.Month,
            Scope = PeriodCapScope.TotalHours,
            CapHours = 45m,
            ImportSourceKey = "region-setup:periodCap.monthlyOvertime",
            ImportContentHash = "original-hash",
        };
        _repository.GetAsync(id).Returns(existing);

        await _handler.Handle(new PutCommand<PeriodCapRuleResource>(ValidResource(id)), CancellationToken.None);

        existing.ImportSourceKey.ShouldBe("region-setup:periodCap.monthlyOvertime");
        existing.ImportContentHash.ShouldBe("original-hash");
    }

    [Test]
    public async Task Handle_UnknownId_ThrowsKeyNotFound_NoPersist()
    {
        var id = Guid.NewGuid();
        _repository.GetAsync(id).Returns((PeriodCapRule?)null);

        await Should.ThrowAsync<KeyNotFoundException>(
            () => _handler.Handle(new PutCommand<PeriodCapRuleResource>(ValidResource(id)), CancellationToken.None));

        _repository.DidNotReceive().Update(Arg.Any<PeriodCapRule>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_NeitherMode_ThrowsInvalidRequest_NoLookup()
    {
        var id = Guid.NewGuid();
        var resource = ValidResource(id);
        resource.CapHours = 0m;

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PutCommand<PeriodCapRuleResource>(resource), CancellationToken.None));

        await _repository.DidNotReceive().GetAsync(Arg.Any<Guid>());
    }

    [Test]
    public async Task Handle_BothModes_ThrowsInvalidRequest_NoLookup()
    {
        var id = Guid.NewGuid();
        var resource = ValidResource(id);
        resource.RollingWindowWeeks = 17;
        resource.MaxAverageWeeklyHours = 48m;

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PutCommand<PeriodCapRuleResource>(resource), CancellationToken.None));

        _repository.DidNotReceive().Update(Arg.Any<PeriodCapRule>());
    }
}
