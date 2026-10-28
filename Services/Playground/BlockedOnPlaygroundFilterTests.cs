// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for BlockedOnPlaygroundFilter and the endpoints it guards: with the mode off the action always runs; with
/// the mode on the action never runs and the answer is HTTP 403 (not 401, not a redirect) with errorCode
/// PLAYGROUND_FEATURE_BLOCKED. Also pins the attribute on every admin LLM, provider and speech endpoint that spends
/// money or changes the shared setup, and its absence on the read endpoints the chat UI needs at start.
/// </summary>

using System.Reflection;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Presentation.Controllers.Assistant;
using Klacks.Api.Presentation.Controllers.Plugins;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Klacks.Api.Presentation.Controllers.UserBackend.Email;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Klacks.Api.Presentation.Controllers.UserBackend.Settings;
using Klacks.Api.Presentation.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class BlockedOnPlaygroundFilterTests
{
    private IPlaygroundMode _mode = null!;
    private BlockedOnPlaygroundFilter _sut = null!;
    private ActionContext _actionContext = null!;

    [SetUp]
    public void SetUp()
    {
        _mode = Substitute.For<IPlaygroundMode>();
        _sut = new BlockedOnPlaygroundFilter(_mode);
        _actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
    }

    private ActionExecutingContext Executing() =>
        new(_actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());

    private ActionExecutionDelegate Next(Action onRun) => () =>
    {
        onRun();
        return Task.FromResult(new ActionExecutedContext(_actionContext, new List<IFilterMetadata>(), new object()));
    };

    [Test]
    public async Task ModeOn_AnswersForbiddenAndNeverRunsTheAction()
    {
        _mode.IsActive.Returns(true);
        var executing = Executing();
        var ran = false;

        await _sut.OnActionExecutionAsync(executing, Next(() => ran = true));

        ran.ShouldBeFalse();
        var result = executing.Result.ShouldBeOfType<ObjectResult>();
        result.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        var problem = result.Value.ShouldBeOfType<ProblemDetails>();
        problem.Status.ShouldBe(StatusCodes.Status403Forbidden);
        problem.Extensions["errorCode"].ShouldBe(PlaygroundHardeningConstants.FeatureBlockedErrorCode);
        problem.Detail.ShouldBe(PlaygroundHardeningConstants.FeatureBlockedMessage);
    }

    [Test]
    public async Task ModeOn_NeverAnswersUnauthorizedOrARedirect()
    {
        _mode.IsActive.Returns(true);
        var executing = Executing();

        await _sut.OnActionExecutionAsync(executing, Next(() => { }));

        executing.Result.ShouldNotBeOfType<UnauthorizedResult>();
        executing.Result.ShouldNotBeOfType<ForbidResult>();
        executing.Result.ShouldNotBeOfType<ChallengeResult>();
        executing.Result.ShouldNotBeOfType<RedirectResult>();
    }

    [Test]
    public async Task ModeOff_RunsTheActionAndSetsNoResult()
    {
        _mode.IsActive.Returns(false);
        var executing = Executing();
        var ran = false;

        await _sut.OnActionExecutionAsync(executing, Next(() => ran = true));

        ran.ShouldBeTrue();
        executing.Result.ShouldBeNull();
    }

    private static bool IsBlocked(Type controller, string method) =>
        controller.GetMethod(method, BindingFlags.Public | BindingFlags.Instance)!
            .GetCustomAttribute<BlockedOnPlaygroundAttribute>() is not null;

    [TestCase(typeof(ModelsController), nameof(ModelsController.CheckSpeechModels))]
    [TestCase(typeof(ModelsController), nameof(ModelsController.OptimizeForKlacksy))]
    [TestCase(typeof(ModelsController), nameof(ModelsController.EnableModel))]
    [TestCase(typeof(ModelsController), nameof(ModelsController.DisableModel))]
    [TestCase(typeof(ModelsController), nameof(ModelsController.SetDefaultModel))]
    [TestCase(typeof(ModelsController), nameof(ModelsController.CreateModel))]
    [TestCase(typeof(ModelsController), nameof(ModelsController.UpdateModel))]
    [TestCase(typeof(ModelsController), nameof(ModelsController.DeleteModel))]
    [TestCase(typeof(ProvidersController), nameof(ProvidersController.DiscoverProviders))]
    [TestCase(typeof(ProvidersController), nameof(ProvidersController.CreateProvider))]
    [TestCase(typeof(ProvidersController), nameof(ProvidersController.UpdateProvider))]
    [TestCase(typeof(ProvidersController), nameof(ProvidersController.DeleteProvider))]
    [TestCase(typeof(ModelSyncController), nameof(ModelSyncController.TriggerSync))]
    [TestCase(typeof(EvalController), nameof(EvalController.Run))]
    [TestCase(typeof(EvalController), nameof(EvalController.RunTurn))]
    [TestCase(typeof(SttController), nameof(SttController.GetProviders))]
    [TestCase(typeof(SttController), nameof(SttController.TestConnection))]
    [TestCase(typeof(SttController), nameof(SttController.Stream))]
    [TestCase(typeof(SttController), nameof(SttController.Transcribe))]
    [TestCase(typeof(CustomSttProviderController), nameof(CustomSttProviderController.Create))]
    [TestCase(typeof(CustomSttProviderController), nameof(CustomSttProviderController.Update))]
    [TestCase(typeof(CustomSttProviderController), nameof(CustomSttProviderController.Delete))]
    [TestCase(typeof(KlacksyLearningController), nameof(KlacksyLearningController.Run))]
    [TestCase(typeof(KlacksyLearningController), nameof(KlacksyLearningController.RetryUnfulfillable))]
    [TestCase(typeof(TranslationController), nameof(TranslationController.TranslateToAll))]
    [TestCase(typeof(ReceivedEmailController), nameof(ReceivedEmailController.Translate))]
    [TestCase(typeof(WhisperPluginController), nameof(WhisperPluginController.Install))]
    [TestCase(typeof(WhisperPluginController), nameof(WhisperPluginController.Uninstall))]
    [TestCase(typeof(FeaturePluginController), nameof(FeaturePluginController.InstallPlugin))]
    [TestCase(typeof(FeaturePluginController), nameof(FeaturePluginController.UninstallPlugin))]
    [TestCase(typeof(FeaturePluginController), nameof(FeaturePluginController.InstallFromMarketplace))]
    [TestCase(typeof(LanguageConfigController), nameof(LanguageConfigController.InstallPlugin))]
    [TestCase(typeof(LanguageConfigController), nameof(LanguageConfigController.UninstallPlugin))]
    [TestCase(typeof(LanguageConfigController), nameof(LanguageConfigController.DownloadAndInstallPackage))]
    [TestCase(typeof(LanguageConfigController), nameof(LanguageConfigController.UploadPackageToMarketplace))]
    [TestCase(typeof(UpdateController), nameof(UpdateController.Trigger))]
    [TestCase(typeof(UpdateController), nameof(UpdateController.Rollback))]
    [TestCase(typeof(EvalController), nameof(EvalController.SubmitCorrection))]
    [TestCase(typeof(EvalController), nameof(EvalController.SubmitHelpfulFeedback))]
    [TestCase(typeof(FeaturePluginController), nameof(FeaturePluginController.EnablePlugin))]
    [TestCase(typeof(FeaturePluginController), nameof(FeaturePluginController.DisablePlugin))]
    [TestCase(typeof(UpdateController), nameof(UpdateController.SaveConfig))]
    [TestCase(typeof(UpdateController), nameof(UpdateController.Cancel))]
    [TestCase(typeof(UpdateController), nameof(UpdateController.Delete))]
    public void SpendingOrSetupChangingEndpoint_IsBlockedOnPlayground(Type controller, string method)
    {
        IsBlocked(controller, method).ShouldBeTrue($"{controller.Name}.{method} must carry [BlockedOnPlayground]");
    }

    [Test]
    public void HarmonizerController_IsBlockedAsAWhole()
    {
        typeof(HolisticHarmonizerController).GetCustomAttribute<BlockedOnPlaygroundAttribute>().ShouldNotBeNull();
    }

    [TestCase(typeof(ModelsController), nameof(ModelsController.GetAvailableModels))]
    [TestCase(typeof(ProvidersController), nameof(ProvidersController.GetProviders))]
    [TestCase(typeof(ProvidersController), nameof(ProvidersController.GetProvider))]
    [TestCase(typeof(ModelSyncController), nameof(ModelSyncController.GetUnread))]
    [TestCase(typeof(ModelSyncController), nameof(ModelSyncController.GetHistory))]
    [TestCase(typeof(CustomSttProviderController), nameof(CustomSttProviderController.GetAll))]
    [TestCase(typeof(TranslationController), nameof(TranslationController.GetStatus))]
    [TestCase(typeof(WhisperPluginController), nameof(WhisperPluginController.GetStatus))]
    [TestCase(typeof(FeaturePluginController), nameof(FeaturePluginController.GetAllPlugins))]
    [TestCase(typeof(FeaturePluginController), nameof(FeaturePluginController.GetPlugin))]
    [TestCase(typeof(FeaturePluginController), nameof(FeaturePluginController.SearchMarketplace))]
    [TestCase(typeof(LanguageConfigController), nameof(LanguageConfigController.GetLanguagePlugins))]
    [TestCase(typeof(UpdateController), nameof(UpdateController.GetStatus))]
    [TestCase(typeof(ReceivedEmailController), nameof(ReceivedEmailController.FetchNow))]
    [TestCase(typeof(ChatController), nameof(ChatController.ProcessMessage))]
    [TestCase(typeof(ChatController), nameof(ChatController.ProcessMessageStream))]
    public void ReadOrChatEndpoint_StaysAvailable(Type controller, string method)
    {
        IsBlocked(controller, method).ShouldBeFalse($"{controller.Name}.{method} must stay reachable on the Playground");
    }

    [Test]
    public void BlockedController_IsNotBlockedOnAnyOtherControllerByAccident()
    {
        typeof(ModelsController).GetCustomAttribute<BlockedOnPlaygroundAttribute>().ShouldBeNull();
        typeof(ProvidersController).GetCustomAttribute<BlockedOnPlaygroundAttribute>().ShouldBeNull();
        typeof(SttController).GetCustomAttribute<BlockedOnPlaygroundAttribute>().ShouldBeNull();
        typeof(ChatController).GetCustomAttribute<BlockedOnPlaygroundAttribute>().ShouldBeNull();
    }
}
