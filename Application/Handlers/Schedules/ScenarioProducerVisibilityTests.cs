// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Klacksy's evaluate_scenario and propose_plan go through EvaluateScenarioQueryHandler and ProposePlanCommandHandler,
/// which loaded or created scenarios without any group check - a second bypass next to the AnalyseScenarios
/// endpoints. A scenario of a hidden group must be evaluated like a missing one (and its grid never read), and no
/// proposal may be created for a hidden group or, by a restricted caller, for all groups.
/// </summary>

using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Schedules;
using Klacks.Api.Application.Interfaces.PeriodClosing;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;
using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.UnitTest.Application.Handlers.Schedules;

[TestFixture]
public class ScenarioProducerVisibilityTests
{
    private static readonly DateOnly From = new(2026, 3, 2);
    private static readonly DateOnly Until = new(2026, 3, 8);

    private IAnalyseScenarioRepository _scenarioRepository = null!;
    private IScheduleEntriesService _entries = null!;
    private IPeriodValidationLoader _loader = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IGroupVisibilityGuard _guard = null!;
    private Guid _hiddenGroupId;

    [SetUp]
    public void SetUp()
    {
        _scenarioRepository = Substitute.For<IAnalyseScenarioRepository>();
        _entries = Substitute.For<IScheduleEntriesService>();
        _loader = Substitute.For<IPeriodValidationLoader>();
        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _hiddenGroupId = Guid.NewGuid();
        _guard = Substitute.For<IGroupVisibilityGuard>();
        _guard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(false);
        _guard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Guid>(0) != _hiddenGroupId);
    }

    [Test]
    public async Task Evaluate_ScenarioOfHiddenGroupByToken_IsNotFoundAndReadsNoGrid()
    {
        var scenario = HiddenScenario();
        _scenarioRepository.GetByTokenAsync(scenario.Token, Arg.Any<CancellationToken>()).Returns(scenario);
        var handler = new EvaluateScenarioQueryHandler(_scenarioRepository, _entries, _loader, _guard);

        var result = await handler.Handle(new EvaluateScenarioQuery(null, scenario.Token), CancellationToken.None);

        result.Found.ShouldBeFalse();
        await _entries.DidNotReceiveWithAnyArgs().GetScheduleEntriesAsync(default!, default);
    }

    [Test]
    public async Task Evaluate_ScenarioOfHiddenGroupById_IsNotFound()
    {
        var scenario = HiddenScenario();
        _scenarioRepository.Get(scenario.Id).Returns(scenario);
        var handler = new EvaluateScenarioQueryHandler(_scenarioRepository, _entries, _loader, _guard);

        var result = await handler.Handle(new EvaluateScenarioQuery(scenario.Id, null), CancellationToken.None);

        result.Found.ShouldBeFalse();
    }

    [Test]
    public async Task ProposePlan_ForHiddenGroup_IsRefusedLikeAMissingGroupAndCreatesNoScenario()
    {
        var handler = ProposePlanHandler();

        var act = () => handler.Handle(Proposal(_hiddenGroupId), CancellationToken.None);

        (await Should.ThrowAsync<KeyNotFoundException>(act)).Message.ShouldBe($"Group with ID {_hiddenGroupId} not found");
        await _scenarioRepository.DidNotReceiveWithAnyArgs().Add(default!);
        await _scenarioService.DidNotReceiveWithAnyArgs().CloneScenarioDataAsync(default, default, default, default, default, default, default);
    }

    [Test]
    public async Task ProposePlan_WithoutGroup_IsForbiddenForARestrictedCaller()
    {
        var handler = ProposePlanHandler();

        var act = () => handler.Handle(Proposal(null), CancellationToken.None);

        await Should.ThrowAsync<ForbiddenException>(act);
        await _scenarioRepository.DidNotReceiveWithAnyArgs().Add(default!);
    }

    private AnalyseScenario HiddenScenario() => new()
    {
        Id = Guid.NewGuid(),
        Token = Guid.NewGuid(),
        Name = "Hidden proposal",
        Status = AnalyseScenarioStatus.Active,
        GroupId = _hiddenGroupId,
        FromDate = From,
        UntilDate = Until
    };

    private static ProposePlanCommand Proposal(Guid? groupId) => new(
        groupId, From, Until, new List<PlacementInput> { new(Guid.NewGuid(), Guid.NewGuid(), From) });

    private ProposePlanCommandHandler ProposePlanHandler() => new(
        Substitute.For<IShiftRepository>(),
        _scenarioRepository,
        _scenarioService,
        Substitute.For<ICompliancePartitionService>(),
        Substitute.For<IMediator>(),
        _unitOfWork,
        Substitute.For<IScenarioNameGenerator>(),
        Substitute.For<ISealedDayRepository>(),
        _guard,
        Substitute.For<IClientPlanningPeriodGuard>(),
        ShiftVisibilityGuards.AllowAll());
}
