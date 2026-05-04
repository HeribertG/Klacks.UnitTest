// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GetClientSortOrderQueryHandler: verifies mapping and empty-list handling.
/// </summary>

using Klacks.Api.Application.DTOs;
using Klacks.Api.Application.Handlers.Schedule;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Schedule;
using Klacks.Api.Domain.Models.Staffs;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Handlers.Schedule;

[TestFixture]
public class GetClientSortOrderQueryHandlerTests
{
    private IClientSortPreferenceRepository _repository = null!;
    private GetClientSortOrderQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IClientSortPreferenceRepository>();
        _handler = new GetClientSortOrderQueryHandler(
            _repository,
            NullLogger<GetClientSortOrderQueryHandler>.Instance);
    }

    [Test]
    public async Task Handle_WhenEntriesExist_ReturnsMappedDtos()
    {
        var userId = "user-abc";
        var groupId = Guid.NewGuid();
        var clientId1 = Guid.NewGuid();
        var clientId2 = Guid.NewGuid();

        _repository.GetByUserAndGroupAsync(userId, groupId, default)
            .Returns(new List<ClientSortPreference>
            {
                new() { UserId = userId, GroupId = groupId, ClientId = clientId1, SortOrder = 0 },
                new() { UserId = userId, GroupId = groupId, ClientId = clientId2, SortOrder = 1 }
            });

        var result = await _handler.Handle(new GetClientSortOrderQuery(userId, groupId), default);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(2);
        result[0].ShouldBeEquivalentTo(new ClientSortOrderDto(clientId1, 0));
        result[1].ShouldBeEquivalentTo(new ClientSortOrderDto(clientId2, 1));
    }

    [Test]
    public async Task Handle_WhenNoEntries_ReturnsEmptyList()
    {
        _repository.GetByUserAndGroupAsync(Arg.Any<string>(), Arg.Any<Guid>(), default)
            .Returns(new List<ClientSortPreference>());

        var result = await _handler.Handle(
            new GetClientSortOrderQuery("user-abc", Guid.NewGuid()), default);

        result.ShouldNotBeNull();
        result.ShouldBeEmpty();
    }
}
