// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the RestrictedTimeWindowRule DeleteCommandHandler: soft-delete on success, NotFound on
/// unknown Id.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Handlers.RestrictedTimeWindowRules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.RestrictedTimeWindowRules;

[TestFixture]
public class DeleteCommandHandlerTests
{
    private IRestrictedTimeWindowRuleRepository _repository = null!;
    private ScheduleMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private DeleteCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IRestrictedTimeWindowRuleRepository>();
        _mapper = new ScheduleMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _handler = new DeleteCommandHandler(_repository, _mapper, _unitOfWork, Substitute.For<ILogger<DeleteCommandHandler>>());
    }

    [Test]
    public async Task Handle_ExistingRule_DeletesAndReturnsResource()
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
        };
        _repository.DeleteAsync(id).Returns(existing);

        var result = await _handler.Handle(new DeleteCommand<RestrictedTimeWindowRuleResource>(id), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Id.ShouldBe(id);
        await _repository.Received(1).DeleteAsync(id);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_UnknownId_ThrowsKeyNotFound_NoCommit()
    {
        var id = Guid.NewGuid();
        _repository.DeleteAsync(id).Returns((RestrictedTimeWindowRule?)null);

        await Should.ThrowAsync<KeyNotFoundException>(
            () => _handler.Handle(new DeleteCommand<RestrictedTimeWindowRuleResource>(id), CancellationToken.None));

        await _unitOfWork.DidNotReceive().CompleteAsync();
    }
}
