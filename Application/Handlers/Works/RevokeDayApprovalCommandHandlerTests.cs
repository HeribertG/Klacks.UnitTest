// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for RevokeDayApprovalCommandHandler: authorised-role permission resolution against the real lock-level matrix.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class RevokeDayApprovalCommandHandlerTests
{
    private IWorkRepository _workRepository = null!;
    private IBreakRepository _breakRepository = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private RevokeDayApprovalCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _breakRepository = Substitute.For<IBreakRepository>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();

        _handler = new RevokeDayApprovalCommandHandler(
            _workRepository,
            _breakRepository,
            new WorkLockLevelService(),
            _httpContextAccessor,
            Substitute.For<ILogger<RevokeDayApprovalCommandHandler>>());
    }

    [Test]
    public async Task Handle_RevokesDayApproval_WhenUserHasAuthorisedRole()
    {
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");
        _workRepository.UnsealByDayAndGroup(Arg.Any<DateOnly>(), Arg.Any<Guid>(), WorkLockLevel.Approved, Arg.Any<CancellationToken>()).Returns(3);
        _breakRepository.UnsealByDayAndGroup(Arg.Any<DateOnly>(), Arg.Any<Guid>(), WorkLockLevel.Approved, Arg.Any<CancellationToken>()).Returns(1);

        var command = new RevokeDayApprovalCommand(new DateOnly(2026, 1, 15), Guid.NewGuid());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.ShouldBe(4);
    }

    [Test]
    public async Task Handle_ThrowsInvalidRequest_WhenUserHasNeitherAdminNorAuthorisedRole()
    {
        WorksTestHelpers.GivenUserIsRegularUser(_httpContextAccessor, "regular-user");

        var command = new RevokeDayApprovalCommand(new DateOnly(2026, 1, 15), Guid.NewGuid());

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        (await Should.ThrowAsync<InvalidRequestException>(act)).Message.ShouldContain("permission");

        await _workRepository.DidNotReceive().UnsealByDayAndGroup(Arg.Any<DateOnly>(), Arg.Any<Guid>(), Arg.Any<WorkLockLevel>(), Arg.Any<CancellationToken>());
    }
}
