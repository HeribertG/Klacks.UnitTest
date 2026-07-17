// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.Handlers.Accounts;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Handlers.Accounts;

[TestFixture]
public class LogoutCommandHandlerTests
{
    private const string TestUserId = "7e6f0a44-1111-2222-3333-444455556666";
    private const string OtherUserId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    private IRefreshTokenService _refreshTokenService = null!;
    private LogoutCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _refreshTokenService = Substitute.For<IRefreshTokenService>();
        _handler = new LogoutCommandHandler(_refreshTokenService);
    }

    [Test]
    public async Task Handle_RemovesAllRefreshTokensForRequestedUser()
    {
        await _handler.Handle(new LogoutCommand(TestUserId), CancellationToken.None);

        await _refreshTokenService.Received(1).RemoveAllUserRefreshTokensAsync(TestUserId);
    }

    [Test]
    public async Task Handle_DoesNotRemoveTokensForAnyOtherUser()
    {
        await _handler.Handle(new LogoutCommand(TestUserId), CancellationToken.None);

        await _refreshTokenService.DidNotReceive().RemoveAllUserRefreshTokensAsync(OtherUserId);
    }

    [Test]
    public async Task Handle_ReturnsUnitValue()
    {
        var result = await _handler.Handle(new LogoutCommand(TestUserId), CancellationToken.None);

        result.ShouldBe(Unit.Value);
    }
}
