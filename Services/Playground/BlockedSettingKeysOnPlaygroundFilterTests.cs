// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for BlockedSettingKeysOnPlaygroundFilter and the settings write endpoints it guards: with the mode on, a
/// write of a DeepL, STT or TTS key or engine setting is answered with HTTP 403 (errorCode
/// PLAYGROUND_FEATURE_BLOCKED, never 401 or a redirect) and never reaches the action; every other setting passes, and
/// with the mode off everything passes. Also pins that the blocked key list covers every such key constant and that
/// every install or download endpoint of the API carries BlockedOnPlayground.
/// </summary>

using System.Reflection;
using System.Text.RegularExpressions;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Presentation.Controllers.UserBackend.Settings;
using Klacks.Api.Presentation.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using SettingConstants = Klacks.Api.Application.Constants.Settings;
using SettingEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class BlockedSettingKeysOnPlaygroundFilterTests
{
    private const string SettingArgumentName = "setting";
    private const string SpeechKeyPattern = "^(DEEPL_API_KEY|ASSISTANT_STT_ENGINE|ASSISTANT_TTS_PROVIDER|ASSISTANT_(STT|TTS)_API_KEY.*)$";
    private const string InstallOrDownloadPattern = "install|download";
    private const int MinimumKnownInstallOrDownloadEndpoints = 8;

    private IPlaygroundMode _mode = null!;
    private ISettingsRepository _settings = null!;
    private BlockedSettingKeysOnPlaygroundFilter _sut = null!;
    private ActionContext _actionContext = null!;

    [SetUp]
    public void SetUp()
    {
        _mode = Substitute.For<IPlaygroundMode>();
        _settings = Substitute.For<ISettingsRepository>();
        _sut = new BlockedSettingKeysOnPlaygroundFilter(_mode, _settings);
        _actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
    }

    private static string[] SpeechKeyConstants() =>
        typeof(SettingConstants)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(value => Regex.IsMatch(value, SpeechKeyPattern))
            .ToArray();

    private static string[] UpdateKeyConstants() =>
        [SettingConstants.UPDATE_CHANNEL, SettingConstants.UPDATE_PINNED_VERSION];

    private ActionExecutingContext Executing(string key, Guid? id = null) =>
        new(
            _actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>
            {
                [SettingArgumentName] = new SettingEntity { Id = id ?? Guid.Empty, Type = key, Value = "value" },
            },
            new object());

    private ActionExecutionDelegate Next(Action onRun) => () =>
    {
        onRun();
        return Task.FromResult(new ActionExecutedContext(_actionContext, new List<IFilterMetadata>(), new object()));
    };

    [Test]
    public void BlockedKeyList_CoversEverySpeechAndTranslationKeyConstant()
    {
        var expected = SpeechKeyConstants().Concat(UpdateKeyConstants()).ToArray();

        expected.ShouldNotBeEmpty();
        PlaygroundBlockedSettingKeys.Keys.OrderBy(k => k).ShouldBe(expected.OrderBy(k => k));
    }

    [Test]
    public async Task ModeOn_BlockedKey_AnswersForbiddenAndNeverRunsTheAction()
    {
        _mode.IsActive.Returns(true);

        foreach (var key in SpeechKeyConstants())
        {
            var executing = Executing(key);
            var ran = false;

            await _sut.OnActionExecutionAsync(executing, Next(() => ran = true));

            ran.ShouldBeFalse(key);
            var result = executing.Result.ShouldBeOfType<ObjectResult>(key);
            result.StatusCode.ShouldBe(StatusCodes.Status403Forbidden, key);
            var problem = result.Value.ShouldBeOfType<ProblemDetails>(key);
            problem.Extensions["errorCode"].ShouldBe(PlaygroundHardeningConstants.FeatureBlockedErrorCode, key);
            executing.Result.ShouldNotBeOfType<UnauthorizedResult>(key);
            executing.Result.ShouldNotBeOfType<ForbidResult>(key);
            executing.Result.ShouldNotBeOfType<ChallengeResult>(key);
            executing.Result.ShouldNotBeOfType<RedirectResult>(key);
        }
    }

    [TestCase(SettingConstants.UPDATE_CHANNEL)]
    [TestCase(SettingConstants.UPDATE_PINNED_VERSION)]
    public async Task ModeOn_UpdateKey_IsBlocked(string key)
    {
        _mode.IsActive.Returns(true);
        var executing = Executing(key);
        var ran = false;

        await _sut.OnActionExecutionAsync(executing, Next(() => ran = true));

        ran.ShouldBeFalse();
        executing.Result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task ModeOn_HarmlessTypeInBodyButBlockedKeyStoredUnderTheId_IsBlocked()
    {
        _mode.IsActive.Returns(true);
        var id = Guid.NewGuid();
        _settings.GetSettingByIdNoTracking(id).Returns(new SettingEntity { Id = id, Type = SettingConstants.DEEPL_API_KEY });
        var executing = Executing(SettingConstants.APP_NAME, id);
        var ran = false;

        await _sut.OnActionExecutionAsync(executing, Next(() => ran = true));

        ran.ShouldBeFalse();
        executing.Result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task ModeOn_BlockedTypeInBodyOverHarmlessStoredRow_IsBlocked()
    {
        _mode.IsActive.Returns(true);
        var id = Guid.NewGuid();
        _settings.GetSettingByIdNoTracking(id).Returns(new SettingEntity { Id = id, Type = SettingConstants.APP_NAME });
        var executing = Executing(SettingConstants.ASSISTANT_STT_ENGINE, id);
        var ran = false;

        await _sut.OnActionExecutionAsync(executing, Next(() => ran = true));

        ran.ShouldBeFalse();
        executing.Result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task ModeOn_HarmlessBodyAndHarmlessOrUnknownStoredRow_PassesThrough()
    {
        _mode.IsActive.Returns(true);
        var storedId = Guid.NewGuid();
        var unknownId = Guid.NewGuid();
        _settings.GetSettingByIdNoTracking(storedId).Returns(new SettingEntity { Id = storedId, Type = SettingConstants.APP_NAME });
        _settings.GetSettingByIdNoTracking(unknownId).Returns((SettingEntity?)null);

        foreach (var id in new[] { storedId, unknownId })
        {
            var executing = Executing(SettingConstants.APP_NAME, id);
            var ran = false;

            await _sut.OnActionExecutionAsync(executing, Next(() => ran = true));

            ran.ShouldBeTrue();
            executing.Result.ShouldBeNull();
        }
    }

    [Test]
    public async Task ModeOff_NeverLooksTheStoredRowUp()
    {
        _mode.IsActive.Returns(false);
        var id = Guid.NewGuid();
        _settings.GetSettingByIdNoTracking(id).Returns(new SettingEntity { Id = id, Type = SettingConstants.DEEPL_API_KEY });
        var executing = Executing(SettingConstants.APP_NAME, id);
        var ran = false;

        await _sut.OnActionExecutionAsync(executing, Next(() => ran = true));

        ran.ShouldBeTrue();
        executing.Result.ShouldBeNull();
        await _settings.DidNotReceiveWithAnyArgs().GetSettingByIdNoTracking(default);
    }

    [Test]
    public async Task ModeOn_KeyInOtherCasing_IsStillBlocked()
    {
        _mode.IsActive.Returns(true);
        var executing = Executing(SettingConstants.DEEPL_API_KEY.ToLowerInvariant());
        var ran = false;

        await _sut.OnActionExecutionAsync(executing, Next(() => ran = true));

        ran.ShouldBeFalse();
        executing.Result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [TestCase(SettingConstants.ASSISTANT_TTS_VOICE)]
    [TestCase(SettingConstants.ASSISTANT_OUTPUT_MODE)]
    [TestCase(SettingConstants.ASSISTANT_ENHANCEMENT_ENABLED)]
    [TestCase(SettingConstants.ASSISTANT_SILENCE_THRESHOLD_MS)]
    [TestCase(SettingConstants.ASSISTANT_BARGE_IN_ENABLED)]
    [TestCase(SettingConstants.ASSISTANT_TRANSCRIPTION_MODEL)]
    [TestCase(SettingConstants.ASSISTANT_TRANSCRIPTION_PROMPT)]
    [TestCase(SettingConstants.ONBOARDING_STATE)]
    [TestCase(SettingConstants.APP_NAME)]
    public async Task ModeOn_OrdinarySetting_PassesThrough(string key)
    {
        _mode.IsActive.Returns(true);
        var executing = Executing(key);
        var ran = false;

        await _sut.OnActionExecutionAsync(executing, Next(() => ran = true));

        ran.ShouldBeTrue();
        executing.Result.ShouldBeNull();
    }

    [Test]
    public async Task ModeOff_BlockedKey_PassesThrough()
    {
        _mode.IsActive.Returns(false);

        foreach (var key in SpeechKeyConstants())
        {
            var executing = Executing(key);
            var ran = false;

            await _sut.OnActionExecutionAsync(executing, Next(() => ran = true));

            ran.ShouldBeTrue(key);
            executing.Result.ShouldBeNull(key);
        }
    }

    [Test]
    public async Task ModeOn_ActionWithoutSettingArgument_PassesThrough()
    {
        _mode.IsActive.Returns(true);
        var executing = new ActionExecutingContext(
            _actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());
        var ran = false;

        await _sut.OnActionExecutionAsync(executing, Next(() => ran = true));

        ran.ShouldBeTrue();
        executing.Result.ShouldBeNull();
    }

    [TestCase(nameof(GeneralSettingsController.AddSetting))]
    [TestCase(nameof(GeneralSettingsController.PutSetting))]
    public void SettingsWriteEndpoint_IsGuardedByKey(string method)
    {
        var action = typeof(GeneralSettingsController).GetMethod(method)!;

        action.GetCustomAttribute<BlockedSettingKeysOnPlaygroundAttribute>().ShouldNotBeNull();
        action.GetCustomAttribute<BlockedOnPlaygroundAttribute>().ShouldBeNull("the settings write must stay open for ordinary keys");
    }

    [TestCase(nameof(GeneralSettingsController.GetSetting))]
    [TestCase(nameof(GeneralSettingsController.GetSettingsListAsync))]
    public void SettingsReadEndpoint_StaysOpen(string method)
    {
        var action = typeof(GeneralSettingsController).GetMethod(method)!;

        action.GetCustomAttribute<BlockedSettingKeysOnPlaygroundAttribute>().ShouldBeNull();
        action.GetCustomAttribute<BlockedOnPlaygroundAttribute>().ShouldBeNull();
    }

    [Test]
    public void TestEmailConfiguration_IsBlockedOnPlayground()
    {
        var action = typeof(GeneralSettingsController).GetMethod(nameof(GeneralSettingsController.TestEmailConfiguration))!;

        action.GetCustomAttribute<BlockedOnPlaygroundAttribute>().ShouldNotBeNull();
    }

    [Test]
    public void EveryInstallOrDownloadWriteEndpoint_IsBlockedOnPlayground()
    {
        var candidates = typeof(GeneralSettingsController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(IsInstallOrDownloadWrite)
            .ToList();

        candidates.Count.ShouldBeGreaterThanOrEqualTo(MinimumKnownInstallOrDownloadEndpoints);

        var unblocked = candidates
            .Where(m => m.GetCustomAttribute<BlockedOnPlaygroundAttribute>() is null
                && m.DeclaringType!.GetCustomAttribute<BlockedOnPlaygroundAttribute>() is null)
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToList();

        unblocked.ShouldBeEmpty($"install/download endpoints without [BlockedOnPlayground]: {string.Join(", ", unblocked)}");
    }

    private static bool IsInstallOrDownloadWrite(MethodInfo method) =>
        method.GetCustomAttributes<HttpMethodAttribute>().Any(attribute =>
            !attribute.HttpMethods.Contains(HttpMethods.Get)
            && attribute.Template is not null
            && Regex.IsMatch(attribute.Template, InstallOrDownloadPattern, RegexOptions.IgnoreCase));
}
