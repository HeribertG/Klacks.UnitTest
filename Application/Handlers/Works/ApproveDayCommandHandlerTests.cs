// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ApproveDayCommandHandler: authorised-role permission resolution against the real lock-level matrix.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class ApproveDayCommandHandlerTests
{
    private IWorkRepository _workRepository = null!;
    private IBreakRepository _breakRepository = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private ApproveDayCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _breakRepository = Substitute.For<IBreakRepository>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();

        _handler = new ApproveDayCommandHandler(
            _workRepository,
            _breakRepository,
            new WorkLockLevelService(),
            _httpContextAccessor,
            Substitute.For<ILogger<ApproveDayCommandHandler>>());
    }

    [Test]
    public async Task Handle_ApprovesDay_WhenUserHasAuthorisedRole()
    {
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");
        _workRepository.SealByDayAndGroup(Arg.Any<DateOnly>(), Arg.Any<Guid>(), WorkLockLevel.Approved, "authorised-user", Arg.Any<CancellationToken>()).Returns(4);
        _breakRepository.SealByDayAndGroup(Arg.Any<DateOnly>(), Arg.Any<Guid>(), WorkLockLevel.Approved, "authorised-user", Arg.Any<CancellationToken>()).Returns(2);

        var command = new ApproveDayCommand(new DateOnly(2026, 1, 15), Guid.NewGuid());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.ShouldBe(6);
    }

    [Test]
    public async Task Handle_ThrowsInvalidRequest_WhenUserHasNeitherAdminNorAuthorisedRole()
    {
        WorksTestHelpers.GivenUserIsRegularUser(_httpContextAccessor, "regular-user");

        var command = new ApproveDayCommand(new DateOnly(2026, 1, 15), Guid.NewGuid());

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        (await Should.ThrowAsync<InvalidRequestException>(act)).Message.ShouldContain("permission");

        await _workRepository.DidNotReceive().SealByDayAndGroup(Arg.Any<DateOnly>(), Arg.Any<Guid>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
