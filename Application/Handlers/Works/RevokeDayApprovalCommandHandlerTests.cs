// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for RevokeDayApprovalCommandHandler: authorised-role permission resolution against the real lock-level matrix.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class RevokeDayApprovalCommandHandlerTests
{
    private IWorkRepository _workRepository = null!;
    private IBreakRepository _breakRepository = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private ISealedDayRepository _sealedDayRepository = null!;
    private RevokeDayApprovalCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _breakRepository = Substitute.For<IBreakRepository>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _sealedDayRepository = Substitute.For<ISealedDayRepository>();
        _sealedDayRepository.GetDayApprovalsAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(new List<SealedDay>());

        _handler = new RevokeDayApprovalCommandHandler(
            _workRepository,
            TestGroupWriteVisibility.UnrestrictedGroups(),
            _breakRepository,
            new WorkLockLevelService(),
            _httpContextAccessor,
            _sealedDayRepository,
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

    [Test]
    public async Task Handle_ByTheApprovingGroup_RevokesAndRemovesItsApprovalRow()
    {
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");
        var groupId = Guid.NewGuid();
        var day = new DateOnly(2026, 1, 15);
        _sealedDayRepository.GetDayApprovalsAsync(day, Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay> { new() { Date = day, GroupId = groupId, Level = WorkLockLevel.Approved } });

        await _handler.Handle(new RevokeDayApprovalCommand(day, groupId), CancellationToken.None);

        await _workRepository.Received(1).UnsealByDayAndGroup(day, groupId, WorkLockLevel.Approved, Arg.Any<CancellationToken>());
        await _sealedDayRepository.Received(1).SoftDeleteDayApprovalAsync(day, groupId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ByAnotherGroup_IsRefused_NothingUnsealed()
    {
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");
        var day = new DateOnly(2026, 1, 15);
        _sealedDayRepository.GetDayApprovalsAsync(day, Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay> { new() { Date = day, GroupId = Guid.NewGuid(), Level = WorkLockLevel.Approved } });

        Func<Task> act = async () => await _handler.Handle(new RevokeDayApprovalCommand(day, Guid.NewGuid()), CancellationToken.None);

        (await Should.ThrowAsync<InvalidRequestException>(act)).Message.ShouldContain("another group");
        await _workRepository.DidNotReceiveWithAnyArgs().UnsealByDayAndGroup(default, default, default, default);
        await _breakRepository.DidNotReceiveWithAnyArgs().UnsealByDayAndGroup(default, default, default, default);
        await _sealedDayRepository.DidNotReceiveWithAnyArgs().SoftDeleteDayApprovalAsync(default, default, default!, default);
    }
}
