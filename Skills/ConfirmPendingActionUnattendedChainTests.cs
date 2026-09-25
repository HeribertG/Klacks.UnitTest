// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Chain tests for security finding B1 of the macro assignment review: a turn holds a sensitive skill (here
/// assign_macro_to_shift) and receives a confirmation token; the same turn then tries to have a background path redeem
/// that token through confirm_pending_action, so the held action would run without the user ever replying. Uses the real
/// SkillRiskClassifier, UnattendedSkillPolicy, ScheduleRecurringTaskSkill, ScheduledTaskRunner, ConfirmPendingActionSkill
/// and pending-confirmation store; only the skill executor is a router that hands confirm_pending_action to the real
/// skill with a fresh turn scope (a new DI scope, as in the runner) and records every other skill. Expected: scheduling
/// the redemption is refused, an already stored task of that kind is refused and disabled by the runner, no path runs the
/// held skill, and the token stays redeemable by the user in the chat.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Application.Services.Assistant.Scheduling;
using Klacks.Api.Application.Skills;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ConfirmPendingActionUnattendedChainTests
{
    private const string HeldSkill = "assign_macro_to_shift";
    private const string EveryMinute = "* * * * *";
    private const string TaskName = "confirm it for me";
    private const string TimeZone = "Europe/Zurich";

    private static readonly Guid Owner = Guid.NewGuid();

    private IPendingConfirmationStore _store = null!;
    private ISkillRegistry _registry = null!;
    private ISkillExecutor _executor = null!;
    private List<SkillInvocation> _executedSkills = null!;

    [SetUp]
    public void SetUp()
    {
        _store = PendingStoreTestFactory.CreateConfirmationStore();
        _registry = Substitute.For<ISkillRegistry>();
        _registry.GetSkillByName(Arg.Any<string>()).Returns(ci => Descriptor(ci.ArgAt<string>(0)));
        _executedSkills = [];
        _executor = Substitute.For<ISkillExecutor>();
        _executor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci => RouteAsync(ci.ArgAt<SkillInvocation>(0), ci.ArgAt<SkillExecutionContext>(1)));
    }

    [Test]
    public async Task SchedulingTheRedemptionOfAHeldToken_IsRefused()
    {
        var token = HoldSensitiveSkillInAnEarlierScope();
        var repository = Substitute.For<IScheduledTaskRepository>();
        var skill = new ScheduleRecurringTaskSkill(
            repository,
            _registry,
            new SkillRiskClassifier(),
            new EffectiveTimeZoneResolver(new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.Utc)));

        var result = await skill.ExecuteAsync(InteractiveContext(), new Dictionary<string, object>
        {
            ["name"] = TaskName,
            ["cronExpression"] = EveryMinute,
            ["actionType"] = ScheduledTaskActionTypes.Skill,
            ["skillName"] = AutonomyDefaults.ConfirmPendingActionSkillName,
            ["skillParameters"] = TokenParametersJson(token),
            ["allowIrreversibleUnattended"] = true,
            ["apply"] = true
        });

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain(AutonomyDefaults.ConfirmPendingActionSkillName);
        await repository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await repository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Test]
    public async Task AStoredTaskRedeemingAHeldToken_IsRefusedAndDisabled_AndTheHeldSkillNeverRuns()
    {
        var token = HoldSensitiveSkillInAnEarlierScope();
        var task = RedemptionTask(token);
        var repository = Substitute.For<IScheduledTaskRepository>();
        repository.GetDueAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(new List<ScheduledTask> { task });
        repository.TryClaimAsync(Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await Runner(repository).RunDueAsync();

        _executedSkills.ShouldNotContain(invocation => invocation.SkillName == HeldSkill);
        task.LastStatus.ShouldBe(ScheduledTaskRunStatus.Error);
        task.IsEnabled.ShouldBeFalse();
        await RedeemInteractivelyAsync(token);
    }

    [Test]
    public void ConfirmPendingAction_IsDeniedOnEveryUnattendedPath()
    {
        var policy = new UnattendedSkillPolicy(_registry, new SkillRiskClassifier());

        foreach (var kind in Enum.GetValues<UnattendedExecutionKind>())
        {
            foreach (var level in Enum.GetValues<AutonomyLevel>())
            {
                foreach (var optIn in new[] { false, true })
                {
                    var decision = policy.Decide(new UnattendedSkillRequest(
                        AutonomyDefaults.ConfirmPendingActionSkillName.ToUpperInvariant(), AdminPermissions(), level, kind, optIn));

                    decision.Allowed.ShouldBeFalse($"{kind} {level} optIn={optIn}");
                    UnattendedDenyReasonClassification.IsOwnerFixable(decision.DenyReason)
                        .ShouldBeFalse($"{kind} {level} optIn={optIn}");
                }
            }
        }
    }

    [Test]
    public async Task ABackgroundPathRedeemingAHeldToken_IsRefused_AndTheTokenStaysRedeemableInTheChat()
    {
        var token = HoldSensitiveSkillInAnEarlierScope();
        var skill = new ConfirmPendingActionSkill(_store, _executor, new TurnConfirmationScope());
        var planStepContext = InteractiveContext() with { BypassAutonomyGate = true, SupportsUiActions = false };

        var result = await skill.ExecuteAsync(planStepContext, TokenParameters(token));

        result.Success.ShouldBeFalse();
        _executedSkills.ShouldBeEmpty();
        await RedeemInteractivelyAsync(token);
    }

    private string HoldSensitiveSkillInAnEarlierScope()
    {
        var token = _store.Create(Owner, HeldSkill, new Dictionary<string, object>
        {
            ["shiftId"] = Guid.NewGuid().ToString(),
            ["macroId"] = Guid.NewGuid().ToString()
        });
        new TurnConfirmationScope().MarkIssuedForSensitiveSkill(token);
        return token;
    }

    private async Task RedeemInteractivelyAsync(string token)
    {
        _executedSkills.Clear();
        var chatSkill = new ConfirmPendingActionSkill(_store, _executor, new TurnConfirmationScope());

        var result = await chatSkill.ExecuteAsync(InteractiveContext(), TokenParameters(token));

        result.Success.ShouldBeTrue();
        _executedSkills.ShouldHaveSingleItem().SkillName.ShouldBe(HeldSkill);
    }

    private async Task<SkillResult> RouteAsync(SkillInvocation invocation, SkillExecutionContext context)
    {
        if (string.Equals(invocation.SkillName, AutonomyDefaults.ConfirmPendingActionSkillName, StringComparison.OrdinalIgnoreCase))
        {
            var freshScopeSkill = new ConfirmPendingActionSkill(_store, _executor, new TurnConfirmationScope());
            return await freshScopeSkill.ExecuteAsync(context, invocation.Parameters);
        }

        _executedSkills.Add(invocation);
        return SkillResult.SuccessResult(null, "done");
    }

    private ScheduledTaskRunner Runner(IScheduledTaskRepository repository)
    {
        var tokenIssuer = Substitute.For<IInternalTokenIssuer>();
        tokenIssuer.IssueForOwnerAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(InternalTokenResult.Issued(new BearerToken("owner-jwt"), new[] { Roles.Admin }));
        var agents = Substitute.For<IAgentRepository>();
        agents.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(new Agent { Id = Guid.NewGuid() });

        return new ScheduledTaskRunner(
            repository,
            _executor,
            Substitute.For<IAssistantNotificationService>(),
            Substitute.For<IPendingUserNoteRepository>(),
            agents,
            new UnattendedSkillPolicy(_registry, new SkillRiskClassifier()),
            Substitute.For<IAgentAutonomyPreferenceRepository>(),
            tokenIssuer,
            Substitute.For<ILogger<ScheduledTaskRunner>>());
    }

    private static ScheduledTask RedemptionTask(string token) => new()
    {
        Id = Guid.NewGuid(),
        Name = TaskName,
        CronExpression = EveryMinute,
        TimeZoneId = TimeZone,
        ActionType = ScheduledTaskActionTypes.Skill,
        SkillName = AutonomyDefaults.ConfirmPendingActionSkillName,
        ParametersJson = TokenParametersJson(token),
        OwnerUserId = Owner,
        OwnerUserName = Owner.ToString(),
        OwnerPermissionsCsv = string.Join(",", AdminPermissions()),
        IsEnabled = true,
        NextRunUtc = DateTime.UtcNow.AddSeconds(-1),
        AllowIrreversibleUnattended = true
    };

    private static string TokenParametersJson(string token) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { [AutonomyDefaults.ConfirmationTokenParameter] = token });

    private static Dictionary<string, object> TokenParameters(string token) =>
        new() { [AutonomyDefaults.ConfirmationTokenParameter] = token };

    private static IReadOnlyList<string> AdminPermissions() => Permissions.ExpandRoles(new[] { Roles.Admin });

    private static SkillExecutionContext InteractiveContext() => new()
    {
        UserId = Owner,
        TenantId = Guid.Empty,
        UserName = Owner.ToString(),
        UserPermissions = AdminPermissions(),
        UserTimezone = TimeZone
    };

    private static SkillDescriptor Descriptor(string name) =>
        new(name, "test skill", SkillCategory.Crud, Array.Empty<SkillParameter>(), Array.Empty<string>(),
            Array.Empty<LLMCapability>(), null);
}
