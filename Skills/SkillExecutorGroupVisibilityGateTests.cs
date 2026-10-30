// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The group visibility guards treat a missing user as an unrestricted background job (ClientGroupFilterService), while
/// GroupScopeGuard answers empty for the same case. A messaging or e-mail skill that was opened to Supervisors would
/// therefore read everything if an entry path ever reached the executor without a user. The executor refuses such a call
/// for the skills of GroupVisibilityBoundSkills when the caller is not an administrator and neither the context nor the
/// async flow names a user. Exercised with the real executor; only registry, dispatcher and gates are stubbed.
/// </summary>

using Klacks.Api.Application.Services.Groups;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SkillExecutorGroupVisibilityGateTests
{
    private const string BoundSkill = "read_messages";
    private const string UnboundSkill = "list_branches";
    private const string GenericHandlerType = "test_handler";
    private const string GenericHandlerConfig = "{\"endpoint\":\"test\"}";

    private ISkillRegistry _registry = null!;
    private IGenericSkillDispatcher _genericDispatcher = null!;
    private SkillExecutorService _sut = null!;

    private static IEnumerable<string> BoundSkills => GroupVisibilityBoundSkills.All;

    [SetUp]
    public void SetUp()
    {
        _registry = Substitute.For<ISkillRegistry>();
        _genericDispatcher = Substitute.For<IGenericSkillDispatcher>();

        var autonomyGate = Substitute.For<IAutonomyGate>();
        autonomyGate.CheckAsync(
                Arg.Any<SkillDescriptor>(), Arg.Any<SkillExecutionContext>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns((SkillResult?)null);
        _genericDispatcher.CanHandle(GenericHandlerType).Returns(true);
        _genericDispatcher.ExecuteAsync(
                GenericHandlerType, GenericHandlerConfig,
                Arg.Any<Dictionary<string, object>>(), Arg.Any<ExplainDepthProfile?>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "ran"));
        _registry.GetSkillByName(Arg.Any<string>()).Returns(call =>
            new SkillDescriptor(call.Arg<string>(), "test", SkillCategory.Query, [], [], [], null)
            {
                HandlerType = GenericHandlerType,
                HandlerConfig = GenericHandlerConfig
            });

        var scopeGuard = Substitute.For<ISkillScenarioScopeGuard>();
        scopeGuard.CheckAsync(Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns((SkillResult?)null);

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

    private static SkillExecutionContext Ctx(Guid userId, params string[] permissions) => new()
    {
        UserId = userId,
        TenantId = Guid.Empty,
        UserName = "caller",
        UserPermissions = permissions.ToList(),
        BypassAutonomyGate = true
    };

    private Task<SkillResult> ExecuteAsync(string skill, SkillExecutionContext context) =>
        _sut.ExecuteAsync(new SkillInvocation { SkillName = skill, Parameters = new Dictionary<string, object>() }, context);

    [TestCaseSource(nameof(BoundSkills))]
    public async Task NonAdminWithoutAnyUser_IsRefused_ForEveryBoundSkill(string skill)
    {
        var result = await ExecuteAsync(skill, Ctx(Guid.Empty, Roles.Authorised, Permissions.CanEditClients));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(GroupVisibilityBoundSkills.UnidentifiedCallerMessage);
        await _genericDispatcher.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!, default!, default, default!, default);
    }

    [Test]
    public async Task NonAdminWithAContextUser_Runs()
    {
        var result = await ExecuteAsync(BoundSkill, Ctx(Guid.NewGuid(), Permissions.CanEditClients));

        result.Success.ShouldBeTrue();
    }

    [Test]
    public async Task NonAdminWithoutContextUser_ButWithAnOwnerSetFurtherUp_Runs()
    {
        using (ExecutionPrincipal.Begin(Guid.NewGuid()))
        {
            var result = await ExecuteAsync(BoundSkill, Ctx(Guid.Empty, Permissions.CanEditClients));

            result.Success.ShouldBeTrue();
        }
    }

    [Test]
    public async Task AdminWithoutAnyUser_Runs_BecauseAdminsAreUnrestrictedAnyway()
    {
        var result = await ExecuteAsync(BoundSkill, Ctx(Guid.Empty, Roles.Admin));

        result.Success.ShouldBeTrue();
    }

    [Test]
    public async Task SkillOutsideTheSet_KeepsRunningWithoutAUser()
    {
        var result = await ExecuteAsync(UnboundSkill, Ctx(Guid.Empty, Permissions.CanViewSchedule));

        result.Success.ShouldBeTrue();
    }
}
