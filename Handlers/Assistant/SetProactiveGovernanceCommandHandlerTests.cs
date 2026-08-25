// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for SetProactiveGovernanceCommandHandler. The centre of gravity is the rule that from
/// MaxAction Prepare upwards a responsible owner must be named: it is checked against the MERGED row,
/// so raising MaxAction on an ownerless row and clearing the owner of a row that already sits at
/// Prepare both have to fail. Also covers patch semantics (an unsupplied field keeps its stored
/// value), the kill switch reaching the plain settings row, rejection of ungoverned kinds, and the
/// refusal of an owner id that resolves to no user.
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
using Klacks.Api.Domain.Models.Authentification;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private UserManager<AppUser> _userManager = null!;
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

        var userStore = Substitute.For<IUserStore<AppUser>>();
        var identityOptions = Substitute.For<IOptions<IdentityOptions>>();
        var passwordHasher = Substitute.For<IPasswordHasher<AppUser>>();
        var userValidators = new List<IUserValidator<AppUser>>();
        var passwordValidators = new List<IPasswordValidator<AppUser>>();
        var keyNormalizer = Substitute.For<ILookupNormalizer>();
        var errors = Substitute.For<IdentityErrorDescriber>();
        var services = Substitute.For<IServiceProvider>();
        var logger = Substitute.For<ILogger<UserManager<AppUser>>>();

        _userManager = Substitute.For<UserManager<AppUser>>(
            userStore, identityOptions, passwordHasher, userValidators, passwordValidators,
            keyNormalizer, errors, services, logger);

        _resolver.ResolveAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ProactiveGovernanceDecision>());

        _sut = new SetProactiveGovernanceCommandHandler(
            _repository, _settingsRepository, _unitOfWork, _resolver, _userManager);
    }

    [TearDown]
    public void TearDown()
    {
        _userManager.Dispose();
    }

    private static SetProactiveGovernanceCommand Command(
        string? triggerKind = GovernedKind,
        ProactiveMaxAction? maxAction = null,
        bool? enabled = null,
        Guid? responsibleOwnerUserId = null,
        bool clearResponsibleOwner = false,
        int? dailyActionBudget = null,
        int? windowActionLimit = null,
        int? windowMinutes = null,
        bool? killSwitch = null)
        => new(
            triggerKind, null, maxAction, enabled, responsibleOwnerUserId, clearResponsibleOwner,
            dailyActionBudget, windowActionLimit, windowMinutes, killSwitch);

    private void GivenExistingRule(AgentTriggerGovernance rule)
        => _repository.FindAsync(rule.TriggerKind, rule.GroupId, Arg.Any<CancellationToken>())
            .Returns(rule);

    private void GivenOwnerExists(Guid ownerUserId)
        => _userManager.FindByIdAsync(ownerUserId.ToString()).Returns(new AppUser());

    [Test]
    public async Task Handle_RaisingMaxActionToPrepareWithoutAnOwner_Throws()
    {
        // Arrange
        var command = Command(maxAction: ProactiveMaxAction.Prepare);

        // Act
        var act = async () => await _sut.Handle(command, CancellationToken.None);

        // Assert
        var exception = await Should.ThrowAsync<InvalidRequestException>(act);
        exception.Message.ShouldContain("responsible owner");
        await _repository.DidNotReceive()
            .UpsertAsync(Arg.Any<AgentTriggerGovernance>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_RaisingMaxActionToExecuteWithoutAnOwner_Throws()
    {
        // Arrange
        var command = Command(maxAction: ProactiveMaxAction.Execute);

        // Act
        var act = async () => await _sut.Handle(command, CancellationToken.None);

        // Assert
        await Should.ThrowAsync<InvalidRequestException>(act);
    }

    [Test]
    public async Task Handle_ClearingTheOwnerOfAStoredPrepareRule_Throws()
    {
        // Arrange
        GivenExistingRule(new AgentTriggerGovernance
        {
            TriggerKind = GovernedKind,
            MaxAction = ProactiveMaxAction.Prepare,
            ResponsibleOwnerUserId = Guid.NewGuid()
        });
        var command = Command(clearResponsibleOwner: true);

        // Act
        var act = async () => await _sut.Handle(command, CancellationToken.None);

        // Assert
        await Should.ThrowAsync<InvalidRequestException>(act);
        await _repository.DidNotReceive()
            .UpsertAsync(Arg.Any<AgentTriggerGovernance>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_PrepareWithAnOwnerThatDoesNotExist_Throws()
    {
        // Arrange
        var ownerUserId = Guid.NewGuid();
        _userManager.FindByIdAsync(ownerUserId.ToString()).Returns((AppUser?)null);
        var command = Command(maxAction: ProactiveMaxAction.Prepare, responsibleOwnerUserId: ownerUserId);

        // Act
        var act = async () => await _sut.Handle(command, CancellationToken.None);

        // Assert
        var exception = await Should.ThrowAsync<InvalidRequestException>(act);
        exception.Message.ShouldContain("does not exist");
    }

    [Test]
    public async Task Handle_PrepareWithAnExistingOwner_IsStored()
    {
        // Arrange
        var ownerUserId = Guid.NewGuid();
        GivenOwnerExists(ownerUserId);
        var command = Command(maxAction: ProactiveMaxAction.Prepare, responsibleOwnerUserId: ownerUserId);

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        await _repository.Received(1).UpsertAsync(
            Arg.Is<AgentTriggerGovernance>(rule =>
                rule.TriggerKind == GovernedKind
                && rule.MaxAction == ProactiveMaxAction.Prepare
                && rule.ResponsibleOwnerUserId == ownerUserId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_HintNeedsNoOwner()
    {
        // Arrange
        var command = Command(maxAction: ProactiveMaxAction.Hint);

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        await _repository.Received(1).UpsertAsync(
            Arg.Is<AgentTriggerGovernance>(rule => rule.ResponsibleOwnerUserId == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_UnsuppliedFields_KeepTheirStoredValues()
    {
        // Arrange
        var ownerUserId = Guid.NewGuid();
        GivenExistingRule(new AgentTriggerGovernance
        {
            TriggerKind = GovernedKind,
            MaxAction = ProactiveMaxAction.Hint,
            Enabled = false,
            ResponsibleOwnerUserId = ownerUserId,
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
                && rule.ResponsibleOwnerUserId == ownerUserId
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
