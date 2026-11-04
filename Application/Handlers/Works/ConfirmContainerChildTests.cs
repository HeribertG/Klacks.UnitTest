// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A service inside a container is confirmed together with its container, never on its own (otherwise one confirmed child
/// would pin the whole container against an absence cover move). The container itself is confirmed as before.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class ConfirmContainerChildTests
{
    private static readonly DateOnly Day = new(2026, 11, 2);

    private IWorkRepository _workRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ConfirmWorkCommandHandler _handler = null!;
    private Work _container = null!;
    private Work _child = null!;

    [SetUp]
    public void SetUp()
    {
        _container = new Work
        {
            Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), ShiftId = Guid.NewGuid(), CurrentDate = Day,
            StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(16, 0),
        };
        _child = new Work
        {
            Id = Guid.NewGuid(), ClientId = _container.ClientId, ShiftId = Guid.NewGuid(), CurrentDate = Day,
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0), ParentWorkId = _container.Id,
        };
        _workRepository = Substitute.For<IWorkRepository>();
        _workRepository.Get(_container.Id).Returns(_container);
        _workRepository.Get(_child.Id).Returns(_child);
        _unitOfWork = Substitute.For<IUnitOfWork>();
        var http = Substitute.For<IHttpContextAccessor>();
        http.HttpContext.Returns(new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "planner")], "TestAuth")),
        });
        _handler = new ConfirmWorkCommandHandler(
            _workRepository, TestScheduleRowAccess.Over(TestGroupWriteVisibility.AllClientsVisible()), _unitOfWork,
            new WorkLockLevelService(), new ScheduleMapper(), http, Substitute.For<IContainerWorkCascadeService>(),
            Substitute.For<IPeriodAuditLogRepository>(), Substitute.For<IUserService>(),
            new FixedCompanyClock(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero)),
            Substitute.For<ILogger<ConfirmWorkCommandHandler>>());
    }

    [Test]
    public async Task AServiceInsideAContainer_CannotBeConfirmedOnItsOwn()
    {
        var refusal = await Should.ThrowAsync<ContainerChildConfirmRefusedException>(
            () => _handler.Handle(new ConfirmWorkCommand(_child.Id), CancellationToken.None));

        refusal.ErrorCode.ShouldBe(ContainerChildConfirmRefusedException.Code);

        _child.LockLevel.ShouldBe(WorkLockLevel.None);
        await _workRepository.DidNotReceive().Put(Arg.Any<Work>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task TheContainer_IsConfirmed()
    {
        await _handler.Handle(new ConfirmWorkCommand(_container.Id), CancellationToken.None);

        _container.LockLevel.ShouldBe(WorkLockLevel.Confirmed);
    }
}
