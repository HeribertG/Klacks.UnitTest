// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ProactiveGovernanceResolver, the single place that folds stored rules, defaults, the
/// global kill switch and the global autonomy level into one answer. Pins the precedence a later action
/// dispatcher relies on: the kill switch and a disabled kind both force EffectiveMaxAction back to Hint
/// while leaving the configured value visible, a group-scoped rule beats the installation-wide one, an
/// unconfigured kind resolves to the fail-safe defaults, and an unparseable kill-switch or autonomy-level
/// value does not silently unlock anything. Every test other than the dedicated autonomy-level ones pins
/// the global level to Autonomous (2, maps to Execute) so it imposes no cap and the tests keep isolating
/// the dimension they were written for.
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
        GivenGlobalAutonomyLevel("2");

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

    private void GivenGlobalAutonomyLevel(string? value)
        => _settingsReader.GetSetting(SettingKeys.KlacksyProactiveAutonomyLevel)
            .Returns(value is null
                ? null
                : new Klacks.Api.Domain.Models.Settings.Settings
                {
                    Type = SettingKeys.KlacksyProactiveAutonomyLevel,
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

    [Test]
    public async Task GetGlobalAutonomyLevelAsync_WithoutAStoredValue_DefaultsToPropose()
    {
        // Arrange
        GivenGlobalAutonomyLevel(null);

        // Act
        var level = await _sut.GetGlobalAutonomyLevelAsync(CancellationToken.None);

        // Assert
        level.ShouldBe(ProactiveGovernanceDefaults.GlobalAutonomyLevel);
        level.ShouldBe(AutonomyLevel.Propose);
    }

    [Test]
    public async Task GetGlobalAutonomyLevelAsync_WithUnparseableValue_DefaultsToPropose()
    {
        // Arrange
        GivenGlobalAutonomyLevel("nonsense");

        // Act
        var level = await _sut.GetGlobalAutonomyLevelAsync(CancellationToken.None);

        // Assert
        level.ShouldBe(AutonomyLevel.Propose);
    }

    [Test]
    public async Task ResolveAsync_WithoutAGlobalAutonomyLevelSet_CapsEveryConfiguredMaxActionAtHint()
    {
        // Arrange - the fail-safe default (Propose/0) must cap even an Execute governance row, exactly
        // like a fresh installation that nobody has opted into autonomy for yet.
        GivenKillSwitch("false");
        GivenGlobalAutonomyLevel(null);
        GivenStoredRule(new AgentTriggerGovernance
        {
            TriggerKind = TriggerKind,
            MaxAction = ProactiveMaxAction.Execute,
        });

        // Act
        var decision = await _sut.ResolveAsync(TriggerKind, null, CancellationToken.None);

        // Assert
        decision.EffectiveMaxAction.ShouldBe(ProactiveMaxAction.Hint);
        decision.ConfiguredMaxAction.ShouldBe(ProactiveMaxAction.Execute);
        decision.GlobalAutonomyCap.ShouldBe(ProactiveMaxAction.Hint);
    }

    private static readonly (AutonomyLevel Level, ProactiveMaxAction Governance, bool KillSwitch, bool Enabled,
        ProactiveMaxAction Expected)[] ResolverMatrixCases = BuildResolverMatrixCases();

    private static (AutonomyLevel, ProactiveMaxAction, bool, bool, ProactiveMaxAction)[] BuildResolverMatrixCases()
    {
        var levels = new[]
        {
            AutonomyLevel.Propose, AutonomyLevel.Assisted, AutonomyLevel.Autonomous, AutonomyLevel.FullyAutonomous
        };
        var governances = new[] { ProactiveMaxAction.Hint, ProactiveMaxAction.Prepare, ProactiveMaxAction.Execute };
        var killSwitchStates = new[] { false, true };
        var enabledStates = new[] { true, false };

        var cases = new List<(AutonomyLevel, ProactiveMaxAction, bool, bool, ProactiveMaxAction)>();
        foreach (var level in levels)
        {
            foreach (var governance in governances)
            {
                foreach (var killSwitch in killSwitchStates)
                {
                    foreach (var enabled in enabledStates)
                    {
                        var expected = ExpectedEffectiveMaxAction(level, governance, killSwitch, enabled);
                        cases.Add((level, governance, killSwitch, enabled, expected));
                    }
                }
            }
        }

        return cases.ToArray();
    }

    // Independent re-implementation of the spec's formula (kept deliberately separate from the
    // production mapping so a bug in ProactiveGovernanceDefaults.MapAutonomyLevel is caught, not
    // mirrored) - "kill switch or disabled pins Hint, otherwise the lower of the stored governance and
    // 0->Hint/1->Prepare/2->Execute/3->Execute".
    private static ProactiveMaxAction ExpectedEffectiveMaxAction(
        AutonomyLevel level, ProactiveMaxAction governance, bool killSwitch, bool enabled)
    {
        if (killSwitch || !enabled)
        {
            return ProactiveMaxAction.Hint;
        }

        var levelCap = level switch
        {
            AutonomyLevel.Propose => ProactiveMaxAction.Hint,
            AutonomyLevel.Assisted => ProactiveMaxAction.Prepare,
            AutonomyLevel.Autonomous => ProactiveMaxAction.Execute,
            AutonomyLevel.FullyAutonomous => ProactiveMaxAction.Execute,
            _ => ProactiveMaxAction.Hint
        };

        return governance < levelCap ? governance : levelCap;
    }

    [Test]
    public async Task ResolveAsync_ResolverMatrix_MatchesSpecFormulaForAllFortyEightCells()
    {
        // Assert - 4 levels x 3 governance values x 2 kill-switch states x 2 enabled states = 48 cells,
        // matching Testspezifikation §3.1. One assertion loop rather than 48 [TestCase] attributes so a
        // failing cell reports exactly which combination broke without hand-maintaining the cross product.
        ResolverMatrixCases.Length.ShouldBe(48);

        var failures = new List<string>();
        foreach (var (level, governance, killSwitch, enabled, expected) in ResolverMatrixCases)
        {
            GivenKillSwitch(killSwitch.ToString());
            GivenGlobalAutonomyLevel(((int)level).ToString());
            GivenStoredRule(new AgentTriggerGovernance
            {
                TriggerKind = TriggerKind,
                MaxAction = governance,
                Enabled = enabled,
            });

            var decision = await _sut.ResolveAsync(TriggerKind, null, CancellationToken.None);

            if (decision.EffectiveMaxAction != expected)
            {
                failures.Add(
                    $"Level={level} Governance={governance} KillSwitch={killSwitch} Enabled={enabled}: "
                    + $"expected {expected}, got {decision.EffectiveMaxAction}");
            }
        }

        failures.ShouldBeEmpty();
    }
}
