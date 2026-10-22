// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for InstallWhisperPluginSkill: it accepts all three engine aliases (small, large-v3-turbo,
/// parakeet-v3), queues the install command with the alias, rejects an unknown alias with an error that
/// names every supported alias, surfaces a rejected enqueue as an error, picks the default engine from the
/// installation language and always lists the Parakeet language coverage in its success message.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Update;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Interfaces.Update;
using Klacks.Api.Domain.Models.Update;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Application.Skills;

[TestFixture]
public class InstallWhisperPluginSkillTests
{
    private const string ModelAliasParameter = "modelAlias";

    private IMediator _mediator = null!;
    private IInstallationIdentityProvider _installation = null!;
    private IInstallationLanguageResolver _languageResolver = null!;
    private InstallWhisperPluginSkill _skill = null!;

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "admin",
        UserPermissions = new List<string>()
    };

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _installation = Substitute.For<IInstallationIdentityProvider>();
        _installation.Get().Returns(new InstallationIdentity(InstallationKind.Docker, "linux-x64"));
        _languageResolver = Substitute.For<IInstallationLanguageResolver>();
        _languageResolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns("de");
        _skill = new InstallWhisperPluginSkill(_mediator, _installation, _languageResolver);
    }

    private void EnqueueSucceeds() =>
        _mediator.Send(Arg.Any<InstallWhisperPluginCommand>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateTriggerResult { Enqueued = true, OperationId = Guid.NewGuid() });

    [TestCase(InstallationKind.Docker, "de", WhisperPluginConstants.ModelAliasParakeet)]
    [TestCase(InstallationKind.Docker, "de-CH", WhisperPluginConstants.ModelAliasParakeet)]
    [TestCase(InstallationKind.Docker, "ja", WhisperPluginConstants.ModelAliasLarge)]
    [TestCase(InstallationKind.Docker, "zh-CN", WhisperPluginConstants.ModelAliasLarge)]
    [TestCase(InstallationKind.Native, "de", WhisperPluginConstants.ModelAliasParakeet)]
    [TestCase(InstallationKind.Native, "ja", WhisperPluginConstants.ModelAliasParakeet)]
    public async Task ExecuteAsync_WithoutAlias_PicksTheEngineFromTheInstallationLanguage(
        InstallationKind kind, string language, string expectedAlias)
    {
        _installation.Get().Returns(new InstallationIdentity(kind, "rid"));
        _languageResolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns(language);
        EnqueueSucceeds();

        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        await _mediator.Received(1).Send(
            Arg.Is<InstallWhisperPluginCommand>(c => c.ModelAlias == expectedAlias), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_ExplicitAlias_WinsOverTheInstallationLanguage()
    {
        _languageResolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns("ja");
        EnqueueSucceeds();

        await _skill.ExecuteAsync(
            Context(), new Dictionary<string, object> { [ModelAliasParameter] = WhisperPluginConstants.ModelAliasSmall });

        await _mediator.Received(1).Send(
            Arg.Is<InstallWhisperPluginCommand>(c => c.ModelAlias == WhisperPluginConstants.ModelAliasSmall),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_ListsTheParakeetCoverageInEverySuccessMessage()
    {
        EnqueueSucceeds();

        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object>());

        foreach (var language in ParakeetLanguageCoverage.Languages)
        {
            result.Message.ShouldContain(language);
        }
    }

    [Test]
    public async Task ExecuteAsync_ParakeetForAnUncoveredLanguage_WarnsAndRecommendsACloudProvider()
    {
        _installation.Get().Returns(new InstallationIdentity(InstallationKind.Native, "rid"));
        _languageResolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns("ja");
        EnqueueSucceeds();

        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object>());

        result.Message.ShouldContain("'ja'");
        result.Message.ShouldContain("not supported");
        result.Message.ShouldContain("cloud");
    }

    [Test]
    public async Task ExecuteAsync_ParakeetForACoveredLanguage_HasNoWarning()
    {
        EnqueueSucceeds();

        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object>());

        result.Message.ShouldNotContain("not supported");
        result.Message.ShouldNotContain("Whisper was chosen");
    }

    [Test]
    public async Task ExecuteAsync_DefaultWhisperForAnUncoveredLanguage_ExplainsTheChoice()
    {
        _languageResolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns("ja");
        EnqueueSucceeds();

        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object>());

        result.Message.ShouldContain("Whisper was chosen");
        result.Message.ShouldContain("'ja'");
    }

    [Test]
    public async Task ExecuteAsync_SuccessMessagePointsToTheLocalEngineSetting()
    {
        _mediator.Send(Arg.Any<InstallWhisperPluginCommand>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateTriggerResult { Enqueued = true, OperationId = Guid.NewGuid() });

        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object>());

        result.Message.ShouldContain("update_speech_settings");
        result.Message.ShouldContain($"\"{WhisperPluginConstants.LocalEngineValue}\"");
    }

    [Test]
    public async Task ExecuteAsync_ReportsUnsupportedOnInstallationAsError()
    {
        _mediator.Send(Arg.Any<InstallWhisperPluginCommand>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateTriggerResult { Enqueued = false, Reason = WhisperPluginReasons.UnsupportedOnInstallation });

        var result = await _skill.ExecuteAsync(
            Context(), new Dictionary<string, object> { [ModelAliasParameter] = WhisperPluginConstants.ModelAliasLarge });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(WhisperPluginReasons.UnsupportedOnInstallation);
    }

    [TestCase(WhisperPluginConstants.ModelAliasSmall)]
    [TestCase(WhisperPluginConstants.ModelAliasLarge)]
    [TestCase(WhisperPluginConstants.ModelAliasParakeet)]
    public async Task ExecuteAsync_QueuesInstallForEverySupportedAlias(string alias)
    {
        _mediator.Send(Arg.Any<InstallWhisperPluginCommand>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateTriggerResult { Enqueued = true, OperationId = Guid.NewGuid() });

        var result = await _skill.ExecuteAsync(
            Context(), new Dictionary<string, object> { [ModelAliasParameter] = alias });

        result.Success.ShouldBeTrue();
        await _mediator.Received(1).Send(
            Arg.Is<InstallWhisperPluginCommand>(c => c.ModelAlias == alias), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_RejectsUnknownAliasAndListsAllThreeAliases()
    {
        var result = await _skill.ExecuteAsync(
            Context(), new Dictionary<string, object> { [ModelAliasParameter] = "gigantic" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain(WhisperPluginConstants.ModelAliasSmall);
        result.Message.ShouldContain(WhisperPluginConstants.ModelAliasLarge);
        result.Message.ShouldContain(WhisperPluginConstants.ModelAliasParakeet);
        await _mediator.DidNotReceiveWithAnyArgs().Send<UpdateTriggerResult>(default!, default);
    }

    [Test]
    public async Task ExecuteAsync_ReportsRejectedEnqueueAsError()
    {
        _mediator.Send(Arg.Any<InstallWhisperPluginCommand>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateTriggerResult { Enqueued = false, Reason = WhisperPluginReasons.UnknownModel });

        var result = await _skill.ExecuteAsync(
            Context(), new Dictionary<string, object> { [ModelAliasParameter] = WhisperPluginConstants.ModelAliasParakeet });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(WhisperPluginReasons.UnknownModel);
    }
}
