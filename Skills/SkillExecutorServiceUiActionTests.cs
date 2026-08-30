// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the UiAction chokepoint in SkillExecutorService: UiAction skills run through
/// permission validation, parameter validation and the autonomy gate like every other skill and
/// only then return their declarative steps as a typed SkillResult (with usage tracking). Headless
/// contexts (no interactive UI session) are refused with a defined error even when the autonomy
/// gate is bypassed, and the confirm_pending_action replay carries the steps AND the original
/// parameters back through the executor.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Application.Skills;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SkillExecutorServiceUiActionTests
{
    private const string MutatingUiActionSkillName = "update_branch";
    private const string ReadOnlyUiActionSkillName = "search_in_list";
    private const string UiActionStepsConfig =
        "{\"steps\":[{\"action\":\"navigate\",\"route\":\"/workplace/settings\"}]}";

    private ISkillRegistry _registry = null!;
    private ISkillUsageTracker _usageTracker = null!;
    private IServiceProvider _serviceProvider = null!;
    private IGenericSkillDispatcher _genericDispatcher = null!;
    private IAutonomyGate _autonomyGate = null!;
    private IEntityChangeNotifier _entityChangeNotifier = null!;
    private IRecentEntityRegistrar _recentEntityRegistrar = null!;
    private IAgentAutonomyPreferenceRepository _preferenceRepository = null!;
    private IPendingConfirmationStore _confirmationStore = null!;
    private TurnConfirmationScope _turnScope = null!;
    private SkillExecutorService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _registry = Substitute.For<ISkillRegistry>();
        _usageTracker = Substitute.For<ISkillUsageTracker>();
        _serviceProvider = Substitute.For<IServiceProvider>();
        _genericDispatcher = Substitute.For<IGenericSkillDispatcher>();
        _autonomyGate = Substitute.For<IAutonomyGate>();
        _entityChangeNotifier = Substitute.For<IEntityChangeNotifier>();
        _recentEntityRegistrar = Substitute.For<IRecentEntityRegistrar>();
        _preferenceRepository = Substitute.For<IAgentAutonomyPreferenceRepository>();
        _confirmationStore = PendingStoreTestFactory.CreateConfirmationStore();
        _turnScope = new TurnConfirmationScope();

        _autonomyGate.CheckAsync(
                Arg.Any<SkillDescriptor>(), Arg.Any<SkillExecutionContext>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns((SkillResult?)null);

        _sut = CreateExecutor(_autonomyGate);
    }

    private SkillExecutorService CreateExecutor(IAutonomyGate gate) => new(
        _registry,
        _usageTracker,
        _serviceProvider,
        _genericDispatcher,
        gate,
        _entityChangeNotifier,
        _recentEntityRegistrar,
        Substitute.For<ILogger<SkillExecutorService>>());

    private SkillExecutorService CreateExecutorWithRealGate()
    {
        var gate = new AutonomyGateService(
            _preferenceRepository,
            new SkillRiskClassifier(),
            _confirmationStore,
            _turnScope,
            NullLogger<AutonomyGateService>.Instance);
        return CreateExecutor(gate);
    }

    private static SkillDescriptor UiActionDescriptor(
        string name,
        SkillCategory category = SkillCategory.Crud,
        IReadOnlyList<string>? requiredPermissions = null,
        string? handlerConfig = UiActionStepsConfig)
        => new(name, "test", category, [], requiredPermissions ?? [], [], null)
        {
            ExecutionType = LlmExecutionTypes.UiAction,
            HandlerConfig = handlerConfig
        };

    private static SkillExecutionContext Ctx(
        bool supportsUiActions = true,
        bool bypassGate = false,
        IReadOnlyList<string>? permissions = null,
        Guid? userId = null) => new()
    {
        UserId = userId ?? Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = permissions ?? new List<string>(),
        BypassAutonomyGate = bypassGate,
        SupportsUiActions = supportsUiActions
    };

    private void SetLevel(Guid userId, AutonomyLevel level)
    {
        _preferenceRepository.GetAsync(userId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = userId.ToString(), Level = level });
    }

    private Task<SkillResult> ExecuteAsync(
        SkillExecutorService executor, string skillName, Dictionary<string, object> parameters, SkillExecutionContext context)
        => executor.ExecuteAsync(new SkillInvocation { SkillName = skillName, Parameters = parameters }, context);

    [Test]
    public async Task UiAction_GatePasses_ReturnsStepsAndTracksUsage()
    {
        _registry.GetSkillByName(MutatingUiActionSkillName).Returns(UiActionDescriptor(MutatingUiActionSkillName));
        var parameters = new Dictionary<string, object> { ["name"] = "North" };

        var result = await ExecuteAsync(_sut, MutatingUiActionSkillName, parameters, Ctx());

        Assert.That(result.Success, Is.True);
        Assert.That(result.UiActionSteps, Is.EqualTo(UiActionStepsConfig));
        Assert.That(result.UiActionParameters, Is.SameAs(parameters));
        Assert.That(result.UiActionTrackingId, Is.Not.Null);
        await _usageTracker.Received(1).TrackAsync(
            Arg.Any<SkillDescriptor>(), Arg.Any<SkillExecutionContext>(), Arg.Any<Dictionary<string, object>>(),
            Arg.Any<SkillResult>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>(), result.UiActionTrackingId);
        await _genericDispatcher.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UiAction_GatePasses_DoesNotNotifyEntityChange_BecauseTheMutationHappensLaterInTheBrowser()
    {
        _registry.GetSkillByName(MutatingUiActionSkillName).Returns(UiActionDescriptor(MutatingUiActionSkillName));

        await ExecuteAsync(_sut, MutatingUiActionSkillName, new Dictionary<string, object>(), Ctx());

        await _entityChangeNotifier.DidNotReceive().NotifyExecutedAsync(
            Arg.Any<SkillDescriptor>(), Arg.Any<SkillExecutionContext>(), Arg.Any<SkillResult>(), Arg.Any<CancellationToken>());
        await _recentEntityRegistrar.DidNotReceive().RegisterAsync(
            Arg.Any<SkillDescriptor>(), Arg.Any<SkillExecutionContext>(), Arg.Any<SkillResult>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UiAction_GateHolds_ReturnsConfirmationWithoutSteps()
    {
        _registry.GetSkillByName(MutatingUiActionSkillName).Returns(UiActionDescriptor(MutatingUiActionSkillName));
        _autonomyGate.CheckAsync(
                Arg.Any<SkillDescriptor>(), Arg.Any<SkillExecutionContext>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.Confirmation("User confirmation required", "token-1"));

        var result = await ExecuteAsync(_sut, MutatingUiActionSkillName, new Dictionary<string, object>(), Ctx());

        Assert.That(result.Type, Is.EqualTo(SkillResultType.Confirmation));
        Assert.That(result.UiActionSteps, Is.Null);
        await _usageTracker.DidNotReceive().TrackAsync(
            Arg.Any<SkillDescriptor>(), Arg.Any<SkillExecutionContext>(), Arg.Any<Dictionary<string, object>>(),
            Arg.Any<SkillResult>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UiAction_WithoutRequiredPermission_IsDenied_BeforeGate()
    {
        _registry.GetSkillByName(MutatingUiActionSkillName).Returns(
            UiActionDescriptor(MutatingUiActionSkillName, requiredPermissions: new[] { "EditSettings" }));

        var result = await ExecuteAsync(_sut, MutatingUiActionSkillName, new Dictionary<string, object>(), Ctx());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Permission denied"));
        Assert.That(result.UiActionSteps, Is.Null);
        await _autonomyGate.DidNotReceive().CheckAsync(
            Arg.Any<SkillDescriptor>(), Arg.Any<SkillExecutionContext>(),
            Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UiAction_WithoutInteractiveUiSession_ReturnsDefinedError_BeforeGate()
    {
        _registry.GetSkillByName(MutatingUiActionSkillName).Returns(UiActionDescriptor(MutatingUiActionSkillName));

        var result = await ExecuteAsync(
            _sut, MutatingUiActionSkillName, new Dictionary<string, object>(), Ctx(supportsUiActions: false));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("requires an interactive UI session"));
        Assert.That(result.UiActionSteps, Is.Null);
        await _autonomyGate.DidNotReceive().CheckAsync(
            Arg.Any<SkillDescriptor>(), Arg.Any<SkillExecutionContext>(),
            Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UiAction_HeadlessWithBypassAutonomyGate_IsStillRefused()
    {
        _registry.GetSkillByName(MutatingUiActionSkillName).Returns(UiActionDescriptor(MutatingUiActionSkillName));

        var result = await ExecuteAsync(
            _sut, MutatingUiActionSkillName, new Dictionary<string, object>(),
            Ctx(supportsUiActions: false, bypassGate: true));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("requires an interactive UI session"));
        Assert.That(result.UiActionSteps, Is.Null);
    }

    [Test]
    public async Task UiAction_WithoutHandlerConfig_FallsBackToEmptyStepsObject()
    {
        _registry.GetSkillByName(MutatingUiActionSkillName).Returns(
            UiActionDescriptor(MutatingUiActionSkillName, handlerConfig: null));

        var result = await ExecuteAsync(_sut, MutatingUiActionSkillName, new Dictionary<string, object>(), Ctx());

        Assert.That(result.Success, Is.True);
        Assert.That(result.UiActionSteps, Is.EqualTo("{}"));
    }

    [Test]
    public async Task ReadOnlyUiAction_AtProposeLevel_ReturnsStepsWithoutConfirmation()
    {
        var executor = CreateExecutorWithRealGate();
        _registry.GetSkillByName(ReadOnlyUiActionSkillName).Returns(
            UiActionDescriptor(ReadOnlyUiActionSkillName, SkillCategory.Query));
        var context = Ctx();
        SetLevel(context.UserId, AutonomyLevel.Propose);

        var result = await ExecuteAsync(
            executor, ReadOnlyUiActionSkillName,
            new Dictionary<string, object> { ["entityType"] = "client", ["searchQuery"] = "Muster" }, context);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Type, Is.Not.EqualTo(SkillResultType.Confirmation));
        Assert.That(result.UiActionSteps, Is.EqualTo(UiActionStepsConfig));
    }

    [Test]
    public async Task MutatingUiAction_AtProposeLevel_IsHeldForConfirmation()
    {
        var executor = CreateExecutorWithRealGate();
        _registry.GetSkillByName(MutatingUiActionSkillName).Returns(UiActionDescriptor(MutatingUiActionSkillName));
        var context = Ctx();
        SetLevel(context.UserId, AutonomyLevel.Propose);

        var result = await ExecuteAsync(
            executor, MutatingUiActionSkillName, new Dictionary<string, object> { ["name"] = "North" }, context);

        Assert.That(result.Type, Is.EqualTo(SkillResultType.Confirmation));
        Assert.That(result.UiActionSteps, Is.Null);
        Assert.That(result.Metadata, Does.ContainKey("confirmationToken"));
    }

    [Test]
    public async Task ConfirmedUiAction_ReplaysStepsWithOriginalParameters()
    {
        var executor = CreateExecutorWithRealGate();
        _serviceProvider.GetService(typeof(ConfirmPendingActionSkill))
            .Returns(_ => new ConfirmPendingActionSkill(_confirmationStore, executor, _turnScope));
        _registry.GetSkillByName(MutatingUiActionSkillName).Returns(UiActionDescriptor(MutatingUiActionSkillName));
        _registry.GetSkillByName(AutonomyDefaults.ConfirmPendingActionSkillName).Returns(
            new SkillDescriptor(
                AutonomyDefaults.ConfirmPendingActionSkillName, "test", SkillCategory.System,
                [], [], [], typeof(ConfirmPendingActionSkill)));
        var context = Ctx();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        var originalParameters = new Dictionary<string, object> { ["branchId"] = "7", ["name"] = "North" };

        var held = await ExecuteAsync(executor, MutatingUiActionSkillName, originalParameters, context);
        var token = held.Metadata!["confirmationToken"].ToString()!;
        var confirmed = await ExecuteAsync(
            executor, AutonomyDefaults.ConfirmPendingActionSkillName,
            new Dictionary<string, object> { [AutonomyDefaults.ConfirmationTokenParameter] = token }, context);

        Assert.That(held.Type, Is.EqualTo(SkillResultType.Confirmation));
        Assert.That(held.UiActionSteps, Is.Null);
        Assert.That(confirmed.Success, Is.True);
        Assert.That(confirmed.UiActionSteps, Is.EqualTo(UiActionStepsConfig));
        Assert.That(confirmed.UiActionParameters, Is.Not.Null);
        Assert.That(confirmed.UiActionParameters!["branchId"].ToString(), Is.EqualTo("7"));
        Assert.That(confirmed.UiActionParameters!["name"].ToString(), Is.EqualTo("North"));
        Assert.That(confirmed.UiActionParameters!.ContainsKey(AutonomyDefaults.ConfirmationTokenParameter), Is.False);
    }

    [Test]
    public async Task UiActionWithValidTokenParameter_PassesGate_AndStripsTokenFromStepParameters()
    {
        var executor = CreateExecutorWithRealGate();
        _registry.GetSkillByName(MutatingUiActionSkillName).Returns(UiActionDescriptor(MutatingUiActionSkillName));
        var context = Ctx();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        var token = _confirmationStore.Create(
            context.UserId, MutatingUiActionSkillName, new Dictionary<string, object> { ["name"] = "North" });

        var result = await ExecuteAsync(
            executor, MutatingUiActionSkillName,
            new Dictionary<string, object>
            {
                ["name"] = "North",
                [AutonomyDefaults.ConfirmationTokenParameter] = token
            },
            context);

        Assert.That(result.Success, Is.True);
        Assert.That(result.UiActionSteps, Is.EqualTo(UiActionStepsConfig));
        Assert.That(result.UiActionParameters!.ContainsKey(AutonomyDefaults.ConfirmationTokenParameter), Is.False);
    }
}
