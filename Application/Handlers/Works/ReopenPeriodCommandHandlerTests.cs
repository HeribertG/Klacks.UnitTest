// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ReopenPeriodCommandHandler: role-based permission flags passed to the lock-level service.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class ReopenPeriodCommandHandlerTests
{
    private IWorkRepository _workRepository = null!;
    private IBreakRepository _breakRepository = null!;
    private IWorkLockLevelService _lockLevelService = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private ReopenPeriodCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _breakRepository = Substitute.For<IBreakRepository>();
        _lockLevelService = Substitute.For<IWorkLockLevelService>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();

        _handler = new ReopenPeriodCommandHandler(
            _workRepository,
            _breakRepository,
            _lockLevelService,
            _httpContextAccessor,
            Substitute.For<ILogger<ReopenPeriodCommandHandler>>());
    }

    [Test]
    public async Task Handle_PassesAuthorisedRoleFlagToCanUnseal_WhenUserHasAuthorisedRole()
    {
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");
        _lockLevelService.CanUnseal(WorkLockLevel.Closed, false, true).Returns(true);
        _workRepository.UnsealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), WorkLockLevel.Closed, Arg.Any<CancellationToken>()).Returns(7);
        _breakRepository.UnsealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), WorkLockLevel.Closed, Arg.Any<CancellationToken>()).Returns(1);

        var command = new ReopenPeriodCommand(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.ShouldBe(8);
        _lockLevelService.Received(1).CanUnseal(WorkLockLevel.Closed, false, true);
    }

    [Test]
    public async Task Handle_ThrowsInvalidRequest_WhenPermissionDenied()
    {
        WorksTestHelpers.GivenUserIsRegularUser(_httpContextAccessor, "regular-user");
        _lockLevelService.CanUnseal(Arg.Any<WorkLockLevel>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(false);

        var command = new ReopenPeriodCommand(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        (await Should.ThrowAsync<InvalidRequestException>(act)).Message.ShouldContain("permission");

        await _workRepository.DidNotReceive().UnsealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<CancellationToken>());
    }
}
