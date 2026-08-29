// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Keeps AgentTriggerKinds.All in step with the constants of AgentTriggerKinds itself, and pins the
/// consequence of a gap at the place where it hurts. AgentTriggerPreferencesController validates every
/// PUT against that list, so a kind added to the class but forgotten in All is a kind the user is
/// offered a mute for - DismissStreakEvaluator suggests one for any kind dismissed three times in a row
/// - and then cannot mute, because the endpoint answers 400. That is exactly what happened while the
/// controller carried its own hand-written list of ten of the twenty-two kinds.
///
/// Reflection reads const fields only (IsLiteral and not IsInitOnly), which is what keeps All - a
/// static readonly field on the same class - out of its own comparison. It is also aimed at
/// typeof(AgentTriggerKinds) rather than the namespace, because the same file declares
/// AgentTriggerSeverity, whose high/medium/low are not trigger kinds.
///
/// The endpoint assertion instantiates the controller instead of comparing collections: reading
/// AgentTriggerKinds.All back off the controller would be tautological now that the controller uses it,
/// whereas driving UpdatePreference still catches a second rejection gate added later.
/// </summary>

using System.Reflection;
using System.Security.Claims;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Controllers.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class AgentTriggerKindsAllGuardTests
{
    private const string CurrentUserId = "33333333-3333-3333-3333-333333333333";
    private const int MinimumExpectedKinds = 20;

    private static IReadOnlyList<string> DeclaredConstants() =>
        typeof(AgentTriggerKinds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

    [Test]
    public void All_ContainsEveryDeclaredTriggerKindConstant()
    {
        // Arrange
        var declared = DeclaredConstants().ToHashSet(StringComparer.Ordinal);

        // Act
        var all = AgentTriggerKinds.All.ToHashSet(StringComparer.Ordinal);

        // Assert
        var missing = declared.Except(all).OrderBy(kind => kind, StringComparer.Ordinal).ToList();
        var stale = all.Except(declared).OrderBy(kind => kind, StringComparer.Ordinal).ToList();

        missing.ShouldBeEmpty(
            "AgentTriggerKinds declares a const that AgentTriggerKinds.All omits, so "
            + "AgentTriggerPreferencesController rejects a PUT for that kind with 400 while the assistant "
            + "keeps offering to mute it. Missing: " + string.Join(", ", missing));

        stale.ShouldBeEmpty(
            "AgentTriggerKinds.All names a value that is no longer a const of AgentTriggerKinds, so the "
            + "endpoint accepts and stores a preference nothing will ever read. Stale: "
            + string.Join(", ", stale));
    }

    [Test]
    public void All_HasNoDuplicatesAndFindsEnoughConstantsToMeanSomething()
    {
        var declared = DeclaredConstants();

        declared.Count.ShouldBeGreaterThan(
            MinimumExpectedKinds,
            "Reflection found almost no trigger-kind constants, so the set comparison above would pass "
            + "vacuously.");

        AgentTriggerKinds.All.Count.ShouldBe(
            AgentTriggerKinds.All.Distinct(StringComparer.Ordinal).Count(),
            "AgentTriggerKinds.All lists the same kind twice, which a set comparison cannot see. A "
            + "duplicate means a copy-paste slipped in and some other kind was probably meant.");

        declared.ShouldAllBe(kind => !string.IsNullOrWhiteSpace(kind));
    }

    [Test]
    public async Task UpdatePreference_AcceptsEveryKindInAll()
    {
        // Arrange
        var controller = CreateController();

        // Act
        var rejected = new List<string>();
        foreach (var kind in AgentTriggerKinds.All)
        {
            var result = await controller.UpdatePreference(kind, new UpdateTriggerPreferenceRequest { Muted = true });
            if (result is not OkObjectResult)
            {
                rejected.Add($"{kind} -> {result.GetType().Name}");
            }
        }

        // Assert
        rejected.ShouldBeEmpty(
            "AgentTriggerPreferencesController refused a kind it publishes as known, so the user is shown "
            + "a mute button that fails. Rejected: " + string.Join(", ", rejected));
    }

    [Test]
    public async Task UpdatePreference_StillRejectsAKindThatDoesNotExist()
    {
        var controller = CreateController();

        var result = await controller.UpdatePreference(
            "not_a_trigger_kind", new UpdateTriggerPreferenceRequest { Muted = true });

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    private static AgentTriggerPreferencesController CreateController()
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, CurrentUserId) }, "Test");

        return new AgentTriggerPreferencesController(new InMemoryAgentTriggerPreferenceService(TimeProvider.System))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            }
        };
    }
}
