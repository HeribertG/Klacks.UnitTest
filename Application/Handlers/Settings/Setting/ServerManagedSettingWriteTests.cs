// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Verifies the always-on deny list of the generic settings write path: AddSetting (PostCommand) and PutSetting
/// (PutCommand) refuse the local voice address and the three licence consent keys with 403 for every caller, also when
/// the body addresses the stored row by id under another Type, and write nothing; other keys still pass; reading a
/// server-managed key stays possible; the generic settings controller offers no delete action at all.
/// </summary>

using System.Reflection;
using Klacks.Api.Application.Commands.Settings.Settings;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces.Klacksy;
using Klacks.Api.Application.Queries.Settings.Settings;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Events;
using Klacks.Api.Domain.Services.Settings;
using Klacks.Api.Presentation.Controllers.UserBackend.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SettingConstants = Klacks.Api.Application.Constants.Settings;
using SettingHandlers = Klacks.Api.Application.Handlers.Settings.Setting;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Handlers.Settings.Setting;

[TestFixture]
public class ServerManagedSettingWriteTests
{
    private static readonly string[] ServerManagedKeys =
    [
        SettingConstants.ASSISTANT_TTS_LOCAL_BASE_URL,
        SettingConstants.ASSISTANT_TTS_LOCAL_LICENSE_ACCEPTED_BY,
        SettingConstants.ASSISTANT_TTS_LOCAL_LICENSE_ACCEPTED_AT,
        SettingConstants.ASSISTANT_TTS_LOCAL_LICENSE_VERSION,
    ];

    private ISettingsRepository _settingsRepository = null!;
    private ISettingsEncryptionService _encryptionService = null!;
    private IUnitOfWork _unitOfWork = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _settingsRepository.AddSetting(Arg.Any<SettingsEntity>()).Returns(ci => ci.Arg<SettingsEntity>());
        _settingsRepository.PutSetting(Arg.Any<SettingsEntity>()).Returns(ci => ci.Arg<SettingsEntity>());
        _encryptionService = Substitute.For<ISettingsEncryptionService>();
        _encryptionService.ProcessForStorage(Arg.Any<string>(), Arg.Any<string>()).Returns(ci => ci.ArgAt<string>(1));
        _unitOfWork = Substitute.For<IUnitOfWork>();
    }

    private SettingHandlers.PostCommandHandler PostHandler() => new(
        _settingsRepository,
        _encryptionService,
        _unitOfWork,
        new SettingValueValidator(),
        Substitute.For<INavigationTargetCacheService>(),
        NullLogger<SettingHandlers.PostCommandHandler>.Instance);

    private SettingHandlers.PutCommandHandler PutHandler() => new(
        _settingsRepository,
        _encryptionService,
        _unitOfWork,
        Substitute.For<IDomainEventDispatcher>(),
        new SettingValueValidator(),
        Substitute.For<INavigationTargetCacheService>(),
        NullLogger<SettingHandlers.PutCommandHandler>.Instance);

    private static SettingsEntity Setting(string type, string value = "http://attacker.example:8000") =>
        new() { Id = Guid.NewGuid(), Type = type, Value = value };

    private async Task NothingWritten()
    {
        await _settingsRepository.DidNotReceive().AddSetting(Arg.Any<SettingsEntity>());
        await _settingsRepository.DidNotReceive().PutSetting(Arg.Any<SettingsEntity>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public void DenyList_HoldsExactlyTheLocalVoiceKeys()
    {
        ServerManagedSettingKeys.Keys.OrderBy(k => k).ShouldBe(ServerManagedKeys.OrderBy(k => k));
    }

    [TestCaseSource(nameof(ServerManagedKeys))]
    public async Task AddSetting_ServerManagedKey_IsForbiddenAndWritesNothing(string key)
    {
        await Should.ThrowAsync<ForbiddenException>(() => PostHandler().Handle(new PostCommand(Setting(key)), CancellationToken.None));

        await NothingWritten();
    }

    [TestCaseSource(nameof(ServerManagedKeys))]
    public async Task PutSetting_ServerManagedKey_IsForbiddenAndWritesNothing(string key)
    {
        await Should.ThrowAsync<ForbiddenException>(() => PutHandler().Handle(new PutCommand(Setting(key)), CancellationToken.None));

        await NothingWritten();
    }

    [Test]
    public async Task PutSetting_KeyInOtherCasing_IsForbidden()
    {
        await Should.ThrowAsync<ForbiddenException>(() => PutHandler().Handle(
            new PutCommand(Setting(" " + SettingConstants.ASSISTANT_TTS_LOCAL_BASE_URL.ToLowerInvariant())), CancellationToken.None));

        await NothingWritten();
    }

    [Test]
    public async Task PutSetting_StoredRowIsServerManagedButBodyNamesAnotherType_IsForbidden()
    {
        var body = Setting(SettingKeys.DefaultLanguage, "fr");
        _settingsRepository.GetSettingByIdNoTracking(body.Id)
            .Returns(new SettingsEntity { Id = body.Id, Type = SettingConstants.ASSISTANT_TTS_LOCAL_BASE_URL, Value = "http://klacks-stt:8000" });

        await Should.ThrowAsync<ForbiddenException>(() => PutHandler().Handle(new PutCommand(body), CancellationToken.None));

        await NothingWritten();
    }

    [Test]
    public async Task AddSetting_StoredRowIsServerManagedButBodyNamesAnotherType_IsForbidden()
    {
        var body = Setting(SettingKeys.DefaultLanguage, "fr");
        _settingsRepository.GetSettingByIdNoTracking(body.Id)
            .Returns(new SettingsEntity { Id = body.Id, Type = SettingConstants.ASSISTANT_TTS_LOCAL_LICENSE_ACCEPTED_BY, Value = "someone" });

        await Should.ThrowAsync<ForbiddenException>(() => PostHandler().Handle(new PostCommand(body), CancellationToken.None));

        await NothingWritten();
    }

    [Test]
    public async Task PutSetting_OrdinaryKey_IsStillWritten()
    {
        await PutHandler().Handle(new PutCommand(Setting(SettingKeys.DefaultLanguage, "fr")), CancellationToken.None);

        await _settingsRepository.Received(1).PutSetting(Arg.Is<SettingsEntity>(s => s.Type == SettingKeys.DefaultLanguage));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task GetSetting_ServerManagedKey_IsStillReadable()
    {
        _settingsRepository.GetSetting(SettingConstants.ASSISTANT_TTS_LOCAL_BASE_URL)
            .Returns(new SettingsEntity { Type = SettingConstants.ASSISTANT_TTS_LOCAL_BASE_URL, Value = "http://klacks-stt:8000" });
        var handler = new SettingHandlers.GetQueryHandler(
            _settingsRepository, _encryptionService, NullLogger<SettingHandlers.GetQueryHandler>.Instance);

        var setting = await handler.Handle(new GetQuery(SettingConstants.ASSISTANT_TTS_LOCAL_BASE_URL), CancellationToken.None);

        setting!.Value.ShouldBe("http://klacks-stt:8000");
    }

    [Test]
    public void GeneralSettingsController_OffersNoDeleteAction()
    {
        typeof(GeneralSettingsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpDeleteAttribute>().Any())
            .ShouldBeEmpty();
    }
}
