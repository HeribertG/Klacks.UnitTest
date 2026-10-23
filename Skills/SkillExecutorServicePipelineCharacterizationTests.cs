// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization of the SkillExecutorService.ExecuteAsync pipeline as a whole: the precedence of its checks
/// (registry, permissions, parameters, UI context, apiCall step, scenario scope, autonomy gate), every early refusal
/// with its failure kind, category, text and token, the four dispatch routes (UI action, generic handler, in-process
/// skill, plugin skill) and their dead ends, the success post-processing order (usage row, log, entity notification,
/// recent-entity registration) and each catch clause. The registry resolves the invoked name "Canonical_Skill" to the
/// descriptor "canonical_skill", so every assertion also pins WHICH of the two names a text, a log line, a usage row
/// and the external-content taint carry.
/// </summary>

using Klacks.Api.Application.Exceptions;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using PluginSkills = Klacks.Plugin.Contracts.Skills;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SkillExecutorServicePipelineCharacterizationTests
{
    private const string Invoked = "Canonical_Skill";
    private const string Canonical = "canonical_skill";
    private const string Untrusted = "web_search";
    private const string InvokedUntrusted = "Web_Search";
    private const string HandlerType = "generic";
    private const string RequiredPermission = "Perm.Required";
    private const string RequiredParameter = "target";

    private ISkillRegistry _registry = null!;
    private ISkillUsageTracker _tracker = null!;
    private IServiceProvider _services = null!;
    private IGenericSkillDispatcher _dispatcher = null!;
    private IAutonomyGate _gate = null!;
    private ISkillScenarioScopeGuard _scopeGuard = null!;
    private IEntityChangeNotifier _notifier = null!;
    private IRecentEntityRegistrar _registrar = null!;
    private RecordingLogger<SkillExecutorService> _logger = null!;
    private SkillExecutorService _sut = null!;
    private CancellationTokenSource _cts = null!;
    private Dictionary<string, object> _parameters = null!;
    private SkillExecutionContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _registry = Substitute.For<ISkillRegistry>();
        _tracker = Substitute.For<ISkillUsageTracker>();
        _services = Substitute.For<IServiceProvider>();
        _dispatcher = Substitute.For<IGenericSkillDispatcher>();
        _dispatcher.CanHandle(HandlerType).Returns(true);
        _gate = Substitute.For<IAutonomyGate>();
        _gate.CheckAsync(default!, default!, default!, default).ReturnsForAnyArgs((SkillResult?)null);
        _scopeGuard = Substitute.For<ISkillScenarioScopeGuard>();
        _scopeGuard.CheckAsync(default!, default).ReturnsForAnyArgs((SkillResult?)null);
        _notifier = Substitute.For<IEntityChangeNotifier>();
        _registrar = Substitute.For<IRecentEntityRegistrar>();
        _logger = new RecordingLogger<SkillExecutorService>();
        _sut = new SkillExecutorService(_registry, _tracker, _services, _dispatcher, _gate, _notifier, _registrar, _scopeGuard, _logger);
        _cts = new CancellationTokenSource();
        _parameters = new Dictionary<string, object> { [RequiredParameter] = "x" };
        _context = new SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "planner",
            UserPermissions = [RequiredPermission],
        };
    }

    [TearDown]
    public void TearDown() => _cts.Dispose();

    [Test]
    public async Task UnknownSkill_IsTrackedUnderTheInvokedName_AsActionCategory_WithTheCallersToken()
    {
        var result = await RunAsync();

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Skill '{Invoked}' not found");
        result.ContainsExternalContent.ShouldBeFalse();
        await _tracker.Received(1).TrackFailureAsync(
            Invoked, SkillFailureKind.NotFound, _context, _parameters, result.Message, Arg.Any<TimeSpan>(), SkillCategory.Action, _cts.Token);
        Logs().ShouldBe(new[] { (LogLevel.Warning, $"Skill not found: {Invoked}") });
    }

    [Test]
    public async Task UnknownUntrustedName_IsNotTainted()
    {
        var result = await RunAsync(Untrusted);

        result.ContainsExternalContent.ShouldBeFalse();
    }

    [Test]
    public async Task MissingPermission_WinsOverAMissingParameter_AndIsTrackedUnderTheCanonicalName()
    {
        Register(Descriptor() with { });
        _parameters.Clear();
        _context = _context with { UserPermissions = [] };

        var result = await RunAsync();

        result.Message.ShouldBe($"Permission denied. Missing permissions: {RequiredPermission}");
        await _tracker.Received(1).TrackFailureAsync(
            Canonical, SkillFailureKind.PermissionDenied, _context, _parameters, result.Message, Arg.Any<TimeSpan>(), SkillCategory.Crud, _cts.Token);
        await _scopeGuard.DidNotReceiveWithAnyArgs().CheckAsync(default!, default);
        Logs().ShouldBeEmpty();
    }

    [Test]
    public async Task MissingParameter_WinsOverAMissingUiContext()
    {
        Register(Descriptor() with { ExecutionType = LlmExecutionTypes.UiAction, HandlerConfig = "{}" });
        _parameters.Clear();

        var result = await RunAsync();

        result.Message.ShouldBe($"Missing required parameters: {RequiredParameter}");
        await _tracker.Received(1).TrackFailureAsync(
            Canonical, SkillFailureKind.ParameterInvalid, _context, _parameters, result.Message, Arg.Any<TimeSpan>(), SkillCategory.Crud, _cts.Token);
    }

    [Test]
    public async Task UiActionWithoutUiContext_NamesTheInvokedSkill_AndWinsOverAnApiCallStep()
    {
        Register(Descriptor() with { ExecutionType = "uiaction", HandlerConfig = ApiCallConfig });

        var result = await RunAsync();

        result.Message.ShouldBe($"Skill '{Invoked}' requires an interactive UI session and cannot be executed in this context.");
        await _tracker.Received(1).TrackFailureAsync(
            Canonical, SkillFailureKind.UiActionContext, _context, _parameters, result.Message, Arg.Any<TimeSpan>(), SkillCategory.Crud, _cts.Token);
        Logs().ShouldBeEmpty();
    }

    [Test]
    public async Task UiActionWithAnApiCallStep_IsRefusedWithAWarning_BeforeTheGates()
    {
        Register(Descriptor() with { ExecutionType = LlmExecutionTypes.UiAction, HandlerConfig = ApiCallConfig });
        _context = _context with { SupportsUiActions = true };

        var result = await RunAsync();

        result.Message.ShouldBe(UiActionStepActions.ApiCallRejectedMessage);
        await _tracker.Received(1).TrackFailureAsync(
            Canonical, SkillFailureKind.Exception, _context, _parameters, result.Message, Arg.Any<TimeSpan>(), SkillCategory.Crud, _cts.Token);
        await _scopeGuard.DidNotReceiveWithAnyArgs().CheckAsync(default!, default);
        Logs().ShouldBe(new[]
        {
            (LogLevel.Warning, $"UiAction skill {Canonical} carries an apiCall step in its stored handler config and is not delivered to the client"),
        });
    }

    [Test]
    public async Task ScenarioScopeRefusal_WinsOverTheAutonomyGate_AndIsReturnedAsIs()
    {
        Register(Descriptor() with { HandlerType = HandlerType, HandlerConfig = "{}" });
        var refusal = SkillResult.Error("scenario hidden");
        _scopeGuard.CheckAsync(_parameters, _cts.Token).Returns(refusal);

        var result = await RunAsync();

        result.ShouldBeSameAs(refusal);
        await _tracker.Received(1).TrackFailureAsync(
            Canonical, SkillFailureKind.ParameterInvalid, _context, _parameters, "scenario hidden", Arg.Any<TimeSpan>(), SkillCategory.Crud, _cts.Token);
        await _gate.DidNotReceiveWithAnyArgs().CheckAsync(default!, default!, default!, default);
        await _dispatcher.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!, default!, default, default!, default);
    }

    [Test]
    public async Task AutonomyGateHold_IsTrackedAsGateHold_AndNothingIsDispatched()
    {
        var descriptor = Register(Descriptor() with { HandlerType = HandlerType, HandlerConfig = "{}" });
        var hold = SkillResult.Confirmation("confirm?", "token");
        _gate.CheckAsync(descriptor, _context, _parameters, _cts.Token).Returns(hold);

        var result = await RunAsync();

        result.ShouldBeSameAs(hold);
        await _tracker.Received(1).TrackFailureAsync(
            Canonical, SkillFailureKind.GateHold, _context, _parameters, "confirm?", Arg.Any<TimeSpan>(), SkillCategory.Crud, _cts.Token);
        await _dispatcher.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!, default!, default, default!, default);
        Logs().ShouldBeEmpty();
    }

    [TestCase("[{\"action\":\"navigate\"}]", "[{\"action\":\"navigate\"}]")]
    [TestCase("  ", "{}")]
    public async Task UiAction_IsDeliveredWithATrackingId_TrackedWithIt_AndSkipsTheEntityHooks(string config, string expectedSteps)
    {
        var descriptor = Register(Descriptor() with { ExecutionType = LlmExecutionTypes.UiAction, HandlerConfig = config });
        _context = _context with { SupportsUiActions = true };
        SkillResult? tracked = null;
        Guid? recordId = null;
        _tracker.TrackAsync(descriptor, _context, _parameters, Arg.Do<SkillResult>(r => tracked = r), Arg.Any<TimeSpan>(), CancellationToken.None, Arg.Do<Guid?>(id => recordId = id))
            .Returns(Task.CompletedTask);

        var result = await RunAsync();

        result.Type.ShouldBe(SkillResultType.Data);
        result.Success.ShouldBeTrue();
        result.UiActionSteps.ShouldBe(expectedSteps);
        result.UiActionParameters.ShouldBeSameAs(_parameters);
        result.Message.ShouldBe($"Function '{Canonical}' will be executed as UI action in the user's browser.");
        result.UiActionTrackingId.ShouldNotBeNull();
        recordId.ShouldBe(result.UiActionTrackingId);
        tracked.ShouldBeSameAs(result);
        await _notifier.DidNotReceiveWithAnyArgs().NotifyExecutedAsync(default!, default!, default!, default);
        await _registrar.DidNotReceiveWithAnyArgs().RegisterAsync(default!, default!, default!, default);
        Logs()[0].ShouldBe((LogLevel.Information, $"Executing skill: {Canonical} for user {_context.UserId}"));
        Logs()[1].Item2.ShouldMatch($"^Skill executed: {Canonical}, Success: True, Duration: \\d+ms$");
        Logs().Count.ShouldBe(2);
    }

    [Test]
    public async Task UiAction_WinsOverAGenericHandlerAndAnImplementation()
    {
        Register(Descriptor(typeof(ISkillImplementation)) with
        {
            ExecutionType = LlmExecutionTypes.UiAction,
            HandlerType = HandlerType,
            HandlerConfig = "{}",
        });
        _context = _context with { SupportsUiActions = true };

        var result = await RunAsync();

        result.UiActionSteps.ShouldBe("{}");
        await _dispatcher.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!, default!, default, default!, default);
        _dispatcher.DidNotReceiveWithAnyArgs().CanHandle(default);
        _services.DidNotReceive().GetService(Arg.Is<Type>(type => type != typeof(IInstallationLanguageResolver)));
    }

    [Test]
    public async Task GenericHandler_WinsOverAnImplementation()
    {
        Register(Descriptor(typeof(ISkillImplementation)) with { HandlerType = HandlerType, HandlerConfig = "{}" });
        var dispatched = SkillResult.SuccessResult("generic");
        _dispatcher.ExecuteAsync(default!, default!, default!, default, default!, default).ReturnsForAnyArgs(dispatched);

        var result = await RunAsync();

        result.ShouldBeSameAs(dispatched);
        _services.DidNotReceive().GetService(Arg.Is<Type>(type => type != typeof(IInstallationLanguageResolver)));
    }

    [Test]
    public async Task CancellationNotCausedByTheCaller_IsAnExecutionError_NotACancelledResult()
    {
        Register(Descriptor() with { HandlerType = HandlerType, HandlerConfig = "{}" });
        DispatcherThrows(new OperationCanceledException("timed out"));

        var result = await RunAsync();

        result.Type.ShouldBe(SkillResultType.Error);
        result.Message.ShouldBe("Execution error: timed out");
        result.Metadata![SkillErrorKeys.ExceptionType].ShouldBe(nameof(OperationCanceledException));
        await _tracker.Received(1).TrackFailureAsync(
            Canonical, SkillFailureKind.Exception, _context, _parameters, result.Message, Arg.Any<TimeSpan>(), SkillCategory.Crud, CancellationToken.None);
        Logs().Last().ShouldBe((LogLevel.Error, $"Unexpected error executing skill: {Invoked}"));
    }

    [Test]
    public async Task GenericHandlerWithoutConfig_NamesTheInvokedSkill_AndNeverDispatches()
    {
        Register(Descriptor() with { HandlerType = HandlerType, HandlerConfig = " " });

        var result = await RunAsync();

        result.Message.ShouldBe($"Skill '{Invoked}' has HandlerType '{HandlerType}' but no HandlerConfig.");
        await _tracker.Received(1).TrackFailureAsync(
            Canonical, SkillFailureKind.Exception, _context, _parameters, result.Message, Arg.Any<TimeSpan>(), SkillCategory.Crud, _cts.Token);
        await _dispatcher.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!, default!, default, default!, default);
        await _tracker.DidNotReceiveWithAnyArgs().TrackAsync(default!, default!, default!, default!, default, default, default);
        Logs().ShouldBe(new[] { (LogLevel.Information, $"Executing skill: {Canonical} for user {_context.UserId}") });
    }

    [Test]
    public async Task GenericHandler_Success_TracksThenLogsThenNotifiesThenRegisters_AndReturnsTheDispatcherResult()
    {
        var descriptor = Register(Descriptor() with { HandlerType = HandlerType, HandlerConfig = "{\"x\":1}" });
        var dispatched = SkillResult.SuccessResult(new { Id = 1 }, "done");
        _dispatcher.ExecuteAsync(HandlerType, "{\"x\":1}", _parameters, _context.ExplainDepth, _context.UserPermissions, _cts.Token)
            .Returns(dispatched);
        var logCountAtNotify = -1;
        _notifier.NotifyExecutedAsync(descriptor, _context, dispatched, _cts.Token).Returns(_ =>
        {
            logCountAtNotify = _logger.Entries.Count;
            return Task.CompletedTask;
        });

        var result = await RunAsync();

        result.ShouldBeSameAs(dispatched);
        Received.InOrder(() =>
        {
            _tracker.TrackAsync(descriptor, _context, _parameters, dispatched, Arg.Any<TimeSpan>(), CancellationToken.None, null);
            _notifier.NotifyExecutedAsync(descriptor, _context, dispatched, _cts.Token);
            _registrar.RegisterAsync(descriptor, _context, dispatched, _cts.Token);
        });
        logCountAtNotify.ShouldBe(2, "the executed line is logged after the usage row and before the notification");
        Logs()[1].Item2.ShouldMatch($"^Skill executed: {Canonical}, Success: True, Duration: \\d+ms$");
        await _tracker.DidNotReceiveWithAnyArgs().TrackFailureAsync(default!, default, default!, default, default, default, default, default);
    }

    [Test]
    public async Task FailingEntityHooks_OnlyWarn_AndTheResultStillReturns()
    {
        var descriptor = Register(Descriptor() with { HandlerType = HandlerType, HandlerConfig = "{}" });
        var dispatched = SkillResult.SuccessResult(null);
        _dispatcher.ExecuteAsync(default!, default!, default!, default, default!, default).ReturnsForAnyArgs(dispatched);
        _notifier.NotifyExecutedAsync(default!, default!, default!, default).ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("n")));
        _registrar.RegisterAsync(default!, default!, default!, default).ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("r")));

        var result = await RunAsync();

        result.ShouldBeSameAs(dispatched);
        Logs().Skip(2).ShouldBe(new[]
        {
            (LogLevel.Warning, $"Entity-changed notification failed for skill {Canonical}"),
            (LogLevel.Warning, $"Recent-entity registration failed for skill {Canonical}"),
        });
    }

    [Test]
    public async Task InProcessSkillImplementation_GetsTheLanguageKey_WhileTheUsageRowKeepsTheOriginalArguments()
    {
        var implementation = Substitute.For<ISkillImplementation>();
        var descriptor = Register(Descriptor(typeof(ISkillImplementation)));
        _services.GetService(typeof(ISkillImplementation)).Returns(implementation);
        _context = _context with { UserLanguage = "de" };
        Dictionary<string, object>? received = null;
        var produced = SkillResult.SuccessResult("ok");
        implementation.ExecuteAsync(_context, Arg.Do<Dictionary<string, object>>(p => received = p), _cts.Token).Returns(produced);

        var result = await RunAsync();

        result.ShouldBeSameAs(produced);
        received.ShouldNotBeSameAs(_parameters);
        received![SkillParameterKeys.UserLanguage].ShouldBe("de");
        received[RequiredParameter].ShouldBe("x");
        _parameters.ShouldNotContainKey(SkillParameterKeys.UserLanguage);
        await _tracker.Received(1).TrackAsync(descriptor, _context, _parameters, produced, Arg.Any<TimeSpan>(), CancellationToken.None, null);
    }

    [Test]
    public async Task InProcessISkill_IsExecutedWithTheSameArguments()
    {
        var skill = Substitute.For<ISkill>();
        Register(Descriptor(typeof(ISkill)));
        _services.GetService(typeof(ISkill)).Returns(skill);
        var produced = SkillResult.SuccessResult("ok");
        skill.ExecuteAsync(_context, _parameters, _cts.Token).Returns(produced);

        var result = await RunAsync();

        result.ShouldBeSameAs(produced);
    }

    [Test]
    public async Task PluginSkill_GetsTheReducedContextAndTheOriginalArguments_AndItsResultIsMapped()
    {
        var plugin = Substitute.For<PluginSkills.ISkillImplementation>();
        Register(Descriptor(typeof(PluginSkills.ISkillImplementation)));
        _services.GetService(typeof(PluginSkills.ISkillImplementation)).Returns(plugin);
        _context = _context with { UserLanguage = "de" };
        PluginSkills.SkillExecutionContext? pluginContext = null;
        Dictionary<string, object>? received = null;
        plugin.ExecuteAsync(Arg.Do<PluginSkills.SkillExecutionContext>(c => pluginContext = c), Arg.Do<Dictionary<string, object>>(p => received = p), _cts.Token)
            .Returns(PluginSkills.SkillResult.Navigation("/page", "go"));

        var result = await RunAsync();

        received.ShouldBeSameAs(_parameters);
        pluginContext!.UserId.ShouldBe(_context.UserId);
        pluginContext.TenantId.ShouldBe(_context.TenantId);
        pluginContext.UserName.ShouldBe(_context.UserName);
        pluginContext.UserPermissions.ShouldBeSameAs(_context.UserPermissions);
        pluginContext.ConfirmedBinding.ShouldBeNull();
        result.Type.ShouldBe(SkillResultType.Navigation);
        result.Success.ShouldBeTrue();
        result.Data.ShouldBe("/page");
        result.Message.ShouldBe("go");
    }

    [Test]
    public async Task ResolvedInstanceOfNoKnownKind_NamesTheInvokedSkill()
    {
        Register(Descriptor(typeof(string)));
        _services.GetService(typeof(string)).Returns("not a skill");

        var result = await RunAsync();

        result.Message.ShouldBe($"Skill '{Invoked}' does not implement ISkillImplementation or ISkill.");
        await _tracker.Received(1).TrackFailureAsync(
            Canonical, SkillFailureKind.Exception, _context, _parameters, result.Message, Arg.Any<TimeSpan>(), SkillCategory.Crud, _cts.Token);
        await _tracker.DidNotReceiveWithAnyArgs().TrackAsync(default!, default!, default!, default!, default, default, default);
    }

    [Test]
    public async Task NeitherHandlerNorImplementation_NamesTheInvokedSkill()
    {
        Register(Descriptor());

        var result = await RunAsync();

        result.Message.ShouldBe($"No implementation or handler found for skill '{Invoked}'.");
        await _tracker.Received(1).TrackFailureAsync(
            Canonical, SkillFailureKind.Exception, _context, _parameters, result.Message, Arg.Any<TimeSpan>(), SkillCategory.Crud, _cts.Token);
        await _notifier.DidNotReceiveWithAnyArgs().NotifyExecutedAsync(default!, default!, default!, default);
    }

    [TestCase("SOME_CODE", "SOME_CODE", "SOME_CODE")]
    [TestCase(null, "UNKNOWN", "(null)")]
    public async Task SkillException_BecomesAnErrorWithItsCode_LoggedUnderTheInvokedName(string? code, string expectedCode, string loggedCode)
    {
        Register(Descriptor() with { HandlerType = HandlerType, HandlerConfig = "{}" });
        var failure = new SkillException("inner_name", "went wrong", code);
        DispatcherThrows(failure);

        var result = await RunAsync();

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("went wrong");
        result.Metadata![SkillErrorKeys.ErrorCode].ShouldBe(expectedCode);
        result.Metadata[SkillErrorKeys.SkillName].ShouldBe("inner_name");
        result.Metadata.Count.ShouldBe(2);
        await _tracker.Received(1).TrackFailureAsync(
            Canonical, SkillFailureKind.Exception, _context, _parameters, "went wrong", Arg.Any<TimeSpan>(), SkillCategory.Crud, CancellationToken.None);
        var warning = _logger.Entries.Last();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldBe($"Skill error in {Invoked}: {loggedCode} - went wrong");
        warning.Exception.ShouldBeSameAs(failure);
    }

    [Test]
    public async Task UserFacingRejection_IsLoggedAndCodedUnderTheInvokedName_AndTrackedUnderTheCanonicalOne()
    {
        Register(Descriptor() with { HandlerType = HandlerType, HandlerConfig = "{}" });
        DispatcherThrows(new ConflictException("taken"));

        var result = await RunAsync();

        result.Message.ShouldBe("taken");
        result.Metadata![SkillErrorKeys.ErrorCode].ShouldBe(SkillErrorKeys.Rejected);
        result.Metadata[SkillErrorKeys.SkillName].ShouldBe(Invoked);
        await _tracker.Received(1).TrackFailureAsync(
            Canonical, SkillFailureKind.Exception, _context, _parameters, "taken", Arg.Any<TimeSpan>(), SkillCategory.Crud, CancellationToken.None);
        Logs().Last().ShouldBe((LogLevel.Warning, $"Skill {Invoked} rejected the request (ConflictException): taken"));
    }

    [Test]
    public async Task UnexpectedExceptionBeforeTheLookup_IsTrackedUnderTheInvokedName_AsActionCategory()
    {
        var failure = new InvalidOperationException("registry down");
        _registry.GetSkillByName(Invoked).Returns(_ => throw failure);

        var result = await RunAsync();

        result.Message.ShouldBe("Execution error: registry down");
        result.Metadata![SkillErrorKeys.ErrorCode].ShouldBe(SkillErrorKeys.ExecutionError);
        result.Metadata[SkillErrorKeys.SkillName].ShouldBe(Invoked);
        result.Metadata[SkillErrorKeys.ExceptionType].ShouldBe(nameof(InvalidOperationException));
        await _tracker.Received(1).TrackFailureAsync(
            Invoked, SkillFailureKind.Exception, _context, _parameters, result.Message, Arg.Any<TimeSpan>(), SkillCategory.Action, CancellationToken.None);
        var error = _logger.Entries.Single();
        error.Level.ShouldBe(LogLevel.Error);
        error.Message.ShouldBe($"Unexpected error executing skill: {Invoked}");
        error.Exception.ShouldBeSameAs(failure);
    }

    [Test]
    public async Task UsageRowThatThrowsOnSuccess_TurnsTheRunIntoAnExecutionError_AndSkipsTheEntityHooks()
    {
        var descriptor = Register(Descriptor() with { HandlerType = HandlerType, HandlerConfig = "{}" });
        _dispatcher.ExecuteAsync(default!, default!, default!, default, default!, default).ReturnsForAnyArgs(SkillResult.SuccessResult(null));
        _tracker.TrackAsync(default!, default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("db down")));

        var result = await RunAsync();

        result.Message.ShouldBe("Execution error: db down");
        await _tracker.Received(1).TrackFailureAsync(
            Canonical, SkillFailureKind.Exception, _context, _parameters, result.Message, Arg.Any<TimeSpan>(), SkillCategory.Crud, CancellationToken.None);
        await _notifier.DidNotReceiveWithAnyArgs().NotifyExecutedAsync(default!, default!, default!, default);
        Logs().ShouldNotContain(l => l.Item2.StartsWith("Skill executed:", StringComparison.Ordinal));
    }

    [Test]
    public async Task CancelledByTheCaller_IsACancelledResult_TrackedUnderTheCanonicalNameWithNoToken()
    {
        Register(Descriptor() with { HandlerType = HandlerType, HandlerConfig = "{}" });
        _dispatcher.ExecuteAsync(default!, default!, default!, default, default!, default).ReturnsForAnyArgs<Task<SkillResult>>(call =>
        {
            _cts.Cancel();
            throw new OperationCanceledException(_cts.Token);
        });

        var result = await RunAsync();

        result.Type.ShouldBe(SkillResultType.Cancelled);
        result.Message.ShouldBe($"Skill '{Invoked}' execution was cancelled");
        await _tracker.Received(1).TrackFailureAsync(
            Canonical, SkillFailureKind.Cancelled, _context, _parameters, result.Message, Arg.Any<TimeSpan>(), SkillCategory.Crud, CancellationToken.None);
        Logs().Last().ShouldBe((LogLevel.Information, $"Skill execution cancelled: {Invoked}"));
    }

    [Test]
    public async Task FailureTrackingThatThrows_IsSwallowedWithAWarning()
    {
        _tracker.TrackFailureAsync(default!, default, default!, default, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("db down")));

        var result = await RunAsync();

        result.Message.ShouldBe($"Skill '{Invoked}' not found");
        Logs().Last().ShouldBe((LogLevel.Warning, $"Failure tracking failed for {Invoked} (NotFound)"));
    }

    [Test]
    public async Task UntrustedSkill_IsTaintedByItsCanonicalName_WhileTheUsageRowGetsTheUntaintedResult()
    {
        var descriptor = Register(Descriptor() with { Name = Untrusted, HandlerType = HandlerType, HandlerConfig = "{}" }, InvokedUntrusted);
        var dispatched = SkillResult.SuccessResult("page");
        _dispatcher.ExecuteAsync(default!, default!, default!, default, default!, default).ReturnsForAnyArgs(dispatched);

        var result = await RunAsync(InvokedUntrusted);

        result.ContainsExternalContent.ShouldBeTrue();
        dispatched.ContainsExternalContent.ShouldBeFalse();
        await _tracker.Received(1).TrackAsync(descriptor, _context, _parameters, dispatched, Arg.Any<TimeSpan>(), CancellationToken.None, null);
        await _notifier.Received(1).NotifyExecutedAsync(descriptor, _context, dispatched, _cts.Token);
    }

    [Test]
    public async Task UntrustedSkill_EarlyRefusal_IsNotTainted_ButAnExceptionResultIs()
    {
        Register(Descriptor() with { Name = Untrusted, HandlerType = HandlerType, HandlerConfig = "{}" }, InvokedUntrusted);
        _context = _context with { UserPermissions = [] };

        var refused = await RunAsync(InvokedUntrusted);

        refused.ContainsExternalContent.ShouldBeFalse();

        _context = _context with { UserPermissions = [RequiredPermission] };
        DispatcherThrows(new InvalidOperationException("boom"));

        var failed = await RunAsync(InvokedUntrusted);

        failed.Message.ShouldBe("Execution error: boom");
        failed.ContainsExternalContent.ShouldBeTrue();
    }

    private const string ApiCallConfig = "[{\"action\":\"apiCall\",\"url\":\"/api/x\"}]";

    private Task<SkillResult> RunAsync(string skillName = Invoked) =>
        _sut.ExecuteAsync(new SkillInvocation { SkillName = skillName, Parameters = _parameters }, _context, _cts.Token);

    private SkillDescriptor Register(SkillDescriptor descriptor, string invokedName = Invoked)
    {
        _registry.GetSkillByName(invokedName).Returns(descriptor);
        return descriptor;
    }

    private static SkillDescriptor Descriptor(Type? implementationType = null) => new(
        Canonical,
        "test",
        SkillCategory.Crud,
        [new SkillParameter(RequiredParameter, "target", SkillParameterType.String, Required: true)],
        [RequiredPermission],
        [],
        implementationType);

    private void DispatcherThrows(Exception exception) =>
        _dispatcher.ExecuteAsync(default!, default!, default!, default, default!, default)
            .ReturnsForAnyArgs<Task<SkillResult>>(_ => throw exception);

    private List<(LogLevel, string)> Logs() => _logger.Entries.Select(e => (e.Level, e.Message)).ToList();
}
