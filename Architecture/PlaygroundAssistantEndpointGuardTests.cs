// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Reflection guard for the assistant API on the public Playground. Every visitor is the shared demo admin, so every
/// action below the route prefix api/backend/assistant must either carry BlockedOnPlayground / BlockedSettingKeysOnPlayground
/// (on the action or its controller) or appear in the exact allowlist below with the reason it stays open. A new endpoint
/// that is neither blocked nor listed turns the test red, and an allowlist entry that no longer matches an open endpoint
/// does too. Endpoints that persist visitor free text or change state shared by all visitors are blocked unless the UI
/// needs them in the normal Playground session; the reason of such an open entry names the owner question. Scope: only controllers with a class-level Route below the prefix; other routes
/// (accounts, settings, plugins, internal) are pinned by PlaygroundBlockedEndpointsTests.
/// </summary>

using System.Reflection;
using System.Text;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Klacks.Api.Presentation.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class PlaygroundAssistantEndpointGuardTests
{
    private const string RoutePrefix = "api/backend/assistant";
    private const int MinimumScannedActions = 100;
    private const string AnyVerb = "ANY";

    private static readonly IReadOnlyDictionary<string, string> OpenEndpoints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["GET api/backend/assistant/autonomy-level"] = "Read-only.",
        ["PUT api/backend/assistant/autonomy-level"] = "Sets the autonomy level of the demo admin; an enum value, no free text.",
        ["GET api/backend/assistant/plans"] = "Read-only.",
        ["GET api/backend/assistant/plans/{id:guid}"] = "Read-only.",
        ["POST api/backend/assistant/plans/{id:guid}/abort"] = "Needed by the plan panel of the chat for plans the chat skill drafts; acts only on a plan of the calling account and stores no free text.",
        ["POST api/backend/assistant/plans/{id:guid}/approve"] = "Needed by the plan panel of the chat to resume a plan the chat skill paused for approval; acts only on a plan of the calling account and stores no free text.",
        ["GET api/backend/assistant/trigger-preferences"] = "Read-only.",
        ["PUT api/backend/assistant/trigger-preferences/{triggerKind}"] = "Per-trigger preference flag of the demo admin; no free text.",
        ["POST api/backend/assistant/chat"] = "The chat itself: token-budgeted, prompt-limited, pinned model, in-memory history.",
        ["POST api/backend/assistant/chat/execute-function"] = "Direct skill call: token-budgeted and prompt-limited; skills that write operator-paid keys check the mode themselves.",
        ["POST api/backend/assistant/chat/execute-functions-batch"] = "Direct skill call: token-budgeted and prompt-limited; skills that write operator-paid keys check the mode themselves.",
        ["GET api/backend/assistant/chat/function-definitions"] = "Read-only.",
        ["GET api/backend/assistant/chat/functions"] = "Read-only.",
        ["GET api/backend/assistant/chat/help"] = "Read-only.",
        ["POST api/backend/assistant/chat/onboarding/explain-depth/advanced"] = "Explain-depth preference flag; no free text.",
        ["POST api/backend/assistant/chat/onboarding/state"] = "Needed by the onboarding tour; stores a status and station ids, no visitor free text. The installation-wide setup kind is answered 403 on the Playground by BlockedSetupOnboardingOnPlayground (pinned by BlockedSetupOnboardingOnPlaygroundFilterTests); only the per-account intro kind goes through.",
        ["POST api/backend/assistant/chat/stream"] = "The chat itself: token-budgeted, prompt-limited, pinned model, in-memory history.",
        ["POST api/backend/assistant/chat/turns/{turnId:guid}/cancel"] = "Stops the caller's own running turn.",
        ["GET api/backend/assistant/chat/warmup"] = "Read-only.",
        ["GET api/backend/assistant/chat/welcome"] = "Read-only.",
        ["GET api/backend/assistant/stt/providers/custom"] = "Read-only.",
        ["GET api/backend/assistant/docs"] = "Read-only.",
        ["GET api/backend/assistant/docs/{docName}"] = "Read-only.",
        ["GET api/backend/assistant/docs/{docName}/raw"] = "Read-only.",
        ["GET api/backend/assistant/escalation-chains"] = "Read-only.",
        ["GET api/backend/assistant/escalation-chains/unresolved"] = "Read-only.",
        ["PUT api/backend/assistant/escalation-chains/{id:guid}/acknowledge"] = "Acknowledges an escalation chain; no free text.",
        ["GET api/backend/assistant/escalation-roster"] = "Read-only.",
        ["PUT api/backend/assistant/escalation-roster/Reorder"] = "Reorders existing roster members; ids only.",
        ["GET api/backend/assistant/eval/goldset-candidates"] = "Read-only.",
        ["POST api/backend/assistant/eval/navigation-outcome"] = "The UI reports every navigation outcome; the logger behind it is the Playground-silent decorator, so nothing is stored.",
        ["GET api/backend/assistant/eval/runs"] = "Read-only.",
        ["GET api/backend/assistant/eval/skill-effectiveness"] = "Read-only.",
        ["POST api/backend/assistant/eval/turn-options"] = "Read-only lookup of the skills offered in the caller's captured turn (POST because the body carries the utterance).",
        ["POST api/backend/assistant/eval/ui-action-result"] = "Called by the UI after every UI action (pinned open by PlaygroundBlockedEndpointsTests); only updates the caller's own skill-usage row, error text capped at 500 characters. Owner question: drop the text on the Playground.",
        ["GET api/backend/assistant/goal-candidates"] = "Read-only.",
        ["PUT api/backend/assistant/goal-candidates/{id:guid}/decision"] = "Decision flag on a goal candidate; the goal-reflection services are switched off on the Playground stack.",
        ["GET api/backend/assistant/learning/capabilities"] = "Read-only.",
        ["GET api/backend/assistant/learning/export-candidates"] = "Read-only.",
        ["GET api/backend/assistant/learning/phrases"] = "Read-only.",
        ["GET api/backend/assistant/learning/run-status"] = "Read-only.",
        ["GET api/backend/assistant/learning/unfulfillable"] = "Read-only.",
        ["GET api/backend/assistant/sync-notifications"] = "Read-only.",
        ["GET api/backend/assistant/sync-notifications/history"] = "Read-only.",
        ["POST api/backend/assistant/sync-notifications/mark-read"] = "Marks the sync notifications read; no free text.",
        ["GET api/backend/assistant/models"] = "Read-only.",
        ["POST api/backend/assistant/proactive-conditions/attributions"] = "Lookup of condition attributions for given ids (GetAttributions); no write.",
        ["GET api/backend/assistant/proactive-governance"] = "Read-only.",
        ["GET api/backend/assistant/proactive-messages"] = "Read-only.",
        ["PUT api/backend/assistant/proactive-messages/read"] = "Inbox state of a proactive message (read, acknowledge, reaction); no free text.",
        ["PUT api/backend/assistant/proactive-messages/read-all"] = "Inbox state of a proactive message (read, acknowledge, reaction); no free text.",
        ["GET api/backend/assistant/proactive-messages/unread-count"] = "Read-only.",
        ["PUT api/backend/assistant/proactive-messages/{id:guid}/acknowledge"] = "Inbox state of a proactive message (read, acknowledge, reaction); no free text.",
        ["PUT api/backend/assistant/proactive-messages/{id:guid}/delegate"] = "Used by the delegate button of a proactive finding in the chat; stores an enum cap, no free text, but approves a remediation that the AgentTrigger service then executes on the demo data. Owner question.",
        ["PUT api/backend/assistant/proactive-messages/{id:guid}/reaction"] = "Inbox state of a proactive message (read, acknowledge, reaction); no free text.",
        ["PUT api/backend/assistant/proactive-messages/{id:guid}/read"] = "Inbox state of a proactive message (read, acknowledge, reaction); no free text.",
        ["GET api/backend/assistant/proactive-standing-approvals"] = "Read-only.",
        ["GET api/backend/assistant/providers"] = "Read-only.",
        ["GET api/backend/assistant/providers/{id}"] = "Read-only.",
        ["GET api/backend/assistant/setup-state"] = "Read-only.",
        ["GET api/backend/assistant/skill-relations"] = "Read-only.",
        ["POST api/backend/assistant/transcription/enhance"] = "Transcription cleanup: token-budgeted and prompt-limited.",
        ["GET api/backend/assistant/transcription/dictionary"] = "Read-only.",
        ["POST api/backend/assistant/tts/synthesize"] = "The provider is forced to Edge on the Playground, so the call costs the operator nothing.",
        ["POST api/backend/assistant/tts/test"] = "The provider is forced to Edge on the Playground, so the call costs the operator nothing.",
        ["GET api/backend/assistant/tts/voices"] = "Read-only.",
        ["GET api/backend/assistant/explain-offers/{topicKey}"] = "Read-only.",
        ["POST api/backend/assistant/explain-offers/{topicKey}/reaction"] = "Reaction on an explain offer; no free text.",
        ["GET api/backend/assistant/usage"] = "Read-only.",
        ["GET api/backend/assistant/usage/export"] = "Read-only.",
        ["GET api/backend/assistant/stt/whisper-plugin/status"] = "Read-only.",
    };

    [Test]
    public void EveryAssistantAction_IsBlockedOnPlaygroundOrExplicitlyAllowlisted()
    {
        var actions = ScannedActions();

        actions.Count.ShouldBeGreaterThan(
            MinimumScannedActions,
            "The scan found too few actions below the assistant prefix; a green result would prove nothing.");

        var open = actions.Where(a => !a.Blocked).Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unlisted = open.Except(OpenEndpoints.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var stale = OpenEndpoints.Keys.Except(open, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.Ordinal).ToList();

        var report = new StringBuilder();
        foreach (var key in unlisted)
        {
            report.AppendLine($"  not blocked and not allowlisted: {key}");
        }

        foreach (var key in stale)
        {
            report.AppendLine($"  allowlisted but blocked or gone: {key}");
        }

        report.Length.ShouldBe(
            0,
            "Every action below api/backend/assistant must be blocked on the Playground ([BlockedOnPlayground]) or listed " +
            "with its reason in OpenEndpoints." + Environment.NewLine + report);
    }

    [Test]
    public void EveryAllowlistEntry_CarriesAReason()
    {
        OpenEndpoints.Where(e => string.IsNullOrWhiteSpace(e.Value)).Select(e => e.Key).ShouldBeEmpty();
    }

    private static List<(string Key, bool Blocked)> ScannedActions()
    {
        var result = new List<(string, bool)>();
        var controllers = typeof(BaseController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);

        foreach (var controller in controllers)
        {
            var classRoute = controller.GetCustomAttributes<RouteAttribute>(inherit: true)
                .Select(r => r.Template)
                .FirstOrDefault(t => t.StartsWith(RoutePrefix, StringComparison.OrdinalIgnoreCase));
            if (classRoute is null)
            {
                continue;
            }

            var classBlocked = IsBlockedMarker(controller.GetCustomAttributes(inherit: true));
            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var action = ToAction(controller, classRoute, classBlocked, method);
                if (action is not null)
                {
                    result.Add(action.Value);
                }
            }
        }

        return result;
    }

    private static (string, bool)? ToAction(Type controller, string classRoute, bool classBlocked, MethodInfo method)
    {
        if (method.GetCustomAttribute<NonActionAttribute>() is not null)
        {
            return null;
        }

        var verbs = method.GetCustomAttributes<HttpMethodAttribute>().ToList();
        var routeAttribute = method.GetCustomAttribute<RouteAttribute>();
        if (verbs.Count == 0 && routeAttribute is null)
        {
            return null;
        }

        var verb = verbs.Count > 0 ? string.Join('/', verbs.SelectMany(v => v.HttpMethods)) : AnyVerb;
        var template = verbs.Select(v => v.Template).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? routeAttribute?.Template;
        var route = string.IsNullOrEmpty(template) ? classRoute : $"{classRoute}/{template}";
        var blocked = classBlocked || IsBlockedMarker(method.GetCustomAttributes(inherit: true));

        return ($"{verb} {route}", blocked);
    }

    private static bool IsBlockedMarker(IEnumerable<object> attributes) =>
        attributes.Any(a => a is BlockedOnPlaygroundAttribute or BlockedSettingKeysOnPlaygroundAttribute);
}
