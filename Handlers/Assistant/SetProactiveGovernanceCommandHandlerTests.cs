// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for SetProactiveGovernanceCommandHandler. A rule names no person - who releases an Execute
/// rule's remediation is decided per finding by the approval chain - so raising MaxAction needs nothing
/// but the level itself. Also covers patch semantics (an unsupplied field keeps its stored value), the
/// kill switch and the autonomy level reaching the plain settings row, and rejection of ungoverned kinds.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class SetProactiveGovernanceCommandHandlerTests
{
    private const string GovernedKind = "unstaffed_shift";
    private const string UngovernedKind = "curiosity_question";

    private IAgentTriggerGovernanceRepository _repository = null!;
    private ISettingsRepository _settingsRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IProactiveGovernanceResolver _resolver = null!;
    private SetProactiveGovernanceCommandHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IAgentTriggerGovernanceRepository>();
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _resolver = Substitute.For<IProactiveGovernanceResolver>();

        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(callInfo => callInfo.Arg<Func<Task<bool>>>()());

        _resolver.ResolveAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ProactiveGovernanceDecision>());

        _sut = new SetProactiveGovernanceCommandHandler(
            _repository, _settingsRepository, _unitOfWork, _resolver);
    }

    private static SetProactiveGovernanceCommand Command(
        string? triggerKind = GovernedKind,
        ProactiveMaxAction? maxAction = null,
        bool? enabled = null,
        int? dailyActionBudget = null,
        int? windowActionLimit = null,
        int? windowMinutes = null,
        bool? killSwitch = null,
        AutonomyLevel? autonomyLevel = null)
        => new(
            triggerKind, null, maxAction, enabled,
            dailyActionBudget, windowActionLimit, windowMinutes, killSwitch, autonomyLevel);

    private void GivenExistingRule(AgentTriggerGovernance rule)
        => _repository.FindAsync(rule.TriggerKind, rule.GroupId, Arg.Any<CancellationToken>())
            .Returns(rule);

    [TestCase(ProactiveMaxAction.Hint)]
    [TestCase(ProactiveMaxAction.Prepare)]
    [TestCase(ProactiveMaxAction.Execute)]
    public async Task Handle_RaisingMaxAction_IsAccepted(ProactiveMaxAction maxAction)
    {
        // Arrange
        var command = Command(maxAction: maxAction);

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        await _repository.Received(1).UpsertAsync(
            Arg.Is<AgentTriggerGovernance>(rule =>
                rule.TriggerKind == GovernedKind && rule.MaxAction == maxAction),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_UnsuppliedFields_KeepTheirStoredValues()
    {
        // Arrange
        GivenExistingRule(new AgentTriggerGovernance
        {
            TriggerKind = GovernedKind,
            MaxAction = ProactiveMaxAction.Hint,
            Enabled = false,
            DailyActionBudget = 7,
            WindowActionLimit = 2,
            WindowMinutes = 15
        });
        var command = Command(dailyActionBudget: 11);

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        await _repository.Received(1).UpsertAsync(
            Arg.Is<AgentTriggerGovernance>(rule =>
                rule.DailyActionBudget == 11
                && rule.Enabled == false
                && rule.MaxAction == ProactiveMaxAction.Hint
                && rule.WindowActionLimit == 2
                && rule.WindowMinutes == 15),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_KillSwitch_IsWrittenAsAPlainSettingsRow()
    {
        // Arrange
        var command = Command(triggerKind: null, killSwitch: true);

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        await _settingsRepository.Received(1)
            .UpsertSettingAsync(SettingKeys.KlacksyProactiveKillSwitch, "true");
    }

    [Test]
    public async Task Handle_KillSwitchAlone_IsFlushedInsteadOfLeftStaged()
    {
        // Arrange
        var command = Command(triggerKind: null, killSwitch: true);

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        // ISettingsRepository is stage-only: without an explicit commit the row never reaches the
        // database and the switch silently does nothing. Live-verified regression, 2026-08-25.
        await _unitOfWork.Received().CompleteAsync();
    }

    [Test]
    public async Task Handle_WritesRunInsideOneTransaction()
    {
        // Arrange
        var command = Command(maxAction: ProactiveMaxAction.Hint, killSwitch: false);

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        // The stage-only settings repository and the self-committing governance repository must not be
        // mixed unguarded; the transaction is what keeps the combined write atomic.
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>());
    }

    [Test]
    public async Task Handle_UngovernedTriggerKind_Throws()
    {
        // Arrange
        var command = Command(triggerKind: UngovernedKind);

        // Act
        var act = async () => await _sut.Handle(command, CancellationToken.None);

        // Assert
        await Should.ThrowAsync<InvalidRequestException>(act);
    }

    [Test]
    public async Task Handle_AutonomyLevelAlone_IsWrittenAsAPlainSettingsRowAndFlushed()
    {
        // Arrange
        var command = Command(triggerKind: null, autonomyLevel: AutonomyLevel.Autonomous);

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        // Same stage-only trap as the kill switch: without CompleteAsync the level row would never
        // reach the database.
        await _settingsRepository.Received(1)
            .UpsertSettingAsync(SettingKeys.KlacksyProactiveAutonomyLevel, "2");
        await _unitOfWork.Received().CompleteAsync();
        await _repository.DidNotReceive()
            .UpsertAsync(Arg.Any<AgentTriggerGovernance>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_AutonomyLevelOutsideTheEnum_Throws()
    {
        // Arrange
        var command = Command(triggerKind: null, autonomyLevel: (AutonomyLevel)4);

        // Act
        var act = async () => await _sut.Handle(command, CancellationToken.None);

        // Assert
        await Should.ThrowAsync<InvalidRequestException>(act);
        await _settingsRepository.DidNotReceive()
            .UpsertSettingAsync(SettingKeys.KlacksyProactiveAutonomyLevel, Arg.Any<string>());
    }

    [Test]
    public async Task Handle_AutonomyLevelWrite_RunsInsideTheSameTransaction()
    {
        // Arrange
        var command = Command(triggerKind: null, killSwitch: false, autonomyLevel: AutonomyLevel.Assisted);

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        // The stage-only settings repository and the self-committing governance repository must not be
        // mixed unguarded; the transaction is what keeps the combined write atomic.
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>());
        await _settingsRepository.Received(1)
            .UpsertSettingAsync(SettingKeys.KlacksyProactiveAutonomyLevel, "1");
    }

    [Test]
    public async Task Handle_WithNeitherKindNorKillSwitch_Throws()
    {
        // Arrange
        var command = Command(triggerKind: null);

        // Act
        var act = async () => await _sut.Handle(command, CancellationToken.None);

        // Assert
        await Should.ThrowAsync<InvalidRequestException>(act);
    }

    [Test]
    public async Task Handle_NegativeBudget_Throws()
    {
        // Arrange
        var command = Command(dailyActionBudget: -1);

        // Act
        var act = async () => await _sut.Handle(command, CancellationToken.None);

        // Assert
        await Should.ThrowAsync<InvalidRequestException>(act);
    }

    [Test]
    public async Task Handle_ZeroWindowMinutes_Throws()
    {
        // Arrange
        var command = Command(windowMinutes: 0);

        // Act
        var act = async () => await _sut.Handle(command, CancellationToken.None);

        // Assert
        await Should.ThrowAsync<InvalidRequestException>(act);
    }
}
