// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ConfirmWorkCommandHandler: role-based permission flags passed to the lock-level
/// service, the audit record that the confirmation level was missing, and the rule that a service can only be
/// confirmed once it has ended (measured on the company clock, night shifts end on the following day).
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class ConfirmWorkCommandHandlerTests
{
    private IWorkRepository _workRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IWorkLockLevelService _lockLevelService = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IContainerWorkCascadeService _cascadeService = null!;
    private IPeriodAuditLogRepository _auditLogRepository = null!;
    private IUserService _userService = null!;
    private FixedCompanyClock _clock = null!;
    private ConfirmWorkCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _lockLevelService = Substitute.For<IWorkLockLevelService>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _cascadeService = Substitute.For<IContainerWorkCascadeService>();
        _auditLogRepository = Substitute.For<IPeriodAuditLogRepository>();
        _userService = Substitute.For<IUserService>();
        _userService.GetDisplayName().Returns("Ada Lovelace");
        _clock = new FixedCompanyClock(new DateTimeOffset(2026, 11, 2, 13, 0, 0, TimeSpan.Zero));

        _handler = new ConfirmWorkCommandHandler(
            _workRepository,
            TestScheduleRowAccess.Over(TestGroupWriteVisibility.AllClientsVisible()),
            _unitOfWork,
            _lockLevelService,
            new ScheduleMapper(),
            _httpContextAccessor,
            _cascadeService,
            _auditLogRepository,
            _userService,
            _clock,
            Substitute.For<ILogger<ConfirmWorkCommandHandler>>());
    }

    [Test]
    public async Task Handle_PassesAuthorisedRoleFlagToSeal_WhenUserHasAuthorisedRole()
    {
        var work = new Work { Id = Guid.NewGuid() };
        _workRepository.Get(work.Id).Returns(work);
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");

        var result = await _handler.Handle(new ConfirmWorkCommand(work.Id), CancellationToken.None);

        result.ShouldNotBeNull();
        _lockLevelService.Received(1).Seal(work, WorkLockLevel.Confirmed, "authorised-user", false, true);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_ThrowsKeyNotFound_WhenWorkDoesNotExist()
    {
        var workId = Guid.NewGuid();
        _workRepository.Get(workId).Returns((Work?)null);
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");

        Func<Task> act = async () => await _handler.Handle(new ConfirmWorkCommand(workId), CancellationToken.None);

        await Should.ThrowAsync<KeyNotFoundException>(act);

        await _auditLogRepository.DidNotReceive().AddAsync(Arg.Any<PeriodAuditLog>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_WritesAuditRecord_ForTheConfirmedDay()
    {
        var date = new DateOnly(2026, 2, 3);
        var work = new Work { Id = Guid.NewGuid(), CurrentDate = date };
        _workRepository.Get(work.Id).Returns(work);
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");

        await _handler.Handle(new ConfirmWorkCommand(work.Id), CancellationToken.None);

        await _auditLogRepository.Received(1).AddAsync(
            Arg.Is<PeriodAuditLog>(log =>
                log.Action == PeriodAuditAction.ConfirmWork &&
                log.StartDate == date &&
                log.EndDate == date &&
                log.AffectedCount == 1 &&
                log.PerformedBy == "authorised-user" &&
                log.PerformedByName == "Ada Lovelace"),
            Arg.Any<CancellationToken>());
    }

    private static Work ServiceOn(DateOnly date, string start, string end, Guid? analyseToken = null) => new()
    {
        Id = Guid.NewGuid(),
        CurrentDate = date,
        StartTime = TimeOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture),
        EndTime = TimeOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture),
        AnalyseToken = analyseToken
    };

    private async Task AssertNothingSealedAsync(Work work)
    {
        _lockLevelService.DidNotReceiveWithAnyArgs().Seal(default!, default, default!, default, default);
        await _workRepository.DidNotReceiveWithAnyArgs().Put(default!);
        await _cascadeService.DidNotReceiveWithAnyArgs().UpdateLockLevelAsync(default, default, default!);
        await _auditLogRepository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_Refuses_WhenTheServiceHasNotEndedYet()
    {
        var work = ServiceOn(new DateOnly(2026, 11, 2), "08:00", "16:00");
        _workRepository.Get(work.Id).Returns(work);
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");

        Func<Task> act = async () => await _handler.Handle(new ConfirmWorkCommand(work.Id), CancellationToken.None);

        (await Should.ThrowAsync<InvalidRequestException>(act)).Message.ShouldContain("ended");
        await AssertNothingSealedAsync(work);
    }

    [Test]
    public async Task Handle_Refuses_AServiceOfTomorrow()
    {
        var work = ServiceOn(new DateOnly(2026, 11, 3), "08:00", "16:00");
        _workRepository.Get(work.Id).Returns(work);
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");

        Func<Task> act = async () => await _handler.Handle(new ConfirmWorkCommand(work.Id), CancellationToken.None);

        await Should.ThrowAsync<InvalidRequestException>(act);
        await AssertNothingSealedAsync(work);
    }

    [Test]
    public async Task Handle_Seals_FromTheEndMinuteOn()
    {
        var work = ServiceOn(new DateOnly(2026, 11, 2), "08:00", "13:00");
        _workRepository.Get(work.Id).Returns(work);
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");

        var result = await _handler.Handle(new ConfirmWorkCommand(work.Id), CancellationToken.None);

        result.ShouldNotBeNull();
        _lockLevelService.Received(1).Seal(work, WorkLockLevel.Confirmed, "authorised-user", false, true);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_Refuses_ANightShiftUntilItsEndOnTheFollowingDay()
    {
        var work = ServiceOn(new DateOnly(2026, 11, 1), "22:00", "14:00");
        _workRepository.Get(work.Id).Returns(work);
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");

        Func<Task> act = async () => await _handler.Handle(new ConfirmWorkCommand(work.Id), CancellationToken.None);

        await Should.ThrowAsync<InvalidRequestException>(act);
        await AssertNothingSealedAsync(work);

        _clock.Now = new DateTimeOffset(2026, 11, 2, 14, 0, 0, TimeSpan.Zero);
        await _handler.Handle(new ConfirmWorkCommand(work.Id), CancellationToken.None);
        _lockLevelService.Received(1).Seal(work, WorkLockLevel.Confirmed, "authorised-user", false, true);
    }

    [Test]
    public async Task Handle_MeasuresTheEndOnTheCompanyClock_NotOnUtc()
    {
        var work = ServiceOn(new DateOnly(2026, 11, 2), "08:00", "16:00");
        _workRepository.Get(work.Id).Returns(work);
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");
        _clock.Now = new DateTimeOffset(2026, 11, 2, 7, 30, 0, TimeSpan.Zero);

        Func<Task> refusedInUtc = async () => await _handler.Handle(new ConfirmWorkCommand(work.Id), CancellationToken.None);
        await Should.ThrowAsync<InvalidRequestException>(refusedInUtc);

        _clock.TimeZone = TimeZoneInfo.CreateCustomTimeZone("test-plus-nine", TimeSpan.FromHours(9), "test", "test");
        await _handler.Handle(new ConfirmWorkCommand(work.Id), CancellationToken.None);

        _lockLevelService.Received(1).Seal(work, WorkLockLevel.Confirmed, "authorised-user", false, true);
    }

    [Test]
    public async Task Handle_AppliesTheRuleToAScenarioWorkAsWell()
    {
        var work = ServiceOn(new DateOnly(2026, 11, 3), "08:00", "16:00", Guid.NewGuid());
        _workRepository.Get(work.Id).Returns(work);
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");

        Func<Task> act = async () => await _handler.Handle(new ConfirmWorkCommand(work.Id), CancellationToken.None);

        await Should.ThrowAsync<InvalidRequestException>(act);
        await AssertNothingSealedAsync(work);
    }
}
