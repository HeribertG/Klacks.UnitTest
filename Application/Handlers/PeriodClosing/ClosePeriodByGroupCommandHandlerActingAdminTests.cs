// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the background identity of ClosePeriodByGroupCommandHandler: ActingAdminUserId may only
/// stand in for a principal when there is no HttpContext at all and the id still belongs to an admin. Every
/// request - admin, non-admin or anonymous - keeps its own identity and rights, so the field can never be
/// used to borrow an admin's permission. CanSeal is stubbed to answer exactly the isAdmin flag it is
/// given, so a passing test proves the handler derived "admin" from the right source.
/// </summary>

using System.Security.Claims;
using System.Text.Json;
using Klacks.Api.Application.Commands.PeriodClosing;
using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.PeriodClosing;
using Klacks.Api.Application.Interfaces.PeriodClosing;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Events;
using Klacks.Api.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.PeriodClosing;

[TestFixture]
public class ClosePeriodByGroupCommandHandlerActingAdminTests
{
    private static readonly Guid GroupId = Guid.Parse("7a1d0000-0000-0000-0000-00000000000a");
    private static readonly Guid ActingAdminId = Guid.Parse("3f1c9a52-0000-0000-0000-000000000001");
    private static readonly DateOnly Start = new(2026, 8, 1);
    private static readonly DateOnly End = new(2026, 8, 31);
    private const string Reason = "Automatic close";
    private const string RequestUserId = "request-admin";

    private IWorkRepository _workRepository = null!;
    private IBreakRepository _breakRepository = null!;
    private IWorkLockLevelService _lockLevelService = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IPeriodAuditLogRepository _auditLogRepository = null!;
    private ISealedDayRepository _sealedDayRepository = null!;
    private IDomainEventDispatcher _eventDispatcher = null!;
    private IPeriodValidationLoader _validationLoader = null!;
    private IUserService _userService = null!;
    private IPlanningAudienceResolver _audienceResolver = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ClosePeriodByGroupCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _breakRepository = Substitute.For<IBreakRepository>();
        _lockLevelService = Substitute.For<IWorkLockLevelService>();
        _lockLevelService.CanSeal(Arg.Any<WorkLockLevel>(), Arg.Any<WorkLockLevel>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(call => call.ArgAt<bool>(2));
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _httpContextAccessor.HttpContext.Returns((HttpContext?)null);
        _auditLogRepository = Substitute.For<IPeriodAuditLogRepository>();
        _sealedDayRepository = Substitute.For<ISealedDayRepository>();
        _sealedDayRepository.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay>());
        _eventDispatcher = Substitute.For<IDomainEventDispatcher>();
        _validationLoader = Substitute.For<IPeriodValidationLoader>();
        GivenIssues();
        _userService = Substitute.For<IUserService>();
        _userService.GetDisplayName().Returns(AuditActorDefaults.UnknownActor);
        _audienceResolver = Substitute.For<IPlanningAudienceResolver>();
        GivenAdmins(ActingAdminId.ToString());
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>())
            .Returns(call => call.ArgAt<Func<Task<int>>>(0)());

        _handler = new ClosePeriodByGroupCommandHandler(
            _workRepository,
            _breakRepository,
            _lockLevelService,
            _httpContextAccessor,
            _auditLogRepository,
            _sealedDayRepository,
            _eventDispatcher,
            _validationLoader,
            Substitute.For<IComplianceEscalationService>(),
            _userService,
            _audienceResolver,
            _unitOfWork,
            Substitute.For<ILogger<ClosePeriodByGroupCommandHandler>>());
    }

    private void GivenAdmins(params string[] adminIds)
    {
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(adminIds.ToHashSet());
    }

    private void GivenIssues(params ScheduleValidationType[] severities)
    {
        _validationLoader.LoadAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(),
                Arg.Any<Guid?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(severities.Select(severity => new PeriodIssueDto { Severity = severity, Code = "Rest" }).ToList());
    }

    private void GivenRequestPrincipal(params Claim[] claims)
    {
        var httpContext = new DefaultHttpContext
        {
            User = claims.Length == 0
                ? new ClaimsPrincipal(new ClaimsIdentity())
                : new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
        };
        _httpContextAccessor.HttpContext.Returns(httpContext);
    }

    private static ClosePeriodByGroupCommand Command(Guid? actingAdminUserId) =>
        new(Start, End, GroupId, Reason, ActingAdminUserId: actingAdminUserId);

    private async Task AssertNothingSealedAsync()
    {
        await _workRepository.DidNotReceiveWithAnyArgs().SealByPeriodAndGroup(
            default, default, default, default, default!, default);
        await _sealedDayRepository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _auditLogRepository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _eventDispatcher.DidNotReceiveWithAnyArgs().DispatchAsync(default(IDomainEvent)!, default);
    }

    [Test]
    public async Task Handle_NoHttpContextAndNoActingAdmin_IsRefused()
    {
        var ex = await Should.ThrowAsync<InvalidRequestException>(() => _handler.Handle(Command(null), CancellationToken.None));

        ex.Message.ShouldContain("permission");
        await AssertNothingSealedAsync();
    }

    [Test]
    public async Task Handle_NoHttpContextWithActingAdmin_SealsUnderThatAdminAndNamesKlacksyAsActor()
    {
        _workRepository.SealByPeriodAndGroup(Start, End, GroupId, WorkLockLevel.Closed, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(4);

        var affected = await _handler.Handle(Command(ActingAdminId), CancellationToken.None);

        affected.ShouldBe(4 + 31);
        var expectedActor = ActingAdminId.ToString();
        await _workRepository.Received(1).SealByPeriodAndGroup(
            Start, End, GroupId, WorkLockLevel.Closed, expectedActor, Arg.Any<CancellationToken>());
        await _sealedDayRepository.Received(31).AddAsync(
            Arg.Is<SealedDay>(day => day.SealedBy == expectedActor && day.GroupId == GroupId && day.Reason == Reason),
            Arg.Any<CancellationToken>());
        await _auditLogRepository.Received(1).AddAsync(
            Arg.Is<PeriodAuditLog>(log =>
                log.Action == PeriodAuditAction.Seal
                && log.GroupId == GroupId
                && log.PerformedBy == expectedActor
                && log.PerformedByName == AuditActorDefaults.AutonomousActorName
                && log.ApprovedByUserId == ActingAdminId),
            Arg.Any<CancellationToken>());
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Is<IDomainEvent>(domainEvent => domainEvent is PeriodClosedEvent
                && ((PeriodClosedEvent)domainEvent).SealedBy == expectedActor
                && ((PeriodClosedEvent)domainEvent).GroupId == GroupId),
            Arg.Any<CancellationToken>());
        _userService.DidNotReceive().GetDisplayName();
    }

    [Test]
    public async Task Handle_NoHttpContextButActingIdIsNoLongerAnAdmin_IsRefused()
    {
        GivenAdmins("3f1c9a52-0000-0000-0000-000000000099");

        await Should.ThrowAsync<InvalidRequestException>(() => _handler.Handle(Command(ActingAdminId), CancellationToken.None));

        await AssertNothingSealedAsync();
    }

    [Test]
    public async Task Handle_NoHttpContextWithEmptyActingAdmin_IsRefused()
    {
        GivenAdmins(Guid.Empty.ToString());

        await Should.ThrowAsync<InvalidRequestException>(() => _handler.Handle(Command(Guid.Empty), CancellationToken.None));

        await AssertNothingSealedAsync();
    }

    [Test]
    public async Task Handle_RequestUserWithoutAdminRoleAndActingAdminSet_IsStillRefused()
    {
        GivenRequestPrincipal(
            new Claim(ClaimTypes.Role, Roles.User),
            new Claim(ClaimTypes.NameIdentifier, "planner"));

        await Should.ThrowAsync<InvalidRequestException>(() => _handler.Handle(Command(ActingAdminId), CancellationToken.None));

        await AssertNothingSealedAsync();
        await _audienceResolver.DidNotReceiveWithAnyArgs().GetAdminUserIdsAsync(default);
    }

    [Test]
    public async Task Handle_AnonymousHttpContextAndActingAdminSet_IsStillRefused()
    {
        GivenRequestPrincipal();

        await Should.ThrowAsync<InvalidRequestException>(() => _handler.Handle(Command(ActingAdminId), CancellationToken.None));

        await AssertNothingSealedAsync();
    }

    [Test]
    public async Task Handle_RequestAdminAndActingAdminSet_KeepsTheRequestIdentity()
    {
        GivenRequestPrincipal(
            new Claim(ClaimTypes.Role, Roles.Admin),
            new Claim(ClaimTypes.NameIdentifier, RequestUserId));
        _userService.GetDisplayName().Returns("Ada Lovelace");

        await _handler.Handle(Command(ActingAdminId), CancellationToken.None);

        await _auditLogRepository.Received(1).AddAsync(
            Arg.Is<PeriodAuditLog>(log => log.PerformedBy == RequestUserId && log.PerformedByName == "Ada Lovelace"),
            Arg.Any<CancellationToken>());
        await _workRepository.Received(1).SealByPeriodAndGroup(
            Start, End, GroupId, WorkLockLevel.Closed, RequestUserId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ActingAdminWithOpenErrors_StaysFailClosed()
    {
        GivenIssues(ScheduleValidationType.Error);

        var ex = await Should.ThrowAsync<PeriodValidationConflictException>(
            () => _handler.Handle(Command(ActingAdminId), CancellationToken.None));

        ex.CurrentErrorCount.ShouldBe(1);
        await AssertNothingSealedAsync();
    }

    [Test]
    public void Deserialize_RequestBodyNamingAnActingAdmin_LeavesTheFieldEmpty()
    {
        var json = $$"""
            {"startDate":"2026-08-01","endDate":"2026-08-31","groupId":"{{GroupId}}","reason":"x","actingAdminUserId":"{{ActingAdminId}}"}
            """;

        var command = JsonSerializer.Deserialize<ClosePeriodByGroupCommand>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        command.ShouldNotBeNull();
        command.GroupId.ShouldBe(GroupId);
        command.ActingAdminUserId.ShouldBeNull();
    }
}
