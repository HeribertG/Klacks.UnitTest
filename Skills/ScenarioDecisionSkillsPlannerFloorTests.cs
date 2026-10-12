// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Owner decision 2026-10-11: planners without a role (planner floor) may accept and reject scenarios, like they may
/// edit works in the schedule (CanEditSchedule). The shipped seeds of accept_scenario and reject_scenario therefore
/// require only rights of Permissions.PlannerFloor; run through the real executor a floor-only user passes the
/// permission check, while a scenario of a hidden group is still answered like a missing one (the command handler's
/// group visibility check, here as its KeyNotFoundException).
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Persistence.Seed.Models;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ScenarioDecisionSkillsPlannerFloorTests
{
    private const string AcceptSkill = "accept_scenario";
    private const string RejectSkill = "reject_scenario";
    private const string SeedFileName = "skill-seeds.json";

    private IMediator _mediator = null!;
    private SkillExecutorService _executor = null!;
    private Guid _visibleScenarioId;
    private Guid _hiddenScenarioId;

    private static Dictionary<string, SkillSeedDefinition> Seeds()
    {
        var path = RepositoryRootLocator.FindFile("Klacks.Api", "Application", "Skills", "Definitions", SeedFileName);
        path.ShouldNotBeNull();
        var seed = JsonSerializer.Deserialize<SkillSeedFile>(
            File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return seed!.Skills.ToDictionary(s => s.Name, StringComparer.Ordinal);
    }

    [SetUp]
    public void SetUp()
    {
        _visibleScenarioId = Guid.NewGuid();
        _hiddenScenarioId = Guid.NewGuid();
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<AcceptAnalyseScenarioCommand>().ScenarioId == _hiddenScenarioId
                ? throw new KeyNotFoundException("AnalyseScenario not found")
                : new AcceptAnalyseScenarioResult(true));
        _mediator.Send(Arg.Any<RejectAnalyseScenarioCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<RejectAnalyseScenarioCommand>().ScenarioId == _hiddenScenarioId
                ? throw new KeyNotFoundException("AnalyseScenario not found")
                : true);

        var seeds = Seeds();
        var registry = Substitute.For<ISkillRegistry>();
        registry.GetSkillByName(AcceptSkill).Returns(Descriptor(seeds[AcceptSkill], typeof(AcceptScenarioSkill)));
        registry.GetSkillByName(RejectSkill).Returns(Descriptor(seeds[RejectSkill], typeof(RejectScenarioSkill)));

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(AcceptScenarioSkill)).Returns(_ => new AcceptScenarioSkill(
            _mediator, Substitute.For<IPendingConfirmationStore>(), Substitute.For<ITurnConfirmationScope>(),
            Substitute.For<ICompanyClock>()));
        serviceProvider.GetService(typeof(RejectScenarioSkill)).Returns(_ => new RejectScenarioSkill(_mediator));

        var gate = Substitute.For<IAutonomyGate>();
        gate.CheckAsync(
                Arg.Any<SkillDescriptor>(), Arg.Any<SkillExecutionContext>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns((SkillResult?)null);

        _executor = new SkillExecutorService(
            registry,
            Substitute.For<ISkillUsageTracker>(),
            serviceProvider,
            Substitute.For<IGenericSkillDispatcher>(),
            gate,
            Substitute.For<IEntityChangeNotifier>(),
            Substitute.For<IRecentEntityRegistrar>(),
            new SkillScenarioScopeGuard(TestScenarioTokenAccess.AllVisible()),
            Substitute.For<ILogger<SkillExecutorService>>());
    }

    private static SkillDescriptor Descriptor(SkillSeedDefinition seed, Type implementation) =>
        new(seed.Name, "test", SkillCategory.Action, [], seed.RequiredPermissions, [], implementation);

    private static SkillExecutionContext FloorUser() => new()
    {
        UserId = Guid.Empty,
        TenantId = Guid.Empty,
        UserName = "planner",
        UserPermissions = Permissions.PlannerFloor
    };

    private Task<SkillResult> RunAsync(string skill, Guid scenarioId) => _executor.ExecuteAsync(
        new SkillInvocation
        {
            SkillName = skill,
            Parameters = new Dictionary<string, object> { ["scenarioId"] = scenarioId.ToString() }
        },
        FloorUser());

    [TestCase(AcceptSkill)]
    [TestCase(RejectSkill)]
    public void ShippedSeed_RequiresTheFloorWorkEditRightOnly(string skill)
    {
        var required = Seeds()[skill].RequiredPermissions;

        required.ShouldBe(new[] { Permissions.CanEditSchedule });
        required.ShouldAllBe(permission => Permissions.PlannerFloor.Contains(permission));
    }

    [TestCase(AcceptSkill)]
    [TestCase(RejectSkill)]
    public async Task FloorOnlyUser_CanDecideAVisibleScenario(string skill)
    {
        var result = await RunAsync(skill, _visibleScenarioId);

        result.Success.ShouldBeTrue(result.Message);
    }

    [TestCase(AcceptSkill)]
    [TestCase(RejectSkill)]
    public async Task FloorOnlyUser_ScenarioOfAHiddenGroup_IsAnsweredLikeAMissingOne(string skill)
    {
        var result = await RunAsync(skill, _hiddenScenarioId);

        result.Success.ShouldBeFalse();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain("not exist");
        result.Message.ShouldNotContain("Permission denied");
    }
}
