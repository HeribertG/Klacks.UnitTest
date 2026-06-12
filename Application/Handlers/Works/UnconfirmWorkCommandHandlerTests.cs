// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for UnconfirmWorkCommandHandler: authorised-role permission resolution against the real lock-level matrix.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class UnconfirmWorkCommandHandlerTests
{
    private IWorkRepository _workRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private UnconfirmWorkCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();

        _handler = new UnconfirmWorkCommandHandler(
            _workRepository,
            _unitOfWork,
            new WorkLockLevelService(),
            new ScheduleMapper(),
            _httpContextAccessor,
            Substitute.For<ILogger<UnconfirmWorkCommandHandler>>());
    }

    [Test]
    public async Task Handle_UnsealsApprovedWork_WhenUserHasAuthorisedRole()
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            LockLevel = WorkLockLevel.Approved,
            SealedAt = DateTime.UtcNow,
            SealedBy = "former-approver"
        };
        _workRepository.Get(work.Id).Returns(work);
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");

        var result = await _handler.Handle(new UnconfirmWorkCommand(work.Id), CancellationToken.None);

        result.ShouldNotBeNull();
        work.LockLevel.ShouldBe(WorkLockLevel.None);
        work.SealedAt.ShouldBeNull();
        work.SealedBy.ShouldBeNull();
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_ThrowsInvalidRequest_WhenRegularUserUnsealsApprovedWork()
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            LockLevel = WorkLockLevel.Approved
        };
        _workRepository.Get(work.Id).Returns(work);
        WorksTestHelpers.GivenUserIsRegularUser(_httpContextAccessor, "regular-user");

        Func<Task> act = async () => await _handler.Handle(new UnconfirmWorkCommand(work.Id), CancellationToken.None);

        (await Should.ThrowAsync<InvalidRequestException>(act)).Message.ShouldContain("unsealed");

        work.LockLevel.ShouldBe(WorkLockLevel.Approved);
    }
}
