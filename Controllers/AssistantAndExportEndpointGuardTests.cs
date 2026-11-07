// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the authorization and rate-limit attributes of the agent writes, the sealed order list and the speech
/// endpoints: changing the default agent or listing sealed orders is Admin-only, and STT, TTS and the transcription
/// cleanup cannot be called without a limit that caps the spend of the configured providers.
/// </summary>

using System.Reflection;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Controllers.Assistant;
using Klacks.Api.Presentation.Controllers.UserBackend.Exports;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class AssistantAndExportEndpointGuardTests
{
    private static MethodInfo Action(Type controller, string method) =>
        controller.GetMethod(method, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"{controller.Name}.{method} not found");

    [TestCase(typeof(AgentsController), nameof(AgentsController.Create))]
    [TestCase(typeof(AgentsController), nameof(AgentsController.Update))]
    [TestCase(typeof(OrderExportController), nameof(OrderExportController.Orders))]
    public void Action_IsAdminOnlyWithThePinnedJwtScheme(Type controller, string method)
    {
        var authorize = Action(controller, method).GetCustomAttribute<AuthorizeAttribute>(inherit: false);

        authorize.ShouldNotBeNull($"{controller.Name}.{method} must carry its own [Authorize]");
        authorize.Roles.ShouldBe(Roles.Admin);
        authorize.AuthenticationSchemes.ShouldBe(JwtBearerDefaults.AuthenticationScheme);
    }

    [TestCase(typeof(SttController))]
    [TestCase(typeof(TtsController))]
    public void SpeechController_IsRateLimitedWithTheSpeechPolicy(Type controller)
    {
        var rateLimiting = controller.GetCustomAttribute<EnableRateLimitingAttribute>(inherit: false);

        rateLimiting.ShouldNotBeNull($"{controller.Name} must carry [EnableRateLimiting]");
        rateLimiting.PolicyName.ShouldBe(RateLimitingPolicies.Speech);
    }

    [Test]
    public void TranscriptionEnhance_IsRateLimitedWithTheLlmChatPolicy()
    {
        var rateLimiting = Action(typeof(TranscriptionController), nameof(TranscriptionController.Enhance))
            .GetCustomAttribute<EnableRateLimitingAttribute>(inherit: false);

        rateLimiting.ShouldNotBeNull("TranscriptionController.Enhance must carry [EnableRateLimiting]");
        rateLimiting.PolicyName.ShouldBe(RateLimitingPolicies.LlmChat);
    }
}
