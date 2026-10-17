// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Parameter keys that may carry the stale-scenario consent (ScenarioAcceptDefaults.ReservedParameters) are dropped from
/// every invocation at the executor entrance, before the gate stores anything and before the skill runs, so neither a
/// model nor a background caller can supply them; ordinary parameters reach the skill unchanged.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SkillExecutorServiceReservedParameterTests
{
    private const string SkillName = "accept_scenario";
    private const string GenericHandlerType = "test_handler";
    private const string GenericHandlerConfig = "{\"endpoint\":\"test\"}";

    private IGenericSkillDispatcher _genericDispatcher = null!;
    private IAutonomyGate _autonomyGate = null!;
    private SkillExecutorService _sut = null!;
    private Dictionary<string, object>? _parametersSeenBySkill;

    [SetUp]
    public void SetUp()
    {
        var registry = Substitute.For<ISkillRegistry>();
        _genericDispatcher = Substitute.For<IGenericSkillDispatcher>();
        _autonomyGate = Substitute.For<IAutonomyGate>();
        _autonomyGate.CheckAsync(
                Arg.Any<SkillDescriptor>(), Arg.Any<SkillExecutionContext>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns((SkillResult?)null);
        _genericDispatcher.CanHandle(GenericHandlerType).Returns(true);
        _genericDispatcher.ExecuteAsync(
                GenericHandlerType, GenericHandlerConfig,
                Arg.Any<Dictionary<string, object>>(), Arg.Any<ExplainDepthProfile?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _parametersSeenBySkill = ci.ArgAt<Dictionary<string, object>>(2);
                return SkillResult.SuccessResult(null, "ran");
            });
        registry.GetSkillByName(SkillName).Returns(
            new SkillDescriptor(SkillName, "test", SkillCategory.Action, [], [], [], null)
            {
                HandlerType = GenericHandlerType,
                HandlerConfig = GenericHandlerConfig
            });

        _sut = new SkillExecutorService(
            registry,
            Substitute.For<ISkillUsageTracker>(),
            Substitute.For<IServiceProvider>(),
            _genericDispatcher,
            _autonomyGate,
            Substitute.For<IEntityChangeNotifier>(),
            Substitute.For<IRecentEntityRegistrar>(),
            Substitute.For<ISkillScenarioScopeGuard>(),
            Substitute.For<ILogger<SkillExecutorService>>());
    }

    [Test]
    public async Task ReservedConsentKeys_AreDroppedBeforeTheGateAndTheSkill()
    {
        var parameters = new Dictionary<string, object> { ["scenarioId"] = Guid.NewGuid().ToString() };
        foreach (var key in ScenarioAcceptDefaults.ReservedParameters)
        {
            parameters[key] = "true";
        }

        var result = await _sut.ExecuteAsync(
            new SkillInvocation { SkillName = SkillName, Parameters = parameters },
            new SkillExecutionContext
            {
                UserId = Guid.Empty,
                TenantId = Guid.Empty,
                UserName = "plan step",
                UserPermissions = new List<string>(),
                BypassAutonomyGate = true
            });

        result.Success.ShouldBeTrue();
        _parametersSeenBySkill.ShouldNotBeNull();
        _parametersSeenBySkill.Keys.ShouldBe(new[] { "scenarioId" });
        await _autonomyGate.Received(1).CheckAsync(
            Arg.Any<SkillDescriptor>(),
            Arg.Any<SkillExecutionContext>(),
            Arg.Is<Dictionary<string, object>>(p => p.Count == 1 && p.ContainsKey("scenarioId")),
            Arg.Any<CancellationToken>());
    }
}
