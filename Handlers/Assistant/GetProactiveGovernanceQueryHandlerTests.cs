// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for GetProactiveGovernanceQueryHandler. The handler folds three resolver reads plus the
/// code-only remediation registry into one DTO: the kill switch, the global autonomy level (with the cap
/// it maps to), the per-kind decisions and per kind whether a scenario could be prepared at all. The
/// real registry is used rather than a substitute - it is a compiled lookup table with no I/O, so a
/// double would only be able to disagree with production.
/// </summary>

using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class GetProactiveGovernanceQueryHandlerTests
{
    private IProactiveGovernanceResolver _resolver = null!;
    private GetProactiveGovernanceQueryHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _resolver = Substitute.For<IProactiveGovernanceResolver>();
        _resolver.ResolveAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ProactiveGovernanceDecision>());

        _sut = new GetProactiveGovernanceQueryHandler(_resolver, new ConditionRemediationRegistry());
    }

    private static ProactiveGovernanceDecision Decision(
        string triggerKind,
        ProactiveMaxAction maxAction = ProactiveMaxAction.Execute) =>
        new(TriggerKind: triggerKind,
            GroupId: null,
            EffectiveMaxAction: maxAction,
            ConfiguredMaxAction: maxAction,
            Enabled: true,
            KillSwitchActive: false,
            DailyActionBudget: 5,
            WindowActionLimit: 3,
            WindowMinutes: 60,
            IsStored: false,
            GlobalAutonomyCap: ProactiveGovernanceDefaults.MapAutonomyLevel(AutonomyLevel.Autonomous));

    [Test]
    public async Task Handle_KindWhoseRemediationCannotBeStagedAsAScenario_ReportsItNotScenarioCapable()
    {
        _resolver.ResolveAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ProactiveGovernanceDecision> { Decision(AgentTriggerKinds.EmptyContainer) });

        var dto = await _sut.Handle(new GetProactiveGovernanceQuery(), CancellationToken.None);

        dto.Rules[0].IsScenarioCapable.ShouldBeFalse(
            "empty_container is remediated by a structural Shift change, which no AnalyseScenario can carry.");
    }

    [Test]
    public async Task Handle_KindWithoutAnyRemediation_ReportsItNotScenarioCapable()
    {
        _resolver.ResolveAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ProactiveGovernanceDecision> { Decision(AgentTriggerKinds.OpenOrder) });

        var dto = await _sut.Handle(new GetProactiveGovernanceQuery(), CancellationToken.None);

        dto.Rules[0].IsScenarioCapable.ShouldBeFalse();
    }

    /// <summary>
    /// EffectiveMaxAction has to be the level the dispatching tick will actually obey, which is the one
    /// the remediation registry lets through. empty_container's remediation is not scenario-capable, so a
    /// stored Prepare is capped at Hint; reporting Prepare made the settings card offer a level whose
    /// tick does nothing but log. The configured level is still reported unchanged beside it.
    /// </summary>
    [Test]
    public async Task Handle_PrepareOnAKindWhoseRemediationCannotBeStaged_ReportsHintAsEffective()
    {
        _resolver.ResolveAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ProactiveGovernanceDecision>
            {
                Decision(AgentTriggerKinds.EmptyContainer, ProactiveMaxAction.Prepare)
            });

        var dto = await _sut.Handle(new GetProactiveGovernanceQuery(), CancellationToken.None);

        dto.Rules[0].EffectiveMaxAction.ShouldBe((int)ProactiveMaxAction.Hint);
        dto.Rules[0].MaxAction.ShouldBe(
            (int)ProactiveMaxAction.Prepare,
            "The configured level stays as stored, so a reader can tell it from the reachable one.");
    }

    [Test]
    public async Task Handle_ExecuteOnAKindWithAnExecuteOnlyRemediation_ReportsExecuteAsEffective()
    {
        _resolver.ResolveAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ProactiveGovernanceDecision>
            {
                Decision(AgentTriggerKinds.EmptyContainer, ProactiveMaxAction.Execute)
            });

        var dto = await _sut.Handle(new GetProactiveGovernanceQuery(), CancellationToken.None);

        dto.Rules[0].EffectiveMaxAction.ShouldBe(
            (int)ProactiveMaxAction.Execute,
            "An Execute-only remediation exists, so Execute is genuinely reachable.");
    }

    /// <summary>
    /// A kind absent from the registry stays capped at Hint whatever it was configured to - that is the
    /// code-only gate, and the card now says so instead of promising a level nothing can carry out.
    /// </summary>
    [TestCase(ProactiveMaxAction.Prepare)]
    [TestCase(ProactiveMaxAction.Execute)]
    public async Task Handle_AKindWithoutAnyRemediation_ReportsHintAsEffective(ProactiveMaxAction configured)
    {
        _resolver.ResolveAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ProactiveGovernanceDecision>
            {
                Decision(AgentTriggerKinds.OpenOrder, configured)
            });

        var dto = await _sut.Handle(new GetProactiveGovernanceQuery(), CancellationToken.None);

        dto.Rules[0].EffectiveMaxAction.ShouldBe((int)ProactiveMaxAction.Hint);
        dto.Rules[0].MaxAction.ShouldBe((int)configured);
    }

    [Test]
    public async Task Handle_HintOnAKindWithoutAnyRemediation_StaysHint()
    {
        _resolver.ResolveAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ProactiveGovernanceDecision>
            {
                Decision(AgentTriggerKinds.OpenOrder, ProactiveMaxAction.Hint)
            });

        var dto = await _sut.Handle(new GetProactiveGovernanceQuery(), CancellationToken.None);

        dto.Rules[0].EffectiveMaxAction.ShouldBe((int)ProactiveMaxAction.Hint);
    }

    [Test]
    public async Task Handle_MapsTheGlobalAutonomyLevelAndItsCapIntoTheDto()
    {
        // Arrange
        _resolver.GetGlobalAutonomyLevelAsync(Arg.Any<CancellationToken>())
            .Returns(AutonomyLevel.Autonomous);
        _resolver.ResolveAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ProactiveGovernanceDecision>
            {
                new(TriggerKind: "unstaffed_shift",
                    GroupId: null,
                    EffectiveMaxAction: ProactiveMaxAction.Execute,
                    ConfiguredMaxAction: ProactiveMaxAction.Execute,
                    Enabled: true,
                    KillSwitchActive: false,
                    DailyActionBudget: 5,
                    WindowActionLimit: 3,
                    WindowMinutes: 60,
                    IsStored: false,
                    GlobalAutonomyCap: ProactiveGovernanceDefaults.MapAutonomyLevel(AutonomyLevel.Autonomous))
            });

        // Act
        var dto = await _sut.Handle(new GetProactiveGovernanceQuery(), CancellationToken.None);

        // Assert
        dto.GlobalAutonomyLevel.ShouldBe((int)AutonomyLevel.Autonomous);
        dto.GlobalAutonomyCap.ShouldBe((int)ProactiveMaxAction.Execute);
        dto.Rules.Count.ShouldBe(1);
        dto.Rules[0].GlobalAutonomyCap.ShouldBe((int)ProactiveMaxAction.Execute);
    }
}
