// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests that the skills which write operator-paid keys or reach configured external servers refuse to run on the
/// public Playground: with the mode on each answers an English error result before it touches a repository or service,
/// with the mode off it runs its normal path exactly as before.
/// </summary>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs.Email;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Domain.Interfaces.Translation;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundBlockedSkillsTests
{
    private IPlaygroundMode _mode = null!;
    private ISettingsRepository _settings = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ISettingsEncryptionService _encryption = null!;

    [SetUp]
    public void SetUp()
    {
        _mode = Substitute.For<IPlaygroundMode>();
        _settings = Substitute.For<ISettingsRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _encryption = Substitute.For<ISettingsEncryptionService>();
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Roles.Admin },
    };

    private static void ShouldBeBlocked(SkillResult result)
    {
        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(PlaygroundHardeningConstants.FeatureBlockedMessage);
    }

    private static void ShouldNotBeBlocked(SkillResult result) =>
        result.Message.ShouldNotBe(PlaygroundHardeningConstants.FeatureBlockedMessage);

    private static Dictionary<string, object> Params(params (string Key, object Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value);

    [Test]
    public async Task UpdateSpeechSettings_ModeOn_IsRefusedWithoutWriting()
    {
        _mode.IsActive.Returns(true);
        var resolver = Substitute.For<ICustomSttProviderResolver>();
        var skill = new UpdateSpeechSettingsSkill(_settings, _unitOfWork, _encryption, resolver, _mode);

        var result = await skill.ExecuteAsync(Ctx(), Params(("deepgramApiKey", "secret"), ("sttEngine", "deepgram")));

        ShouldBeBlocked(result);
        _settings.ReceivedCalls().ShouldBeEmpty();
        _unitOfWork.ReceivedCalls().ShouldBeEmpty();
        _encryption.ReceivedCalls().ShouldBeEmpty();
        resolver.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task UpdateSpeechSettings_ModeOff_RunsItsNormalPath()
    {
        _mode.IsActive.Returns(false);
        var skill = new UpdateSpeechSettingsSkill(_settings, _unitOfWork, _encryption, Substitute.For<ICustomSttProviderResolver>(), _mode);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        ShouldNotBeBlocked(result);
    }

    [Test]
    public async Task UpdateWebSearchSettings_ModeOn_IsRefusedWithoutWriting()
    {
        _mode.IsActive.Returns(true);
        var skill = new UpdateWebSearchSettingsSkill(_settings, _unitOfWork, _encryption, _mode);

        var result = await skill.ExecuteAsync(Ctx(), Params(("apiKey", "secret"), ("provider", "serper")));

        ShouldBeBlocked(result);
        _settings.ReceivedCalls().ShouldBeEmpty();
        _unitOfWork.ReceivedCalls().ShouldBeEmpty();
        _encryption.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task UpdateWebSearchSettings_ModeOff_RunsItsNormalPath()
    {
        _mode.IsActive.Returns(false);
        var skill = new UpdateWebSearchSettingsSkill(_settings, _unitOfWork, _encryption, _mode);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        ShouldNotBeBlocked(result);
        result.Message.ShouldBe("No web search settings parameters provided to update.");
    }

    [Test]
    public async Task UpdateOpenRouteSettings_ModeOn_IsRefusedWithoutWriting()
    {
        _mode.IsActive.Returns(true);
        var skill = new UpdateOpenRouteSettingsSkill(_settings, _unitOfWork, _encryption, _mode);

        var result = await skill.ExecuteAsync(Ctx(), Params(("apiKey", "secret")));

        ShouldBeBlocked(result);
        _settings.ReceivedCalls().ShouldBeEmpty();
        _unitOfWork.ReceivedCalls().ShouldBeEmpty();
        _encryption.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task UpdateOpenRouteSettings_ModeOff_RunsItsNormalPath()
    {
        _mode.IsActive.Returns(false);
        var skill = new UpdateOpenRouteSettingsSkill(_settings, _unitOfWork, _encryption, _mode);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        ShouldNotBeBlocked(result);
    }

    [Test]
    public async Task TranslateEmail_ModeOn_IsRefusedWithoutCallingTheTranslationService()
    {
        _mode.IsActive.Returns(true);
        var mediator = Substitute.For<IMediator>();
        var translation = Substitute.For<ITranslationService>();
        var skill = new TranslateEmailSkill(mediator, translation, _mode);

        var result = await skill.ExecuteAsync(Ctx(), Params(("emailId", Guid.NewGuid().ToString()), ("targetLanguage", "de")));

        ShouldBeBlocked(result);
        translation.ReceivedCalls().ShouldBeEmpty();
        mediator.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task TranslateEmail_ModeOff_RunsItsNormalPath()
    {
        _mode.IsActive.Returns(false);
        var translation = Substitute.For<ITranslationService>();
        translation.IsConfiguredAsync().Returns(false);
        var skill = new TranslateEmailSkill(Substitute.For<IMediator>(), translation, _mode);

        var result = await skill.ExecuteAsync(Ctx(), Params(("emailId", Guid.NewGuid().ToString()), ("targetLanguage", "de")));

        ShouldNotBeBlocked(result);
        await translation.Received(1).IsConfiguredAsync();
    }

    [Test]
    public async Task TestImapConnection_ModeOn_IsRefusedWithoutReadingSettingsOrConnecting()
    {
        _mode.IsActive.Returns(true);
        var imap = Substitute.For<IImapTestService>();
        var skill = new TestImapConnectionSkill(_settings, imap, _mode);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        ShouldBeBlocked(result);
        _settings.ReceivedCalls().ShouldBeEmpty();
        imap.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task TestImapConnection_ModeOff_RunsItsNormalPath()
    {
        _mode.IsActive.Returns(false);
        var skill = new TestImapConnectionSkill(_settings, Substitute.For<IImapTestService>(), _mode);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        ShouldNotBeBlocked(result);
        _settings.ReceivedCalls().ShouldNotBeEmpty();
    }

    [Test]
    public async Task TestSmtpConnection_ModeOn_IsRefusedWithoutReadingSettingsOrConnecting()
    {
        _mode.IsActive.Returns(true);
        var smtp = Substitute.For<IEmailTestService>();
        var skill = new TestSmtpConnectionSkill(_settings, smtp, _mode);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        ShouldBeBlocked(result);
        _settings.ReceivedCalls().ShouldBeEmpty();
        smtp.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task TestSmtpConnection_ModeOff_RunsItsNormalPath()
    {
        _mode.IsActive.Returns(false);
        var skill = new TestSmtpConnectionSkill(_settings, Substitute.For<IEmailTestService>(), _mode);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        ShouldNotBeBlocked(result);
        _settings.ReceivedCalls().ShouldNotBeEmpty();
    }
}
