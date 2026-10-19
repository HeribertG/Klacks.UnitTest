// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Skills reached outside an HTTP request (Telegram, inbound automation, proactive remediation) carry their acting
/// user only in SkillExecutionContext.UserId, while every visibility guard reads the ambient user (HTTP claim, else
/// ExecutionPrincipal). The executor therefore runs each invocation under ExecutionPrincipal = context.UserId unless an
/// owner is already set, the autonomy gate included: a confirmation preview (send_message resolves its recipient under the
/// caller's group visibility) must never be computed as an unrestricted system job. Exercised with the real executor, scope guard, token guard, group guard and UserService; only
/// the group visibility service (the user's DB rows) is stubbed.
/// </summary>

using Klacks.Api.Application.Services.Groups;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SkillExecutorServiceExecutionPrincipalTests
{
    private const string SkillName = "read_schedule_state";
    private const string GenericHandlerType = "test_handler";
    private const string GenericHandlerConfig = "{\"endpoint\":\"test\"}";

    private ISkillRegistry _registry = null!;
    private IGenericSkillDispatcher _genericDispatcher = null!;
    private IGroupVisibilityService _groupVisibilityService = null!;
    private IAnalyseScenarioRepository _scenarioRepository = null!;
    private SkillExecutorService _sut = null!;
    private Guid _visibleGroupId;
    private Guid _hiddenToken;
    private string? _principalSeenBySkill;
    private string? _principalSeenByGate;

    [SetUp]
    public void SetUp()
    {
        _registry = Substitute.For<ISkillRegistry>();
        _genericDispatcher = Substitute.For<IGenericSkillDispatcher>();
        _groupVisibilityService = Substitute.For<IGroupVisibilityService>();
        _scenarioRepository = Substitute.For<IAnalyseScenarioRepository>();
        _visibleGroupId = Guid.NewGuid();
        _hiddenToken = Guid.NewGuid();
        _principalSeenBySkill = null;
        _principalSeenByGate = null;

        _groupVisibilityService.GetVisibilityScopeAsync()
            .Returns(Task.FromResult(GroupVisibilityScope.Restricted([_visibleGroupId], [_visibleGroupId])));
        _scenarioRepository.GetByTokenAsync(_hiddenToken, Arg.Any<CancellationToken>())
            .Returns(new AnalyseScenario { Id = Guid.NewGuid(), Token = _hiddenToken, GroupId = Guid.NewGuid() });

        var autonomyGate = Substitute.For<IAutonomyGate>();
        autonomyGate.CheckAsync(
                Arg.Any<SkillDescriptor>(), Arg.Any<SkillExecutionContext>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _principalSeenByGate = ExecutionPrincipal.CurrentUserId;
                return (SkillResult?)null;
            });
        _genericDispatcher.CanHandle(GenericHandlerType).Returns(true);
        _genericDispatcher.ExecuteAsync(
                GenericHandlerType, GenericHandlerConfig,
                Arg.Any<Dictionary<string, object>>(), Arg.Any<ExplainDepthProfile?>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _principalSeenBySkill = ExecutionPrincipal.CurrentUserId;
                return SkillResult.SuccessResult(null, "ran");
            });
        _registry.GetSkillByName(SkillName).Returns(
            new SkillDescriptor(SkillName, "test", SkillCategory.Query, [], [], [], null)
            {
                HandlerType = GenericHandlerType,
                HandlerConfig = GenericHandlerConfig
            });

        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);
        var userService = new UserService(accessor, null!, Substitute.For<ILogger<UserService>>());
        var groupGuard = new GroupVisibilityGuard(_groupVisibilityService, userService);
        var scopeGuard = new SkillScenarioScopeGuard(new ScenarioTokenAccessGuard(_scenarioRepository, groupGuard));

        _sut = new SkillExecutorService(
            _registry,
            Substitute.For<ISkillUsageTracker>(),
            Substitute.For<IServiceProvider>(),
            _genericDispatcher,
            autonomyGate,
            Substitute.For<IEntityChangeNotifier>(),
            Substitute.For<IRecentEntityRegistrar>(),
            scopeGuard,
            Substitute.For<ILogger<SkillExecutorService>>());
    }

    private static SkillExecutionContext Ctx(Guid userId) => new()
    {
        UserId = userId,
        TenantId = Guid.Empty,
        UserName = "Klacksy telegram",
        UserPermissions = new List<string>(),
        BypassAutonomyGate = true
    };

    private Task<SkillResult> ExecuteAsync(Guid userId, Guid? analyseToken)
    {
        var parameters = new Dictionary<string, object>();
        if (analyseToken is { } token)
        {
            parameters["analyseToken"] = token.ToString();
        }

        return _sut.ExecuteAsync(new SkillInvocation { SkillName = SkillName, Parameters = parameters }, Ctx(userId));
    }

    [Test]
    public async Task RestrictedUserWithoutHttpContext_HiddenScenarioToken_IsRefusedLikeAnUnknownScenario()
    {
        var result = await ExecuteAsync(Guid.NewGuid(), _hiddenToken);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"No scenario found for analyseToken '{_hiddenToken}'.");
        await _genericDispatcher.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!, default!, default, default!, default);
    }

    [Test]
    public async Task SkillRunsUnderTheContextUser_AndThePrincipalIsRestoredAfterwards()
    {
        var userId = Guid.NewGuid();

        var result = await ExecuteAsync(userId, null);

        result.Success.ShouldBeTrue();
        _principalSeenBySkill.ShouldBe(userId.ToString());
        ExecutionPrincipal.CurrentUserId.ShouldBeNull();
    }

    [Test]
    public async Task TheAutonomyGate_AndSoEveryConfirmationPreview_RunsUnderTheContextUser()
    {
        var userId = Guid.NewGuid();

        await ExecuteAsync(userId, null);

        _principalSeenByGate.ShouldBe(userId.ToString());
    }

    [Test]
    public async Task AnAlreadySetPrincipal_IsKept()
    {
        var owner = Guid.NewGuid();

        using (ExecutionPrincipal.Begin(owner))
        {
            await ExecuteAsync(Guid.NewGuid(), null);
            ExecutionPrincipal.CurrentUserId.ShouldBe(owner.ToString());
        }

        _principalSeenBySkill.ShouldBe(owner.ToString());
    }

    [Test]
    public async Task EmptyContextUser_StaysWithoutOwner()
    {
        var result = await ExecuteAsync(Guid.Empty, _hiddenToken);

        result.Success.ShouldBeTrue();
        _principalSeenBySkill.ShouldBeNull();
    }
}
