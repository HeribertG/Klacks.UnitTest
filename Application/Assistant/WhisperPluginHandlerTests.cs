// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Assistant;

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Update;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Update;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class WhisperPluginHandlerTests
{
    private IUpdateHistoryRepository _updateRepository = null!;
    private ICustomSttProviderRepository _providerRepository = null!;
    private ISettingsRepository _settingsRepository = null!;
    private IUnitOfWork _unitOfWork = null!;

    [SetUp]
    public void SetUp()
    {
        _updateRepository = Substitute.For<IUpdateHistoryRepository>();
        _providerRepository = Substitute.For<ICustomSttProviderRepository>();
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
    }

    private InstallWhisperPluginCommandHandler CreateInstallHandler() => new(_updateRepository);

    private UninstallWhisperPluginCommandHandler CreateUninstallHandler() =>
        new(_updateRepository, _providerRepository, _settingsRepository, _unitOfWork);

    private GetWhisperPluginStatusQueryHandler CreateStatusHandler() =>
        new(_providerRepository, _updateRepository);

    private static CustomSttProvider EnabledProvider(string modelId = WhisperPluginConstants.LargeModelId) => new()
    {
        Id = WhisperPluginConstants.ProviderId,
        Name = WhisperPluginConstants.ProviderName,
        ApiUrl = WhisperPluginConstants.ProviderApiUrl,
        LanguageModel = modelId,
        IsEnabled = true,
        IsSystem = true,
    };

    private static UpdateHistory Operation(UpdateOperationType type, UpdateOperationStatus status) => new()
    {
        Id = Guid.NewGuid(),
        OperationType = type,
        Status = status,
        TargetVersion = WhisperPluginConstants.ModelAliasLarge,
        RequestedAt = DateTime.UtcNow,
    };

    [Test]
    public async Task Install_rejects_unknown_model_alias()
    {
        var result = await CreateInstallHandler().Handle(new InstallWhisperPluginCommand("gigantic", "admin"), CancellationToken.None);

        result.Enqueued.ShouldBeFalse();
        result.Reason.ShouldBe(WhisperPluginReasons.UnknownModel);
        await _updateRepository.DidNotReceive().AddAsync(Arg.Any<UpdateHistory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Install_rejects_when_operation_active()
    {
        _updateRepository.GetActiveOperationAsync(Arg.Any<CancellationToken>())
            .Returns(Operation(UpdateOperationType.Update, UpdateOperationStatus.Running));

        var result = await CreateInstallHandler().Handle(
            new InstallWhisperPluginCommand(WhisperPluginConstants.ModelAliasLarge, "admin"), CancellationToken.None);

        result.Enqueued.ShouldBeFalse();
        result.Reason.ShouldBe(UpdateReasons.OperationInProgress);
    }

    [Test]
    public async Task Install_enqueues_whisper_install_with_resolved_model_id()
    {
        _updateRepository.GetActiveOperationAsync(Arg.Any<CancellationToken>()).Returns((UpdateHistory?)null);
        UpdateHistory? added = null;
        await _updateRepository.AddAsync(Arg.Do<UpdateHistory>(e => added = e), Arg.Any<CancellationToken>());

        var result = await CreateInstallHandler().Handle(
            new InstallWhisperPluginCommand(WhisperPluginConstants.ModelAliasSmall, "admin"), CancellationToken.None);

        result.Enqueued.ShouldBeTrue();
        result.OperationId.ShouldNotBeNull();
        added.ShouldNotBeNull();
        added!.OperationType.ShouldBe(UpdateOperationType.WhisperInstall);
        added.Status.ShouldBe(UpdateOperationStatus.Pending);
        added.TargetVersion.ShouldBe(WhisperPluginConstants.ModelAliasSmall);
        added.ArtifactRef.ShouldBe(WhisperPluginConstants.SmallModelId);
        added.ContainsMigrations.ShouldBeFalse();
        added.RequestedBy.ShouldBe("admin");
    }

    [Test]
    public async Task Install_maps_db_conflict_to_operation_in_progress()
    {
        _updateRepository.GetActiveOperationAsync(Arg.Any<CancellationToken>()).Returns((UpdateHistory?)null);
        _updateRepository.AddAsync(Arg.Any<UpdateHistory>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateException());

        var result = await CreateInstallHandler().Handle(
            new InstallWhisperPluginCommand(WhisperPluginConstants.ModelAliasLarge, "admin"), CancellationToken.None);

        result.Enqueued.ShouldBeFalse();
        result.Reason.ShouldBe(UpdateReasons.OperationInProgress);
    }

    [Test]
    public async Task Uninstall_rejects_when_not_installed()
    {
        _providerRepository.GetByIdAsync(WhisperPluginConstants.ProviderId, Arg.Any<CancellationToken>())
            .Returns((CustomSttProvider?)null);

        var result = await CreateUninstallHandler().Handle(new UninstallWhisperPluginCommand("admin"), CancellationToken.None);

        result.Enqueued.ShouldBeFalse();
        result.Reason.ShouldBe(WhisperPluginReasons.NotInstalled);
    }

    [Test]
    public async Task Uninstall_rejects_when_provider_disabled()
    {
        var provider = EnabledProvider();
        provider.IsEnabled = false;
        _providerRepository.GetByIdAsync(WhisperPluginConstants.ProviderId, Arg.Any<CancellationToken>()).Returns(provider);

        var result = await CreateUninstallHandler().Handle(new UninstallWhisperPluginCommand("admin"), CancellationToken.None);

        result.Enqueued.ShouldBeFalse();
        result.Reason.ShouldBe(WhisperPluginReasons.NotInstalled);
    }

    [Test]
    public async Task Uninstall_enqueues_and_resets_engine_when_whisper_active()
    {
        _providerRepository.GetByIdAsync(WhisperPluginConstants.ProviderId, Arg.Any<CancellationToken>())
            .Returns(EnabledProvider());
        _updateRepository.GetActiveOperationAsync(Arg.Any<CancellationToken>()).Returns((UpdateHistory?)null);
        var setting = new Klacks.Api.Domain.Models.Settings.Settings
        {
            Type = Klacks.Api.Application.Constants.Settings.ASSISTANT_STT_ENGINE,
            Value = $"{SttProviderConstants.CustomProviderPrefix}{WhisperPluginConstants.ProviderId}",
        };
        _settingsRepository.GetSetting(Klacks.Api.Application.Constants.Settings.ASSISTANT_STT_ENGINE).Returns(setting);

        UpdateHistory? added = null;
        await _updateRepository.AddAsync(Arg.Do<UpdateHistory>(e => added = e), Arg.Any<CancellationToken>());

        var result = await CreateUninstallHandler().Handle(new UninstallWhisperPluginCommand("admin"), CancellationToken.None);

        result.Enqueued.ShouldBeTrue();
        added.ShouldNotBeNull();
        added!.OperationType.ShouldBe(UpdateOperationType.WhisperUninstall);
        setting.Value.ShouldBe(SttProviderConstants.Browser);
        await _settingsRepository.Received(1).PutSetting(setting);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Uninstall_keeps_engine_when_not_whisper()
    {
        _providerRepository.GetByIdAsync(WhisperPluginConstants.ProviderId, Arg.Any<CancellationToken>())
            .Returns(EnabledProvider());
        _updateRepository.GetActiveOperationAsync(Arg.Any<CancellationToken>()).Returns((UpdateHistory?)null);
        var setting = new Klacks.Api.Domain.Models.Settings.Settings
        {
            Type = Klacks.Api.Application.Constants.Settings.ASSISTANT_STT_ENGINE,
            Value = SttProviderConstants.Deepgram,
        };
        _settingsRepository.GetSetting(Klacks.Api.Application.Constants.Settings.ASSISTANT_STT_ENGINE).Returns(setting);

        var result = await CreateUninstallHandler().Handle(new UninstallWhisperPluginCommand("admin"), CancellationToken.None);

        result.Enqueued.ShouldBeTrue();
        setting.Value.ShouldBe(SttProviderConstants.Deepgram);
        await _settingsRepository.DidNotReceive().PutSetting(Arg.Any<Klacks.Api.Domain.Models.Settings.Settings>());
    }

    [Test]
    public async Task Status_reports_not_installed_without_provider()
    {
        _providerRepository.GetByIdAsync(WhisperPluginConstants.ProviderId, Arg.Any<CancellationToken>())
            .Returns((CustomSttProvider?)null);
        _updateRepository.GetActiveOperationAsync(Arg.Any<CancellationToken>()).Returns((UpdateHistory?)null);
        _updateRepository.GetLatestByTypesAsync(Arg.Any<IReadOnlyCollection<UpdateOperationType>>(), Arg.Any<CancellationToken>())
            .Returns((UpdateHistory?)null);

        var result = await CreateStatusHandler().Handle(new GetWhisperPluginStatusQuery(), CancellationToken.None);

        result.Installed.ShouldBeFalse();
        result.ActiveOperation.ShouldBeNull();
        result.LastOperation.ShouldBeNull();
        result.OtherOperationActive.ShouldBeFalse();
    }

    [Test]
    public async Task Status_maps_installed_provider_and_model_alias()
    {
        _providerRepository.GetByIdAsync(WhisperPluginConstants.ProviderId, Arg.Any<CancellationToken>())
            .Returns(EnabledProvider(WhisperPluginConstants.SmallModelId));
        _updateRepository.GetActiveOperationAsync(Arg.Any<CancellationToken>()).Returns((UpdateHistory?)null);
        _updateRepository.GetLatestByTypesAsync(Arg.Any<IReadOnlyCollection<UpdateOperationType>>(), Arg.Any<CancellationToken>())
            .Returns((UpdateHistory?)null);

        var result = await CreateStatusHandler().Handle(new GetWhisperPluginStatusQuery(), CancellationToken.None);

        result.Installed.ShouldBeTrue();
        result.ModelAlias.ShouldBe(WhisperPluginConstants.ModelAliasSmall);
        result.ModelId.ShouldBe(WhisperPluginConstants.SmallModelId);
    }

    [Test]
    public async Task Status_maps_active_whisper_operation()
    {
        _providerRepository.GetByIdAsync(WhisperPluginConstants.ProviderId, Arg.Any<CancellationToken>())
            .Returns((CustomSttProvider?)null);
        var active = Operation(UpdateOperationType.WhisperInstall, UpdateOperationStatus.Running);
        _updateRepository.GetActiveOperationAsync(Arg.Any<CancellationToken>()).Returns(active);
        _updateRepository.GetLatestByTypesAsync(Arg.Any<IReadOnlyCollection<UpdateOperationType>>(), Arg.Any<CancellationToken>())
            .Returns(active);

        var result = await CreateStatusHandler().Handle(new GetWhisperPluginStatusQuery(), CancellationToken.None);

        result.ActiveOperation.ShouldNotBeNull();
        result.ActiveOperation!.OperationType.ShouldBe(nameof(UpdateOperationType.WhisperInstall));
        result.ActiveOperation.Status.ShouldBe(nameof(UpdateOperationStatus.Running));
        result.OtherOperationActive.ShouldBeFalse();
        result.LastOperation.ShouldBeNull();
    }

    [Test]
    public async Task Status_flags_foreign_active_operation()
    {
        _providerRepository.GetByIdAsync(WhisperPluginConstants.ProviderId, Arg.Any<CancellationToken>())
            .Returns((CustomSttProvider?)null);
        _updateRepository.GetActiveOperationAsync(Arg.Any<CancellationToken>())
            .Returns(Operation(UpdateOperationType.Update, UpdateOperationStatus.Running));
        _updateRepository.GetLatestByTypesAsync(Arg.Any<IReadOnlyCollection<UpdateOperationType>>(), Arg.Any<CancellationToken>())
            .Returns((UpdateHistory?)null);

        var result = await CreateStatusHandler().Handle(new GetWhisperPluginStatusQuery(), CancellationToken.None);

        result.ActiveOperation.ShouldBeNull();
        result.OtherOperationActive.ShouldBeTrue();
    }

    [Test]
    public async Task Status_maps_last_terminal_whisper_operation()
    {
        _providerRepository.GetByIdAsync(WhisperPluginConstants.ProviderId, Arg.Any<CancellationToken>())
            .Returns((CustomSttProvider?)null);
        _updateRepository.GetActiveOperationAsync(Arg.Any<CancellationToken>()).Returns((UpdateHistory?)null);
        var failed = Operation(UpdateOperationType.WhisperInstall, UpdateOperationStatus.Failed);
        failed.Message = "Not enough free memory";
        _updateRepository.GetLatestByTypesAsync(Arg.Any<IReadOnlyCollection<UpdateOperationType>>(), Arg.Any<CancellationToken>())
            .Returns(failed);

        var result = await CreateStatusHandler().Handle(new GetWhisperPluginStatusQuery(), CancellationToken.None);

        result.LastOperation.ShouldNotBeNull();
        result.LastOperation!.Status.ShouldBe(nameof(UpdateOperationStatus.Failed));
        result.LastOperation.Message.ShouldBe("Not enough free memory");
    }
}
