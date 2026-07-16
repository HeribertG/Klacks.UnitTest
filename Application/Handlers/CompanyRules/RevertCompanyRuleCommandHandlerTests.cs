// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for <see cref="RevertCompanyRuleCommandHandler"/>: surcharge writes the snapshot values
/// back and skips a snapshot key outside the known surcharge settings, counter rule and custom macro
/// soft-delete the created target and tolerate a target that is already gone but let a genuine delete
/// blocker propagate unchanged without deleting the registry row, and every successful kind soft-deletes
/// the registry row. A missing registry row throws.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.CompanyRules;
using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Handlers.CompanyRules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Infrastructure.Mediator;
using Microsoft.Extensions.Logging;
using MacroCommands = Klacks.Api.Application.Commands.Settings.Macros;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Handlers.CompanyRules;

[TestFixture]
public class RevertCompanyRuleCommandHandlerTests
{
    private ICompanyRuleRepository _registry = null!;
    private ISettingsRepository _settings = null!;
    private IMediator _mediator = null!;
    private IUnitOfWork _unitOfWork = null!;
    private Klacks.Api.Domain.Events.IDomainEventDispatcher _eventDispatcher = null!;
    private RevertCompanyRuleCommandHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _registry = Substitute.For<ICompanyRuleRepository>();
        _settings = Substitute.For<ISettingsRepository>();
        _settings
            .UpsertSettingAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(async callInfo =>
            {
                var type = callInfo.ArgAt<string>(0);
                var value = callInfo.ArgAt<string>(1);
                var existing = await _settings.GetSetting(type);
                if (existing is not null)
                {
                    existing.Value = value;
                    await _settings.PutSetting(existing);
                }
                else
                {
                    await _settings.AddSetting(new SettingsEntity { Id = Guid.NewGuid(), Type = type, Value = value });
                }
            });
        _mediator = Substitute.For<IMediator>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => ci.ArgAt<Func<Task<bool>>>(0)());

        _eventDispatcher = Substitute.For<Klacks.Api.Domain.Events.IDomainEventDispatcher>();

        _sut = new RevertCompanyRuleCommandHandler(
            _registry, _settings, _mediator, _unitOfWork,
            Substitute.For<ILogger<RevertCompanyRuleCommandHandler>>(),
            new SettingsMapper(),
            _eventDispatcher);
    }

    [Test]
    public void Handle_NotFound_Throws()
    {
        _registry.GetAsync(Arg.Any<Guid>()).Returns((CompanyRule?)null);
        Assert.ThrowsAsync<InvalidRequestException>(() => _sut.Handle(new RevertCompanyRuleCommand(Guid.NewGuid()), CancellationToken.None));
    }

    [Test]
    public async Task Handle_Surcharge_RestoresSnapshot_AndDeletesRegistry()
    {
        var id = Guid.NewGuid();
        var snapshot = JsonSerializer.Serialize(new Dictionary<string, string?> { [SettingKeys.NightRate] = "1.25" });
        _registry.GetAsync(id).Returns(new CompanyRule
        {
            Id = id, Name = "Night pay", Kind = CompanyRuleKind.SurchargeSettings,
            TargetEntityType = CompanyRuleTargetEntityTypes.Settings, SettingsSnapshotJson = snapshot
        });
        _settings.GetSetting(SettingKeys.NightRate).Returns(new SettingsEntity { Id = Guid.NewGuid(), Type = SettingKeys.NightRate, Value = "1.5" });

        await _sut.Handle(new RevertCompanyRuleCommand(id), CancellationToken.None);

        await _settings.Received().PutSetting(Arg.Is<SettingsEntity>(s => s.Type == SettingKeys.NightRate && s.Value == "1.25"));
        await _registry.Received(1).DeleteAsync(id);
    }

    [Test]
    public async Task Handle_Surcharge_RestoreChangesRelevantValue_DispatchesSurchargeSettingsChangedEvent()
    {
        var id = Guid.NewGuid();
        var snapshot = JsonSerializer.Serialize(new Dictionary<string, string?> { [SettingKeys.NightRate] = "1.25" });
        _registry.GetAsync(id).Returns(new CompanyRule
        {
            Id = id, Name = "Night pay", Kind = CompanyRuleKind.SurchargeSettings,
            TargetEntityType = CompanyRuleTargetEntityTypes.Settings, SettingsSnapshotJson = snapshot
        });
        _settings.GetSetting(SettingKeys.NightRate).Returns(new SettingsEntity { Id = Guid.NewGuid(), Type = SettingKeys.NightRate, Value = "1.5" });

        await _sut.Handle(new RevertCompanyRuleCommand(id), CancellationToken.None);

        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Is<Klacks.Api.Domain.Events.SurchargeSettingsChangedEvent>(e =>
                e.ChangedKeys.Count == 1 && e.ChangedKeys.Contains(SettingKeys.NightRate)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_Surcharge_RestoreWritesSameValue_DoesNotDispatch()
    {
        var id = Guid.NewGuid();
        var snapshot = JsonSerializer.Serialize(new Dictionary<string, string?> { [SettingKeys.NightRate] = "1.25" });
        _registry.GetAsync(id).Returns(new CompanyRule
        {
            Id = id, Name = "Night pay", Kind = CompanyRuleKind.SurchargeSettings,
            TargetEntityType = CompanyRuleTargetEntityTypes.Settings, SettingsSnapshotJson = snapshot
        });
        _settings.GetSetting(SettingKeys.NightRate).Returns(new SettingsEntity { Id = Guid.NewGuid(), Type = SettingKeys.NightRate, Value = "1.25" });

        await _sut.Handle(new RevertCompanyRuleCommand(id), CancellationToken.None);

        await _eventDispatcher.DidNotReceiveWithAnyArgs().DispatchAsync((Klacks.Api.Domain.Events.IDomainEvent)null!, default);
    }

    [Test]
    public async Task Handle_CounterRule_DeletesTarget_AndRegistry()
    {
        var id = Guid.NewGuid();
        var target = Guid.NewGuid();
        _registry.GetAsync(id).Returns(new CompanyRule
        {
            Id = id, Name = "Night cap", Kind = CompanyRuleKind.CounterRule,
            TargetEntityType = CompanyRuleTargetEntityTypes.CounterRule, TargetEntityId = target
        });
        _mediator.Send(Arg.Any<DeleteCommand<CounterRuleResource>>(), Arg.Any<CancellationToken>())
            .Returns(new CounterRuleResource { Id = target });

        await _sut.Handle(new RevertCompanyRuleCommand(id), CancellationToken.None);

        await _mediator.Received(1).Send(Arg.Is<DeleteCommand<CounterRuleResource>>(c => c.Id == target), Arg.Any<CancellationToken>());
        await _registry.Received(1).DeleteAsync(id);
    }

    [Test]
    public async Task Handle_CounterRule_TargetAlreadyGone_Tolerated()
    {
        var id = Guid.NewGuid();
        var target = Guid.NewGuid();
        _registry.GetAsync(id).Returns(new CompanyRule
        {
            Id = id, Name = "Night cap", Kind = CompanyRuleKind.CounterRule,
            TargetEntityType = CompanyRuleTargetEntityTypes.CounterRule, TargetEntityId = target
        });
        _mediator.Send(Arg.Any<DeleteCommand<CounterRuleResource>>(), Arg.Any<CancellationToken>())
            .Returns<CounterRuleResource?>(_ => throw new KeyNotFoundException("gone"));

        var result = await _sut.Handle(new RevertCompanyRuleCommand(id), CancellationToken.None);

        result.ShouldNotBeNull();
        await _registry.Received(1).DeleteAsync(id);
    }

    [Test]
    public async Task Handle_CustomMacro_DeletesMacro_AndRegistry()
    {
        var id = Guid.NewGuid();
        var target = Guid.NewGuid();
        _registry.GetAsync(id).Returns(new CompanyRule
        {
            Id = id, Name = "Holiday", Kind = CompanyRuleKind.CustomMacro,
            TargetEntityType = CompanyRuleTargetEntityTypes.Macro, TargetEntityId = target
        });
        _mediator.Send(Arg.Any<MacroCommands.DeleteCommand>(), Arg.Any<CancellationToken>())
            .Returns(new MacroResource { Id = target, Name = "Holiday" });

        await _sut.Handle(new RevertCompanyRuleCommand(id), CancellationToken.None);

        await _mediator.Received(1).Send(Arg.Is<MacroCommands.DeleteCommand>(c => c.Id == target), Arg.Any<CancellationToken>());
        await _registry.Received(1).DeleteAsync(id);
    }

    [Test]
    public void Handle_CustomMacro_DeleteThrowsInvalidRequestException_PropagatesUnchanged_RegistryNotDeleted()
    {
        var id = Guid.NewGuid();
        var target = Guid.NewGuid();
        _registry.GetAsync(id).Returns(new CompanyRule
        {
            Id = id, Name = "Holiday", Kind = CompanyRuleKind.CustomMacro,
            TargetEntityType = CompanyRuleTargetEntityTypes.Macro, TargetEntityId = target
        });
        _mediator.Send(Arg.Any<MacroCommands.DeleteCommand>(), Arg.Any<CancellationToken>())
            .Returns<MacroResource>(_ => throw new InvalidRequestException("Macro still referenced by shifts."));

        Assert.ThrowsAsync<InvalidRequestException>(() => _sut.Handle(new RevertCompanyRuleCommand(id), CancellationToken.None));

        _registry.DidNotReceive().DeleteAsync(id);
    }

    [Test]
    public async Task Handle_Surcharge_UnknownSnapshotKey_SkippedWithoutError()
    {
        var id = Guid.NewGuid();
        var snapshot = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            [SettingKeys.NightRate] = "1.25",
            ["not-a-surcharge-setting"] = "whatever"
        });
        _registry.GetAsync(id).Returns(new CompanyRule
        {
            Id = id, Name = "Night pay", Kind = CompanyRuleKind.SurchargeSettings,
            TargetEntityType = CompanyRuleTargetEntityTypes.Settings, SettingsSnapshotJson = snapshot
        });
        _settings.GetSetting(SettingKeys.NightRate).Returns(new SettingsEntity { Id = Guid.NewGuid(), Type = SettingKeys.NightRate, Value = "1.5" });

        await _sut.Handle(new RevertCompanyRuleCommand(id), CancellationToken.None);

        await _settings.Received().PutSetting(Arg.Is<SettingsEntity>(s => s.Type == SettingKeys.NightRate && s.Value == "1.25"));
        await _settings.DidNotReceive().PutSetting(Arg.Is<SettingsEntity>(s => s.Type == "not-a-surcharge-setting"));
        await _settings.DidNotReceive().AddSetting(Arg.Is<SettingsEntity>(s => s.Type == "not-a-surcharge-setting"));
        await _registry.Received(1).DeleteAsync(id);
    }

    [Test]
    public async Task Handle_CustomMacro_TargetAlreadyGone_Tolerated()
    {
        var id = Guid.NewGuid();
        var target = Guid.NewGuid();
        _registry.GetAsync(id).Returns(new CompanyRule
        {
            Id = id, Name = "Holiday", Kind = CompanyRuleKind.CustomMacro,
            TargetEntityType = CompanyRuleTargetEntityTypes.Macro, TargetEntityId = target
        });
        _mediator.Send(Arg.Any<MacroCommands.DeleteCommand>(), Arg.Any<CancellationToken>())
            .Returns<MacroResource>(_ => throw new InvalidOperationException("Macro with ID not found"));

        var result = await _sut.Handle(new RevertCompanyRuleCommand(id), CancellationToken.None);

        result.ShouldNotBeNull();
        await _registry.Received(1).DeleteAsync(id);
    }
}
