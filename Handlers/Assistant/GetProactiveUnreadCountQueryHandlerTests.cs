// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GetProactiveUnreadCountQueryHandler — verifies the unread count is read for
/// the requesting user.
/// </summary>

using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Queries.Assistant;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class GetProactiveUnreadCountQueryHandlerTests
{
    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private GetProactiveUnreadCountQueryHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _sut = new GetProactiveUnreadCountQueryHandler(_dispatchRepository);
    }

    [Test]
    public async Task Handle_ReturnsUnreadCountForRequestingUser()
    {
        _dispatchRepository.CountUnreadAsync("user-a", Arg.Any<CancellationToken>()).Returns(7);

        var result = await _sut.Handle(new GetProactiveUnreadCountQuery { UserId = "user-a" }, CancellationToken.None);

        Assert.That(result, Is.EqualTo(7));
    }
}
