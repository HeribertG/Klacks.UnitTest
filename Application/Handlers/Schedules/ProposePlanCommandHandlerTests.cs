// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ProposePlanCommandHandler: it creates the scenario, clones the real schedule under
/// the token and flushes BEFORE the guardrail check, writes clean placements via BulkAddWorks, skips
/// blocking placements into the rejected list, and rejects placements whose shift cannot be resolved.
/// The partition/override semantics are exercised through the REAL CompliancePartitionService and
/// SupervisorOverrideAuthorizer wired over a substituted checker, so the handler seam stays covered.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Schedules;

[TestFixture]
public class ProposePlanCommandHandlerTests
{
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly DateOnly From = new(2026, 3, 2);
    private static readonly DateOnly Until = new(2026, 3, 8);
    private const string GeneratedName = "generated name";
    private const string FrenchUser = "fr-CH";

    private IShiftRepository _shiftRepo = null!;
    private IAnalyseScenarioRepository _scenarioRepo = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IPreCommitConflictChecker _checker = null!;
    private IMediator _mediator = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IComplianceEnforcementResolver _enforcementResolver = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IScenarioNameGenerator _nameGenerator = null!;
    private ISealedDayRepository _sealedDayRepo = null!;
    private ProposePlanCommandHandler _handler = null!;

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

        _enforcementResolver = Substitute.For<IComplianceEnforcementResolver>();
        _enforcementResolver.IsSupervisorOverrideAllowedAsync().Returns(true);

        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _httpContextAccessor.HttpContext.Returns(new DefaultHttpContext { User = AnonymousUser() });

        var partitionService = new CompliancePartitionService(
            _checker,
            new SupervisorOverrideAuthorizer(_enforcementResolver, _httpContextAccessor),
            _httpContextAccessor,
            Substitute.For<ILogger<CompliancePartitionService>>());

        _nameGenerator = Substitute.For<IScenarioNameGenerator>();
        _nameGenerator.GenerateAsync(
                Arg.Any<ScenarioNameKind>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(GeneratedName);

        _sealedDayRepo = Substitute.For<ISealedDayRepository>();
        _sealedDayRepo.GetLockedPairsAsync(Arg.Any<IReadOnlyCollection<(DateOnly Date, Guid ClientId)>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<(DateOnly Date, Guid ClientId)>());

        _handler = new ProposePlanCommandHandler(
            _shiftRepo, _scenarioRepo, _scenarioService, partitionService, _mediator, _unitOfWork, _nameGenerator, _sealedDayRepo);
    }

    private static ClaimsPrincipal AnonymousUser() => new(new ClaimsIdentity());

    private static ClaimsPrincipal SupervisorUser() =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, Roles.Authorised) }, "test"));

    private static PlacementInput Placement(Guid clientId, DateOnly date) => new(clientId, ShiftId, date);

    private static ScheduleValidationNotificationDto Collision(Guid clientId) => new()
    {
        Type = ScheduleValidationType.Error,
        ClientId = clientId,
        Comment = "schedule.error-list.collision"
    };

    private static ScheduleValidationNotificationDto OverridableBlock(Guid clientId) => new()
    {
        Type = ScheduleValidationType.Error,
        ClientId = clientId,
        Comment = "schedule.error-list.rest-violation",
        CommentParams = new Dictionary<string, string> { [ComplianceRuleNames.EnforcementRuleParamKey] = ComplianceRuleNames.MinRestHours }
    };

    private Task<ProposePlanOutcome> Propose(params PlacementInput[] placements)
        => Propose(overrideBlock: false, placements);

    private Task<ProposePlanOutcome> Propose(bool overrideBlock, params PlacementInput[] placements)
        => _handler.Handle(new ProposePlanCommand(GroupId, From, Until, placements, overrideBlock), CancellationToken.None);

    [Test]
    public async Task ScenarioName_IsAProposalNameInTheUsersLanguage()
    {
        var command = new ProposePlanCommand(GroupId, From, Until, [Placement(Guid.NewGuid(), From)], Language: FrenchUser);

        var outcome = await _handler.Handle(command, CancellationToken.None);

        outcome.ScenarioName.ShouldBe(GeneratedName);
        await _nameGenerator.Received(1).GenerateAsync(
            ScenarioNameKind.Proposal, From, Until, GroupId, FrenchUser, Arg.Any<CancellationToken>());
        await _scenarioRepo.Received(1).Add(Arg.Is<AnalyseScenario>(s => s.Name == GeneratedName));
    }

    [Test]
    public async Task SealedDayBlocksPlacement_RejectedAndNotWritten()
    {
        var sealedClient = Guid.NewGuid();
        var freeClient = Guid.NewGuid();
        _sealedDayRepo.GetLockedPairsAsync(Arg.Any<IReadOnlyCollection<(DateOnly Date, Guid ClientId)>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<(DateOnly Date, Guid ClientId)> { (From, sealedClient) });

        var outcome = await Propose(Placement(sealedClient, From), Placement(freeClient, From));

        outcome.Written.Count.ShouldBe(1);
        outcome.Rejected.ShouldHaveSingleItem().ClientId.ShouldBe(sealedClient);
        await _mediator.Received(1).Send(
            Arg.Is<BulkAddWorksCommand>(c => c.Request.Works.All(w => w.ClientId == freeClient)), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CleanPlacements_WriteAll_AndCreateScenario()
    {
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();

        var outcome = await Propose(Placement(clientA, From), Placement(clientB, From.AddDays(1)));

        outcome.Written.Count.ShouldBe(2);
        outcome.Rejected.ShouldBeEmpty();
        await _scenarioRepo.Received(1).Add(Arg.Any<AnalyseScenario>());
        await _mediator.Received(1).Send(
            Arg.Is<BulkAddWorksCommand>(c => c.Request.Works.Count == 2), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClonesAndFlushes_BeforeGuardrailCheck()
    {
        var order = new List<string>();
        _scenarioService.When(s => s.CloneScenarioDataAsync(
                Arg.Any<Guid?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("clone"));
        _unitOfWork.When(u => u.CompleteAsync()).Do(_ => order.Add("complete"));
        _checker.When(c => c.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("check"));

        await Propose(Placement(Guid.NewGuid(), From));

        order.IndexOf("clone").ShouldBeLessThan(order.IndexOf("complete"));
        order.IndexOf("complete").ShouldBeLessThan(order.IndexOf("check"));
    }

    [Test]
    public async Task ShiftNotFound_PlacementRejected()
    {
        var unknownShift = Guid.NewGuid();
        var clientA = Guid.NewGuid();

        var outcome = await Propose(new PlacementInput(clientA, unknownShift, From));

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

        var outcome = await Propose(Placement(clean, From), Placement(blocked, From));

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

        var outcome = await Propose(Placement(clientA, From));

        outcome.Written.Count.ShouldBe(1);
        outcome.Warnings.Count.ShouldBe(1);
        outcome.Warnings[0].Comment.ShouldBe("schedule.error-list.weekly-overtime");
    }

    [Test]
    public async Task OverridableBlock_WithoutOverrideFlag_IsRejectedLikeAnyBlock()
    {
        var clientA = Guid.NewGuid();
        _httpContextAccessor.HttpContext!.User = SupervisorUser();
        _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult(new List<ScheduleValidationNotificationDto> { OverridableBlock(clientA) }));

        var outcome = await Propose(overrideBlock: false, Placement(clientA, From));

        outcome.Written.ShouldBeEmpty();
        outcome.Rejected.Count.ShouldBe(1);
    }

    [Test]
    public async Task OverridableBlock_WithOverrideFlag_ButNoSupervisorRole_IsStillRejected()
    {
        var clientA = Guid.NewGuid();
        // _httpContextAccessor.HttpContext.User stays the anonymous user set up in Setup().
        _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult(new List<ScheduleValidationNotificationDto> { OverridableBlock(clientA) }));

        var outcome = await Propose(overrideBlock: true, Placement(clientA, From));

        outcome.Written.ShouldBeEmpty();
        outcome.Rejected.Count.ShouldBe(1);
    }

    [Test]
    public async Task OverridableBlock_WithOverrideFlagAndSupervisorRole_IsWrittenAndReported()
    {
        var clientA = Guid.NewGuid();
        _httpContextAccessor.HttpContext!.User = SupervisorUser();
        _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult(new List<ScheduleValidationNotificationDto> { OverridableBlock(clientA) }));

        var outcome = await Propose(overrideBlock: true, Placement(clientA, From));

        outcome.Written.Count.ShouldBe(1);
        outcome.Rejected.ShouldBeEmpty();
        outcome.Warnings.ShouldContain(w => w.Comment == "schedule.error-list.rest-violation");
    }

    [Test]
    public async Task OverridableBlock_WithOverrideFlagButSupervisorOverrideDisallowedGlobally_IsRejected()
    {
        var clientA = Guid.NewGuid();
        _httpContextAccessor.HttpContext!.User = SupervisorUser();
        _enforcementResolver.IsSupervisorOverrideAllowedAsync().Returns(false);
        _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult(new List<ScheduleValidationNotificationDto> { OverridableBlock(clientA) }));

        var outcome = await Propose(overrideBlock: true, Placement(clientA, From));

        outcome.Written.ShouldBeEmpty();
        outcome.Rejected.Count.ShouldBe(1);
    }

    [Test]
    public async Task HardBlock_Collision_IsNeverOverridable_EvenWithSupervisorAndFlag()
    {
        var clientA = Guid.NewGuid();
        _httpContextAccessor.HttpContext!.User = SupervisorUser();
        _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult(new List<ScheduleValidationNotificationDto> { Collision(clientA) }));

        var outcome = await Propose(overrideBlock: true, Placement(clientA, From));

        outcome.Written.ShouldBeEmpty();
        outcome.Rejected.Count.ShouldBe(1);
    }
}
