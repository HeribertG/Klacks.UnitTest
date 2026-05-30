// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ProposePlanService: it creates the scenario, clones the real schedule under the
/// token and flushes BEFORE the guardrail check, writes clean placements via BulkAddWorks, skips
/// blocking placements into the rejected list, and rejects placements whose shift cannot be resolved.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Services.Schedules;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class ProposePlanServiceTests
{
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly DateOnly From = new(2026, 3, 2);
    private static readonly DateOnly Until = new(2026, 3, 8);

    private IShiftRepository _shiftRepo = null!;
    private IAnalyseScenarioRepository _scenarioRepo = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IPreCommitConflictChecker _checker = null!;
    private IMediator _mediator = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ProposePlanService _service = null!;

    [SetUp]
    public void Setup()
    {
        _shiftRepo = Substitute.For<IShiftRepository>();
        _shiftRepo.Get(ShiftId).Returns(new Shift
        {
            Id = ShiftId,
            Name = "Day",
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0)
        });

        _scenarioRepo = Substitute.For<IAnalyseScenarioRepository>();
        _scenarioRepo.GetByGroupAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AnalyseScenario>());

        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _scenarioService.CloneScenarioDataAsync(
                Arg.Any<Guid?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Guid>());

        _checker = Substitute.For<IPreCommitConflictChecker>();
        _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(PreCommitCheckResult.Empty);

        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<BulkAddWorksCommand>(), Arg.Any<CancellationToken>())
            .Returns(new BulkWorksResponse());

        _unitOfWork = Substitute.For<IUnitOfWork>();

        _service = new ProposePlanService(_shiftRepo, _scenarioRepo, _scenarioService, _checker, _mediator, _unitOfWork);
    }

    private static PlacementInput Placement(Guid clientId, DateOnly date) => new(clientId, ShiftId, date);

    private static ScheduleValidationNotificationDto Collision(Guid clientId) => new()
    {
        Type = ScheduleValidationType.Error,
        ClientId = clientId,
        Comment = "schedule.error-list.collision"
    };

    [Test]
    public async Task CleanPlacements_WriteAll_AndCreateScenario()
    {
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();

        var outcome = await _service.ProposeAsync(GroupId, From, Until,
            new[] { Placement(clientA, From), Placement(clientB, From.AddDays(1)) });

        outcome.Written.Count.ShouldBe(2);
        outcome.Rejected.ShouldBeEmpty();
        await _scenarioRepo.Received(1).Add(Arg.Any<AnalyseScenario>());
        await _mediator.Received(1).Send(
            Arg.Is<BulkAddWorksCommand>(c => c.Request.Works.Count == 2), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClonesAndFlushes_BeforeGuardrailCheck()
    {
        // The guardrail queries works by token, so the clone + flush MUST happen before the first
        // CheckAsync — otherwise the checker sees an empty world and falsely passes. CheckAsync runs
        // twice (batch + warning pass), so a manual order log is used instead of Received.InOrder.
        var order = new List<string>();
        _scenarioService.When(s => s.CloneScenarioDataAsync(
                Arg.Any<Guid?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("clone"));
        _unitOfWork.When(u => u.CompleteAsync()).Do(_ => order.Add("complete"));
        _checker.When(c => c.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("check"));

        var clientA = Guid.NewGuid();
        await _service.ProposeAsync(GroupId, From, Until, new[] { Placement(clientA, From) });

        order.IndexOf("clone").ShouldBeLessThan(order.IndexOf("complete"));
        order.IndexOf("complete").ShouldBeLessThan(order.IndexOf("check"));
    }

    [Test]
    public async Task ShiftNotFound_PlacementRejected()
    {
        var unknownShift = Guid.NewGuid();
        var clientA = Guid.NewGuid();

        var outcome = await _service.ProposeAsync(GroupId, From, Until,
            new[] { new PlacementInput(clientA, unknownShift, From) });

        outcome.Written.ShouldBeEmpty();
        outcome.Rejected.Count.ShouldBe(1);
        outcome.Rejected[0].Reason.ShouldBe("shift not found");
        await _mediator.DidNotReceive().Send(Arg.Any<BulkAddWorksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BlockingPlacement_SkippedAndReported_OthersWritten()
    {
        var clean = Guid.NewGuid();
        var blocked = Guid.NewGuid();

        _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var rows = ci.Arg<IReadOnlyList<PlannedWorkRow>>();
                return rows.Any(r => r.ClientId == blocked)
                    ? new PreCommitCheckResult(new List<ScheduleValidationNotificationDto> { Collision(blocked) })
                    : PreCommitCheckResult.Empty;
            });

        var outcome = await _service.ProposeAsync(GroupId, From, Until,
            new[] { Placement(clean, From), Placement(blocked, From) });

        outcome.Written.Count.ShouldBe(1);
        outcome.Written[0].ClientId.ShouldBe(clean);
        outcome.Rejected.Count.ShouldBe(1);
        outcome.Rejected[0].ClientId.ShouldBe(blocked);
        outcome.Rejected[0].Reason.ShouldBe("schedule.error-list.collision");
        await _mediator.Received(1).Send(
            Arg.Is<BulkAddWorksCommand>(c => c.Request.Works.Count == 1), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WarningsOnAcceptedSet_AreSurfaced()
    {
        var clientA = Guid.NewGuid();
        _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult(new List<ScheduleValidationNotificationDto>
            {
                new()
                {
                    Type = ScheduleValidationType.Warning,
                    ClientId = clientA,
                    Comment = "schedule.error-list.weekly-overtime"
                }
            }));

        var outcome = await _service.ProposeAsync(GroupId, From, Until, new[] { Placement(clientA, From) });

        outcome.Written.Count.ShouldBe(1);
        outcome.Warnings.Count.ShouldBe(1);
        outcome.Warnings[0].Comment.ShouldBe("schedule.error-list.weekly-overtime");
    }
}
