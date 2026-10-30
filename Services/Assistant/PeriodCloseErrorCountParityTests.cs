// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Parity of the error count between the advice and the manual close: the real GetPeriodIssuesQueryHandler, the source of the
/// advice, and the real ClosePeriodByGroupCommandHandler run over the SAME validation loader and the SAME escalation, and the close
/// refuses with exactly the count the advice reads. A warning a Block rule escalates counts on both sides, and schedule notes,
/// which only the advice loads, carry Info and count on neither.
/// </summary>

using Klacks.Api.Application.Commands.PeriodClosing;
using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.PeriodClosing;
using Klacks.Api.Application.Interfaces.PeriodClosing;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Queries.PeriodClosing;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Events;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class PeriodCloseErrorCountParityTests
{
    private const string BlockedCode = "Rest";
    private const string OpenCode = "Overtime";

    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid ActingAdminId = Guid.NewGuid();
    private static readonly DateOnly Start = new(2026, 8, 1);
    private static readonly DateOnly End = new(2026, 8, 31);

    private IPeriodValidationLoader _loader = null!;
    private IComplianceEscalationService _escalation = null!;

    [SetUp]
    public void Setup()
    {
        _loader = Substitute.For<IPeriodValidationLoader>();
        _loader.LoadAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new List<PeriodIssueDto>
            {
                Issue(ScheduleValidationType.Error, OpenCode),
                Issue(ScheduleValidationType.Warning, BlockedCode),
                Issue(ScheduleValidationType.Warning, OpenCode),
                Issue(ScheduleValidationType.Info, OpenCode)
            });
        _escalation = Substitute.For<IComplianceEscalationService>();
        _escalation.When(service => service.EscalateBlockedIssuesAsync(Arg.Any<IReadOnlyList<PeriodIssueDto>>()))
            .Do(call =>
            {
                foreach (var issue in call.Arg<IReadOnlyList<PeriodIssueDto>>().Where(issue => issue.Code == BlockedCode))
                {
                    issue.Severity = ScheduleValidationType.Error;
                }
            });
    }

    private static PeriodIssueDto Issue(ScheduleValidationType severity, string code) =>
        new() { Severity = severity, Code = code, Date = Start };

    private async Task<int> AdvisedErrorCountAsync()
    {
        var readRepository = Substitute.For<IPeriodClosingReadRepository>();
        readRepository.GetScheduleNoteIssues(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<PeriodScheduleNoteIssueRow> { new(Start, Guid.NewGuid(), "Anna", "Muster", "note") });
        var groups = Substitute.For<IGroupVisibilityGuard>();
        groups.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(true);

        var handler = new GetPeriodIssuesQueryHandler(
            readRepository, _loader, _escalation, groups, Substitute.For<IClientVisibilityGuard>(),
            Substitute.For<ILogger<GetPeriodIssuesQueryHandler>>());

        return PeriodCloseGates.CountErrors(await handler.Handle(new GetPeriodIssuesQuery(Start, End, GroupId), CancellationToken.None));
    }

    private ClosePeriodByGroupCommandHandler CloseHandler()
    {
        var lockLevels = Substitute.For<IWorkLockLevelService>();
        lockLevels.CanSeal(Arg.Any<WorkLockLevel>(), Arg.Any<WorkLockLevel>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(true);
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);
        var audience = Substitute.For<IPlanningAudienceResolver>();
        audience.GetAdminUserIdsAsync(Arg.Any<CancellationToken>()).Returns(new HashSet<string> { ActingAdminId.ToString() });
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>()).Returns(call => call.ArgAt<Func<Task<int>>>(0)());

        return new ClosePeriodByGroupCommandHandler(
            Substitute.For<IWorkRepository>(),
            Substitute.For<IBreakRepository>(),
            lockLevels,
            accessor,
            Substitute.For<IPeriodAuditLogRepository>(),
            Substitute.For<ISealedDayRepository>(),
            Substitute.For<IDomainEventDispatcher>(),
            _loader,
            _escalation,
            Substitute.For<IUserService>(),
            audience,
            unitOfWork,
            Substitute.For<ILogger<ClosePeriodByGroupCommandHandler>>());
    }

    [Test]
    public async Task TheManualClose_RefusesWithExactlyTheCountTheAdviceReads()
    {
        var advised = await AdvisedErrorCountAsync();

        var refusal = await Should.ThrowAsync<PeriodValidationConflictException>(() => CloseHandler().Handle(
            new ClosePeriodByGroupCommand(Start, End, GroupId, AuditActorDefaults.AutonomousActorName, ActingAdminUserId: ActingAdminId),
            CancellationToken.None));

        advised.ShouldBe(2);
        refusal.CurrentErrorCount.ShouldBe(advised);
    }
}
