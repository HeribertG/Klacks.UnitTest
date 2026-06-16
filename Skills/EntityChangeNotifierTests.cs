// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the post-execution refresh hook: successful direct mutations push an
/// entity-changed notification, while read-only, scenario-gated, failed, and schedule-domain
/// executions (covered by the work-notifications hub) do not.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class EntityChangeNotifierTests
{
    private static SkillDescriptor Descriptor(string name, SkillCategory category = SkillCategory.Crud) =>
        new(name, name, category, Array.Empty<SkillParameter>(), Array.Empty<string>(), Array.Empty<LLMCapability>(), null);

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditClients" }
    };

    private static EntityChangeNotifier Sut(IAssistantNotificationService notif) =>
        new(new SkillRiskClassifier(), notif, NullLogger<EntityChangeNotifier>.Instance);

    [Test]
    public async Task MutatingSkill_NotifiesAffectedEntityAndOperation()
    {
        var notif = Substitute.For<IAssistantNotificationService>();

        await Sut(notif).NotifyExecutedAsync(Descriptor("delete_client"), Ctx(), SkillResult.SuccessResult(null));

        await notif.Received(1).SendEntityChangedAsync(
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(e => e.Contains("client")),
            "delete",
            "delete_client");
    }

    [Test]
    public async Task SettingsSkill_NotInEntityMap_DerivesEntityFromSkillName()
    {
        var notif = Substitute.For<IAssistantNotificationService>();

        await Sut(notif).NotifyExecutedAsync(Descriptor("create_macro"), Ctx(), SkillResult.SuccessResult(null));

        await notif.Received(1).SendEntityChangedAsync(
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(e => e.Contains("macro")),
            "create",
            "create_macro");
    }

    [Test]
    public async Task ReadOnlySkill_DoesNotNotify()
    {
        var notif = Substitute.For<IAssistantNotificationService>();

        await Sut(notif).NotifyExecutedAsync(Descriptor("get_client_details", SkillCategory.Query), Ctx(), SkillResult.SuccessResult(null));

        await notif.DidNotReceive().SendEntityChangedAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task ScenarioGatedSkill_DoesNotNotify_PendingHumanAcceptance()
    {
        var notif = Substitute.For<IAssistantNotificationService>();

        await Sut(notif).NotifyExecutedAsync(Descriptor("start_wizard1"), Ctx(), SkillResult.SuccessResult(null));

        await notif.DidNotReceive().SendEntityChangedAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task ScheduleEntitySkill_DoesNotNotify_DeferredToWorkNotificationsHub()
    {
        var notif = Substitute.For<IAssistantNotificationService>();

        await Sut(notif).NotifyExecutedAsync(Descriptor("add_break"), Ctx(), SkillResult.SuccessResult(null));

        await notif.DidNotReceive().SendEntityChangedAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task FailedSkill_DoesNotNotify()
    {
        var notif = Substitute.For<IAssistantNotificationService>();

        await Sut(notif).NotifyExecutedAsync(Descriptor("delete_client"), Ctx(), SkillResult.Error("nope"));

        await notif.DidNotReceive().SendEntityChangedAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>());
    }
}
