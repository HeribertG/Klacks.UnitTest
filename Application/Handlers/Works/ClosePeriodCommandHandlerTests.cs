// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClosePeriodCommandHandler: role-based permission flags passed to the lock-level service.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class ClosePeriodCommandHandlerTests
{
    private IWorkRepository _workRepository = null!;
    private IBreakRepository _breakRepository = null!;
    private IWorkLockLevelService _lockLevelService = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private ClosePeriodCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _breakRepository = Substitute.For<IBreakRepository>();
        _lockLevelService = Substitute.For<IWorkLockLevelService>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();

        _handler = new ClosePeriodCommandHandler(
            _workRepository,
            _breakRepository,
            _lockLevelService,
            _httpContextAccessor,
            Substitute.For<ILogger<ClosePeriodCommandHandler>>());
    }

    [Test]
    public async Task Handle_PassesAuthorisedRoleFlagToCanSeal_WhenUserHasAuthorisedRole()
    {
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");
        _lockLevelService.CanSeal(WorkLockLevel.None, WorkLockLevel.Closed, false, true).Returns(true);
        _workRepository.SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), WorkLockLevel.Closed, "authorised-user", Arg.Any<CancellationToken>()).Returns(10);
        _breakRepository.SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), WorkLockLevel.Closed, "authorised-user", Arg.Any<CancellationToken>()).Returns(3);

        var command = new ClosePeriodCommand(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.ShouldBe(13);
        _lockLevelService.Received(1).CanSeal(WorkLockLevel.None, WorkLockLevel.Closed, false, true);
    }

    [Test]
    public async Task Handle_ThrowsInvalidRequest_WhenPermissionDenied()
    {
        WorksTestHelpers.GivenUserIsRegularUser(_httpContextAccessor, "regular-user");
        _lockLevelService.CanSeal(Arg.Any<WorkLockLevel>(), Arg.Any<WorkLockLevel>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(false);

        var command = new ClosePeriodCommand(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        (await Should.ThrowAsync<InvalidRequestException>(act)).Message.ShouldContain("permission");

        await _workRepository.DidNotReceive().SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
