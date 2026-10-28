// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins which endpoints answer HTTP 403 on the public Playground: every visitor is the shared demo admin, so the
/// endpoints that take over or lock that account, write operator-paid keys or the update channel, store visitor text in
/// the agent memory or rules, reach configured mail or identity servers, or run migrations are blocked; the endpoints the
/// UI needs for the normal session (login, token refresh, logout, token validation, listing) stay open.
/// Also blocked: the writes that change what Klacksy learned or may do for later visitors (learning curation, skill
/// relations, transcription dictionary, proactive governance, standing approvals, plan creation, escalation cancel).
/// Kept open on purpose (UI needs them): plan approve and abort, onboarding state (its setup kind is refused by its own
/// filter, see BlockedSetupOnboardingOnPlaygroundFilterTests), delegate, ui-action-result.
/// The blocked accounts actions include ChangePassword and ChangePasswordUser, which carry ExemptFromAdminSetupGate only so
/// an installation can finish its first-admin setup; AdminSetupGateService is exempt on the Playground, so nothing there
/// depends on them.
/// </summary>

using System.Reflection;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Klacks.Api.Presentation.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundBlockedEndpointsTests
{
    private static Type Controller(string name) =>
        typeof(BaseController).Assembly.GetTypes().Single(t => t.Name == name && typeof(ControllerBase).IsAssignableFrom(t));

    private static MethodInfo Action(string controller, string method) =>
        Controller(controller).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(m => m.Name == method);

    private static bool IsBlocked(MethodInfo action) =>
        action.GetCustomAttribute<BlockedOnPlaygroundAttribute>() is not null
        || action.DeclaringType!.GetCustomAttribute<BlockedOnPlaygroundAttribute>() is not null;

    [TestCase("GeneralSettingsController", "TestEmailConfiguration")]
    [TestCase("ReceivedEmailController", "TestImapConnection")]
    [TestCase("ReceivedEmailController", "Translate")]
    [TestCase("IdentityProvidersController", "PostIdentityProvider")]
    [TestCase("IdentityProvidersController", "PutIdentityProvider")]
    [TestCase("IdentityProvidersController", "TestConnection")]
    [TestCase("IdentityProvidersController", "SyncClients")]
    [TestCase("FeaturePluginController", "EnablePlugin")]
    [TestCase("FeaturePluginController", "DisablePlugin")]
    [TestCase("UpdateController", "SaveConfig")]
    [TestCase("UpdateController", "Trigger")]
    [TestCase("UpdateController", "Rollback")]
    [TestCase("UpdateController", "Cancel")]
    [TestCase("UpdateController", "Delete")]
    [TestCase("AccountsController", "ChangePassword")]
    [TestCase("AccountsController", "ChangePasswordUser")]
    [TestCase("AccountsController", "ChangeRoleUser")]
    [TestCase("AccountsController", "DeactivateAccountUser")]
    [TestCase("AccountsController", "ReactivateAccountUser")]
    [TestCase("AccountsController", "UpdateAccount")]
    [TestCase("AccountsController", "RegisterUser")]
    [TestCase("AccountsController", "RequestPasswordReset")]
    [TestCase("AccountsController", "ResetPassword")]
    [TestCase("PersonalAccessTokensController", "Create")]
    [TestCase("PersonalAccessTokensController", "Revoke")]
    [TestCase("KlacksBotTokensController", "Create")]
    [TestCase("KlacksBotTokensController", "Revoke")]
    [TestCase("EvalController", "SubmitCorrection")]
    [TestCase("EvalController", "SubmitHelpfulFeedback")]
    [TestCase("AgentPlansController", "CreateAndStartPlan")]
    [TestCase("EscalationChainsController", "Cancel")]
    [TestCase("KlacksyLearningController", "UpdatePhrase")]
    [TestCase("KlacksyLearningController", "DeletePhrase")]
    [TestCase("KlacksyLearningController", "ApprovePhraseProposal")]
    [TestCase("KlacksyLearningController", "UpdateCapability")]
    [TestCase("KlacksyLearningController", "DeleteCapability")]
    [TestCase("KlacksyLearningController", "DismissUnfulfillable")]
    [TestCase("KlacksyLearningController", "MarkExported")]
    [TestCase("ProactiveGovernanceController", "Put")]
    [TestCase("ProactiveStandingApprovalsController", "Post")]
    [TestCase("ProactiveStandingApprovalsController", "Delete")]
    [TestCase("SkillRelationsController", "Accept")]
    [TestCase("SkillRelationsController", "Dismiss")]
    [TestCase("TranscriptionDictionaryController", "Create")]
    [TestCase("TranscriptionDictionaryController", "Update")]
    [TestCase("TranscriptionDictionaryController", "Delete")]
    public void WriteOrCostEndpoint_IsBlockedOnPlayground(string controller, string method)
    {
        IsBlocked(Action(controller, method)).ShouldBeTrue($"{controller}.{method}");
    }

    [TestCase("DatabaseController")]
    [TestCase("AgentsController")]
    [TestCase("AgentMemoriesController")]
    [TestCase("AgentSoulController")]
    [TestCase("GlobalRulesController")]
    public void WholeController_IsBlockedOnPlayground(string controller)
    {
        var type = Controller(controller);

        type.GetCustomAttribute<BlockedOnPlaygroundAttribute>().ShouldNotBeNull(controller);
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes().Any(a => a is HttpMethodAttribute))
            .ShouldAllBe(m => IsBlocked(m));
    }

    [TestCase("AccountsController", "LoginUser")]
    [TestCase("AccountsController", "RefreshToken")]
    [TestCase("AccountsController", "Logout")]
    [TestCase("AccountsController", "ValidateToken")]
    [TestCase("AccountsController", "GetUserList")]
    [TestCase("AccountsController", "GenerateUsername")]
    [TestCase("AccountsController", "ValidatePasswordResetToken")]
    [TestCase("PersonalAccessTokensController", "GetOwnTokens")]
    [TestCase("KlacksBotTokensController", "Get")]
    [TestCase("EvalController", "ReportUiActionResult")]
    [TestCase("EvalController", "ReportNavigationOutcome")]
    [TestCase("EvalController", "TurnOptions")]
    [TestCase("AgentPlansController", "ApproveAndContinue")]
    [TestCase("AgentPlansController", "AbortPlan")]
    [TestCase("AgentPlansController", "ListMyPlans")]
    [TestCase("ChatController", "SaveOnboardingState")]
    [TestCase("ProactiveMessagesController", "Delegate")]
    [TestCase("EscalationChainsController", "GetRunning")]
    [TestCase("KlacksyLearningController", "GetPhrases")]
    [TestCase("KlacksyLearningController", "GetCapabilities")]
    [TestCase("KlacksyLearningController", "GetUnfulfillable")]
    [TestCase("ProactiveGovernanceController", "Get")]
    [TestCase("ProactiveStandingApprovalsController", "Get")]
    [TestCase("SkillRelationsController", "GetAll")]
    [TestCase("TranscriptionDictionaryController", "GetAll")]
    [TestCase("UpdateController", "GetStatus")]
    [TestCase("UpdateController", "GetConfig")]
    public void SessionOrReadEndpoint_StaysOpen(string controller, string method)
    {
        IsBlocked(Action(controller, method)).ShouldBeFalse($"{controller}.{method} is needed by the normal session");
    }

    [TestCase("TranscriptionDictionaryController", "Create")]
    [TestCase("SkillRelationsController", "Accept")]
    [TestCase("ProactiveStandingApprovalsController", "Post")]
    [TestCase("ProactiveGovernanceController", "Put")]
    [TestCase("KlacksyLearningController", "DeletePhrase")]
    [TestCase("EscalationChainsController", "Cancel")]
    [TestCase("AgentPlansController", "CreateAndStartPlan")]
    public async Task BlockedAssistantEndpoint_AnswersForbiddenWithModeOn_AndRunsWithModeOff(string controller, string method)
    {
        var attribute = Action(controller, method).GetCustomAttribute<BlockedOnPlaygroundAttribute>();
        attribute.ShouldNotBeNull($"{controller}.{method}");
        attribute.ServiceType.ShouldBe(typeof(BlockedOnPlaygroundFilter));

        var mode = Substitute.For<IPlaygroundMode>();
        var filter = new BlockedOnPlaygroundFilter(mode);
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        var ran = false;
        ActionExecutionDelegate next = () =>
        {
            ran = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object()));
        };

        mode.IsActive.Returns(true);
        var blocked = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());
        await filter.OnActionExecutionAsync(blocked, next);

        ran.ShouldBeFalse();
        blocked.Result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);

        mode.IsActive.Returns(false);
        var open = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());
        await filter.OnActionExecutionAsync(open, next);

        ran.ShouldBeTrue();
        open.Result.ShouldBeNull();
    }

    [Test]
    public void ChatEndpoints_AreNotBlocked_TheyAreBudgetedInstead()
    {
        var chat = Controller("ChatController");

        chat.GetCustomAttribute<BlockedOnPlaygroundAttribute>().ShouldBeNull();
        IsBlocked(Action("ChatController", "ProcessMessageStream")).ShouldBeFalse();
    }
}
