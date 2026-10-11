// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The skill executor runs the scenario-scope gate for every skill before the autonomy gate and before dispatch, so a
/// skill invoked with the analyseToken of a scenario the caller cannot see never runs. SkillScenarioScopeGuard itself
/// is covered by SkillScenarioScopeGuardTests.
/// </summary>

using Klacks.Api.Domain.Services.Assistant.Skills;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SkillExecutorScenarioScopeGateTests
{
    private const string TestSkillName = "test_scenario_skill";
    private const string HandlerType = "generic";
    private const string RefusalMessage = "No scenario found for analyseToken 'x'.";

    private ISkillRegistry _registry = null!;
    private IGenericSkillDispatcher _genericDispatcher = null!;
    private IAutonomyGate _autonomyGate = null!;
    private ISkillScenarioScopeGuard _scopeGuard = null!;
    private SkillExecutorService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _registry = Substitute.For<ISkillRegistry>();
        _genericDispatcher = Substitute.For<IGenericSkillDispatcher>();
        _autonomyGate = Substitute.For<IAutonomyGate>();
        _scopeGuard = Substitute.For<ISkillScenarioScopeGuard>();
        _sut = new SkillExecutorService(
            _registry,
            Substitute.For<ISkillUsageTracker>(),
            Substitute.For<IServiceProvider>(),
            _genericDispatcher,
            _autonomyGate,
            Substitute.For<IEntityChangeNotifier>(),
            Substitute.For<IRecentEntityRegistrar>(),
            _scopeGuard,
            Substitute.For<ILogger<SkillExecutorService>>());

        var descriptor = new SkillDescriptor(
            TestSkillName,
            "test",
            SkillCategory.Crud,
            new[] { new SkillParameter("analyseToken", "test", SkillParameterType.String, Required: false) },
            [],
            [],
            null)
        {
            HandlerType = HandlerType,
            HandlerConfig = "{}"
        };

        _registry.GetSkillByName(TestSkillName).Returns(descriptor);
        _genericDispatcher.CanHandle(HandlerType).Returns(true);
        _genericDispatcher.ExecuteAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "ok"));
        _autonomyGate.CheckAsync(
                Arg.Any<SkillDescriptor>(), Arg.Any<SkillExecutionContext>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns((SkillResult?)null);
    }

    private Task<SkillResult> ExecuteAsync() =>
        _sut.ExecuteAsync(
            new SkillInvocation
            {
                SkillName = TestSkillName,
                Parameters = new Dictionary<string, object> { ["analyseToken"] = Guid.NewGuid().ToString() }
            },
            new SkillExecutionContext
            {
                UserId = Guid.NewGuid(),
                TenantId = Guid.NewGuid(),
                UserName = "planner",
                UserPermissions = new List<string> { Klacks.Api.Domain.Constants.Roles.Admin }
            });

    [Test]
    public async Task HiddenScenarioToken_IsRefused_BeforeGateAndDispatch()
    {
        _scopeGuard.CheckAsync(Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.Error(RefusalMessage));

        var result = await ExecuteAsync();

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(RefusalMessage);
        await _autonomyGate.DidNotReceiveWithAnyArgs().CheckAsync(default!, default!, default!, default);
        await _genericDispatcher.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task VisibleScenarioToken_ReachesTheSkill()
    {
        _scopeGuard.CheckAsync(Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns((SkillResult?)null);

        var result = await ExecuteAsync();

        result.Success.ShouldBeTrue();
        await _genericDispatcher.ReceivedWithAnyArgs(1).ExecuteAsync(default!, default!, default!, default);
    }
}
