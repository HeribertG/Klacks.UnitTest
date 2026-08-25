// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ProactiveGovernanceResolver, the single place that folds stored rules, defaults and the
/// global kill switch into one answer. Pins the precedence a later action dispatcher relies on: the
/// kill switch and a disabled kind both force EffectiveMaxAction back to Hint while leaving the
/// configured value visible, a group-scoped rule beats the installation-wide one, an unconfigured kind
/// resolves to the fail-safe defaults, and an unparseable kill-switch value does not silently unlock
/// anything.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class ProactiveGovernanceResolverTests
{
    private const string TriggerKind = "unstaffed_shift";

    private IAgentTriggerGovernanceRepository _repository = null!;
    private ISettingsReader _settingsReader = null!;
    private ProactiveGovernanceResolver _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IAgentTriggerGovernanceRepository>();
        _settingsReader = Substitute.For<ISettingsReader>();
        _repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentTriggerGovernance>());

        _sut = new ProactiveGovernanceResolver(
            _repository, _settingsReader, Substitute.For<ILogger<ProactiveGovernanceResolver>>());
    }

    private void GivenKillSwitch(string? value)
        => _settingsReader.GetSetting(SettingKeys.KlacksyProactiveKillSwitch)
            .Returns(value is null
                ? null
                : new Klacks.Api.Domain.Models.Settings.Settings
                {
                    Type = SettingKeys.KlacksyProactiveKillSwitch,
                    Value = value
                });

    private void GivenStoredRule(AgentTriggerGovernance rule)
    {
        _repository.FindAsync(rule.TriggerKind, rule.GroupId, Arg.Any<CancellationToken>())
            .Returns(rule);
        _repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentTriggerGovernance> { rule });
    }

    [Test]
    public async Task ResolveAsync_WithoutAStoredRule_UsesTheFailSafeDefaults()
    {
        // Arrange
        GivenKillSwitch(null);

        // Act
        var decision = await _sut.ResolveAsync(TriggerKind, null, CancellationToken.None);

        // Assert
        decision.EffectiveMaxAction.ShouldBe(ProactiveMaxAction.Hint);
        decision.Enabled.ShouldBe(ProactiveGovernanceDefaults.Enabled);
        decision.DailyActionBudget.ShouldBe(ProactiveGovernanceDefaults.DailyActionBudget);
        decision.WindowActionLimit.ShouldBe(ProactiveGovernanceDefaults.WindowActionLimit);
        decision.WindowMinutes.ShouldBe(ProactiveGovernanceDefaults.WindowMinutes);
        decision.IsStored.ShouldBeFalse();
    }

    [Test]
    public async Task ResolveAsync_WithKillSwitchOn_PinsEffectiveMaxActionToHint()
    {
        // Arrange
        GivenKillSwitch("true");
        GivenStoredRule(new AgentTriggerGovernance
        {
            TriggerKind = TriggerKind,
            MaxAction = ProactiveMaxAction.Execute,
            ResponsibleOwnerUserId = Guid.NewGuid()
        });

        // Act
        var decision = await _sut.ResolveAsync(TriggerKind, null, CancellationToken.None);

        // Assert
        decision.EffectiveMaxAction.ShouldBe(ProactiveMaxAction.Hint);
        decision.ConfiguredMaxAction.ShouldBe(ProactiveMaxAction.Execute);
        decision.KillSwitchActive.ShouldBeTrue();
    }

    [Test]
    public async Task ResolveAsync_WithDisabledKind_PinsEffectiveMaxActionToHint()
    {
        // Arrange
        GivenKillSwitch("false");
        GivenStoredRule(new AgentTriggerGovernance
        {
            TriggerKind = TriggerKind,
            MaxAction = ProactiveMaxAction.Prepare,
            Enabled = false
        });

        // Act
        var decision = await _sut.ResolveAsync(TriggerKind, null, CancellationToken.None);

        // Assert
        decision.EffectiveMaxAction.ShouldBe(ProactiveMaxAction.Hint);
        decision.ConfiguredMaxAction.ShouldBe(ProactiveMaxAction.Prepare);
        decision.KillSwitchActive.ShouldBeFalse();
    }

    [Test]
    public async Task ResolveAsync_WithEnabledKindAndKillSwitchOff_HonoursTheStoredMaxAction()
    {
        // Arrange
        GivenKillSwitch("false");
        GivenStoredRule(new AgentTriggerGovernance
        {
            TriggerKind = TriggerKind,
            MaxAction = ProactiveMaxAction.Prepare,
            Enabled = true
        });

        // Act
        var decision = await _sut.ResolveAsync(TriggerKind, null, CancellationToken.None);

        // Assert
        decision.EffectiveMaxAction.ShouldBe(ProactiveMaxAction.Prepare);
        decision.IsStored.ShouldBeTrue();
    }

    [Test]
    public async Task ResolveAsync_WithGroupScopedRule_PrefersItOverTheInstallationWideOne()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        GivenKillSwitch("false");
        _repository.FindAsync(TriggerKind, null, Arg.Any<CancellationToken>())
            .Returns(new AgentTriggerGovernance
            {
                TriggerKind = TriggerKind,
                MaxAction = ProactiveMaxAction.Hint
            });
        _repository.FindAsync(TriggerKind, groupId, Arg.Any<CancellationToken>())
            .Returns(new AgentTriggerGovernance
            {
                TriggerKind = TriggerKind,
                GroupId = groupId,
                MaxAction = ProactiveMaxAction.Prepare
            });

        // Act
        var decision = await _sut.ResolveAsync(TriggerKind, groupId, CancellationToken.None);

        // Assert
        decision.EffectiveMaxAction.ShouldBe(ProactiveMaxAction.Prepare);
    }

    [Test]
    public async Task ResolveAsync_WithoutGroupScopedRule_FallsBackToTheInstallationWideOne()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        GivenKillSwitch("false");
        _repository.FindAsync(TriggerKind, groupId, Arg.Any<CancellationToken>())
            .Returns((AgentTriggerGovernance?)null);
        _repository.FindAsync(TriggerKind, null, Arg.Any<CancellationToken>())
            .Returns(new AgentTriggerGovernance
            {
                TriggerKind = TriggerKind,
                MaxAction = ProactiveMaxAction.Execute
            });

        // Act
        var decision = await _sut.ResolveAsync(TriggerKind, groupId, CancellationToken.None);

        // Assert
        decision.EffectiveMaxAction.ShouldBe(ProactiveMaxAction.Execute);
    }

    [Test]
    public async Task IsKillSwitchActiveAsync_WithUnparseableValue_StaysOff()
    {
        // Arrange
        GivenKillSwitch("perhaps");

        // Act
        var active = await _sut.IsKillSwitchActiveAsync(CancellationToken.None);

        // Assert
        active.ShouldBeFalse();
    }

    [Test]
    public async Task ResolveAllAsync_CoversEveryGovernedKind()
    {
        // Arrange
        GivenKillSwitch(null);

        // Act
        var decisions = await _sut.ResolveAllAsync(CancellationToken.None);

        // Assert
        decisions.Count.ShouldBe(ProactiveGovernanceDefaults.GovernedKinds.Count);
        decisions.Select(decision => decision.TriggerKind)
            .ShouldBe(ProactiveGovernanceDefaults.GovernedKinds, ignoreOrder: true);
        decisions.ShouldAllBe(decision => decision.EffectiveMaxAction == ProactiveMaxAction.Hint);
    }
}
