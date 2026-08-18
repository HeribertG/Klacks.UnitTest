// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ReorderUsersCommandHandler — verifies it forwards to
/// IAccountManagementService.ReorderUsersAsync and returns the result unchanged.
/// </summary>

using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.Handlers.Accounts;
using Klacks.Api.Domain.DTOs;
using Klacks.Api.Domain.Interfaces.Accounts;

namespace Klacks.UnitTest.Handlers.Accounts;

[TestFixture]
public class ReorderUsersCommandHandlerTests
{
    private IAccountManagementService _accountManagementService = null!;
    private ReorderUsersCommandHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _accountManagementService = Substitute.For<IAccountManagementService>();
        _sut = new ReorderUsersCommandHandler(_accountManagementService);
    }

    [Test]
    public async Task Handle_ForwardsOrderedUserIdsToService_AndReturnsResult()
    {
        var orderedUserIds = new List<string> { "user-c", "user-a", "user-b" };
        var expected = new HttpResultResource { Success = true, Messages = "User order updated" };
        _accountManagementService.ReorderUsersAsync(orderedUserIds).Returns(expected);

        var result = await _sut.Handle(new ReorderUsersCommand(orderedUserIds), CancellationToken.None);

        await _accountManagementService.Received(1).ReorderUsersAsync(orderedUserIds);
        result.ShouldBe(expected);
    }
}
