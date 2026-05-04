// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SaveClientSortOrderCommandHandler: verifies ReplaceAllAsync is called with correct entities and UnitOfWork is saved.
/// </summary>

using Klacks.Api.Application.Commands.Schedule;
using Klacks.Api.Application.DTOs;
using Klacks.Api.Application.Handlers.Schedule;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Staffs;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Handlers.Schedule;

[TestFixture]
public class SaveClientSortOrderCommandHandlerTests
{
    private IClientSortPreferenceRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private SaveClientSortOrderCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IClientSortPreferenceRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _handler = new SaveClientSortOrderCommandHandler(
            _repository,
            _unitOfWork,
            NullLogger<SaveClientSortOrderCommandHandler>.Instance);
    }

    [Test]
    public async Task Handle_CallsReplaceAllWithMappedEntitiesAndSaves()
    {
        var userId = "user-abc";
        var groupId = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        var command = new SaveClientSortOrderCommand(
            userId, groupId,
            new List<ClientSortOrderDto> { new(clientId, 0) });

        var result = await _handler.Handle(command, default);

        result.ShouldBeTrue();

        await _repository.Received(1).ReplaceAllAsync(
            userId,
            groupId,
            Arg.Is<IEnumerable<ClientSortPreference>>(entries =>
                entries.Single().ClientId == clientId &&
                entries.Single().SortOrder == 0 &&
                entries.Single().UserId == userId));

        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_EmptyEntries_StillCallsReplaceAllAndSaves()
    {
        var command = new SaveClientSortOrderCommand(
            "user-abc", Guid.NewGuid(), new List<ClientSortOrderDto>());

        await _handler.Handle(command, default);

        await _repository.Received(1).ReplaceAllAsync(
            Arg.Any<string>(),
            Arg.Any<Guid>(),
            Arg.Is<IEnumerable<ClientSortPreference>>(e => !e.Any()));

        await _unitOfWork.Received(1).CompleteAsync();
    }
}
