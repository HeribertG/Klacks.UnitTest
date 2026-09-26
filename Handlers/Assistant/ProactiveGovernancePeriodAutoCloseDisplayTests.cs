// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The governance card must show the effect period_auto_close really has. Its own chain seals on an effective
/// Execute with the global level at FullyAutonomous, so the card reports Execute exactly then and Hint otherwise -
/// never the registry-gated Hint it showed before, which hid running closes. next_period_scheduling_due keeps its
/// previous, registry-gated value.
/// </summary>

using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class ProactiveGovernancePeriodAutoCloseDisplayTests
{
    private IProactiveGovernanceResolver _resolver = null!;
    private GetProactiveGovernanceQueryHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _resolver = Substitute.For<IProactiveGovernanceResolver>();
        _sut = new GetProactiveGovernanceQueryHandler(_resolver, new ConditionRemediationRegistry());
    }

    private void Given(AutonomyLevel globalLevel, string kind, ProactiveMaxAction effective)
    {
        _resolver.GetGlobalAutonomyLevelAsync(Arg.Any<CancellationToken>()).Returns(globalLevel);
        _resolver.ResolveAllAsync(Arg.Any<CancellationToken>()).Returns(new List<ProactiveGovernanceDecision>
        {
            new(TriggerKind: kind,
                GroupId: null,
                EffectiveMaxAction: effective,
                ConfiguredMaxAction: ProactiveMaxAction.Execute,
                Enabled: true,
                KillSwitchActive: false,
                DailyActionBudget: 5,
                WindowActionLimit: 3,
                WindowMinutes: 60,
                IsStored: true,
                GlobalAutonomyCap: ProactiveGovernanceDefaults.MapAutonomyLevel(globalLevel))
        });
    }

    [Test]
    public async Task PeriodAutoClose_ExecuteAtFullAutonomy_IsShownAsEffectiveExecute()
    {
        Given(AutonomyLevel.FullyAutonomous, AgentTriggerKinds.PeriodAutoClose, ProactiveMaxAction.Execute);

        var dto = await _sut.Handle(new GetProactiveGovernanceQuery(), CancellationToken.None);

        dto.Rules.Single().EffectiveMaxAction.ShouldBe((int)ProactiveMaxAction.Execute);
    }

    [TestCase(AutonomyLevel.Autonomous, ProactiveMaxAction.Execute)]
    [TestCase(AutonomyLevel.FullyAutonomous, ProactiveMaxAction.Prepare)]
    [TestCase(AutonomyLevel.FullyAutonomous, ProactiveMaxAction.Hint)]
    public async Task PeriodAutoClose_AnythingElse_IsShownAsHint(AutonomyLevel globalLevel, ProactiveMaxAction effective)
    {
        Given(globalLevel, AgentTriggerKinds.PeriodAutoClose, effective);

        var dto = await _sut.Handle(new GetProactiveGovernanceQuery(), CancellationToken.None);

        dto.Rules.Single().EffectiveMaxAction.ShouldBe((int)ProactiveMaxAction.Hint);
    }

    [Test]
    public async Task NextPeriodSchedulingDue_KeepsTheRegistryGatedValue()
    {
        Given(AutonomyLevel.FullyAutonomous, AgentTriggerKinds.NextPeriodSchedulingDue, ProactiveMaxAction.Execute);

        var dto = await _sut.Handle(new GetProactiveGovernanceQuery(), CancellationToken.None);

        dto.Rules.Single().EffectiveMaxAction.ShouldBe((int)ProactiveMaxAction.Hint);
    }
}
