// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the RestrictedTimeWindowRule PutCommandHandler: field validation, NotFound on unknown
/// Id, and the invariant that ImportSourceKey/ImportContentHash are never modified by an update (the
/// region-setup re-import relies on the stored hash staying stale for a customer-edited row so it detects
/// the edit).
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Handlers.RestrictedTimeWindowRules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.RestrictedTimeWindowRules;

[TestFixture]
public class PutCommandHandlerTests
{
    private IRestrictedTimeWindowRuleRepository _repository = null!;
    private ScheduleMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private PutCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IRestrictedTimeWindowRuleRepository>();
        _mapper = new ScheduleMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _handler = new PutCommandHandler(_repository, _mapper, _unitOfWork, Substitute.For<ILogger<PutCommandHandler>>());
    }

    private static RestrictedTimeWindowRuleResource ValidResource(Guid id) => new()
    {
        Id = id,
        SeasonFromMonth = 11,
        SeasonFromDay = 1,
        SeasonToMonth = 2,
        SeasonToDay = 15,
        DailyStart = new TimeOnly(22, 0),
        DailyEnd = new TimeOnly(6, 0),
        AppliesToGroupTag = "night",
    };

    [Test]
    public async Task Handle_ExistingRule_UpdatesEditableFields()
    {
        var id = Guid.NewGuid();
        var existing = new RestrictedTimeWindowRule
        {
            Id = id,
            SeasonFromMonth = 6,
            SeasonFromDay = 15,
            SeasonToMonth = 9,
            SeasonToDay = 15,
            DailyStart = new TimeOnly(12, 30),
            DailyEnd = new TimeOnly(15, 0),
            AppliesToGroupTag = "outdoor",
            ImportSourceKey = string.Empty,
            ImportContentHash = string.Empty,
        };
        _repository.GetAsync(id).Returns(existing);

        var result = await _handler.Handle(new PutCommand<RestrictedTimeWindowRuleResource>(ValidResource(id)), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.SeasonFromMonth.ShouldBe(11);
        result.SeasonToMonth.ShouldBe(2);
        result.DailyStart.ShouldBe(new TimeOnly(22, 0));
        result.DailyEnd.ShouldBe(new TimeOnly(6, 0));
        result.AppliesToGroupTag.ShouldBe("night");
        _repository.Received(1).Update(existing);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_ImportSourcedRule_LeavesImportKeysUntouched()
    {
        var id = Guid.NewGuid();
        var existing = new RestrictedTimeWindowRule
        {
            Id = id,
            SeasonFromMonth = 6,
            SeasonFromDay = 15,
            SeasonToMonth = 9,
            SeasonToDay = 15,
            DailyStart = new TimeOnly(12, 30),
            DailyEnd = new TimeOnly(15, 0),
            AppliesToGroupTag = "outdoor",
            ImportSourceKey = "region-setup:restrictedWindow.middayBan",
            ImportContentHash = "original-hash",
        };
        _repository.GetAsync(id).Returns(existing);

        await _handler.Handle(new PutCommand<RestrictedTimeWindowRuleResource>(ValidResource(id)), CancellationToken.None);

        existing.ImportSourceKey.ShouldBe("region-setup:restrictedWindow.middayBan");
        existing.ImportContentHash.ShouldBe("original-hash");
    }

    [Test]
    public async Task Handle_UnknownId_ThrowsKeyNotFound_NoPersist()
    {
        var id = Guid.NewGuid();
        _repository.GetAsync(id).Returns((RestrictedTimeWindowRule?)null);

        await Should.ThrowAsync<KeyNotFoundException>(
            () => _handler.Handle(new PutCommand<RestrictedTimeWindowRuleResource>(ValidResource(id)), CancellationToken.None));

        _repository.DidNotReceive().Update(Arg.Any<RestrictedTimeWindowRule>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_InvalidDay_ThrowsInvalidRequest_NoLookup()
    {
        var id = Guid.NewGuid();
        var resource = ValidResource(id);
        resource.SeasonToDay = 32;

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PutCommand<RestrictedTimeWindowRuleResource>(resource), CancellationToken.None));

        await _repository.DidNotReceive().GetAsync(Arg.Any<Guid>());
    }

    [Test]
    public async Task Handle_EmptyDailyWindow_ThrowsInvalidRequest_NoLookup()
    {
        var id = Guid.NewGuid();
        var resource = ValidResource(id);
        resource.DailyEnd = resource.DailyStart;

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PutCommand<RestrictedTimeWindowRuleResource>(resource), CancellationToken.None));

        _repository.DidNotReceive().Update(Arg.Any<RestrictedTimeWindowRule>());
    }
}
