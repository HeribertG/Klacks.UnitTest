// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for GateOfferDetector: a turn offers an explanation only when it ENDED at a gate - its last executed call is a held
/// confirmation of a topic's gate skill (the offer carries the issued token), or a successful apply=false preview of a
/// topic's preview skill (no token). A held confirmation of a preview skill, a confirmation of an unrelated skill, an
/// apply=true execution, a failed preview, a call the loop rejected or a stop skipped, and a gate followed by another
/// executed call are no gate.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class GateOfferDetectorTests
{
    private const string Token = "issued-token";

    private static LLMFunctionCall Held(string skill) => new()
    {
        FunctionName = skill,
        Success = false,
        RequiresConfirmation = true,
        IssuedConfirmationToken = Token
    };

    private static LLMFunctionCall Preview(string skill, bool success = true, bool? apply = false)
    {
        var call = new LLMFunctionCall { FunctionName = skill, Success = success };
        if (apply.HasValue)
        {
            call.Parameters[PreviewApplySkillCalls.ApplyParameter] = apply.Value;
        }

        return call;
    }

    [TestCase("accept_scenario", ExplainOfferTopics.ScenarioAcceptKey)]
    [TestCase("bulk_delete_works", ExplainOfferTopics.DeleteFinalKey)]
    [TestCase("create_plan", ExplainOfferTopics.PlanKey)]
    [TestCase("apply_planning_profile", ExplainOfferTopics.PlanningCommandsKey)]
    public void AHeldConfirmationOfAGateSkill_IsThatTopicsGate_AndCarriesTheIssuedToken(string skill, string topicKey)
    {
        var gate = GateOfferDetector.Detect([Held(skill)])!;

        gate.Topic.Key.ShouldBe(topicKey);
        gate.GateSkillName.ShouldBe(skill);
        gate.ConfirmationToken.ShouldBe(Token);
    }

    [Test]
    public void AHeldConfirmationOfThePreviewSkill_IsNoGate()
    {
        GateOfferDetector.Detect([Held("seal_open_orders")]).ShouldBeNull();
    }

    [Test]
    public void ASuccessfulPreviewOfThePreviewSkill_IsTheSealingGate_WithoutAToken()
    {
        var gate = GateOfferDetector.Detect([Preview("seal_open_orders")])!;

        gate.Topic.Key.ShouldBe(ExplainOfferTopics.SealingKey);
        gate.GateSkillName.ShouldBe("seal_open_orders");
        gate.ConfirmationToken.ShouldBeNull();
        GateOfferDetector.Detect([Preview("seal_open_orders", apply: null)])!.Topic.Key.ShouldBe(ExplainOfferTopics.SealingKey);
    }

    [Test]
    public void AnExecutedApply_AFailedPreviewAndAPreviewOfAnotherSkill_AreNoGate()
    {
        GateOfferDetector.Detect([Preview("seal_open_orders", apply: true)]).ShouldBeNull();
        GateOfferDetector.Detect([Preview("seal_open_orders", success: false)]).ShouldBeNull();
        GateOfferDetector.Detect([Preview("assign_orders_to_groups")]).ShouldBeNull();
    }

    [Test]
    public void AHeldConfirmationOfAnUnrelatedSkill_IsNoGate()
    {
        GateOfferDetector.Detect([Held("delete_system_user")]).ShouldBeNull();
    }

    [Test]
    public void AGateFollowedByAnotherExecutedCall_IsNoGateAnymore()
    {
        var read = new LLMFunctionCall { FunctionName = "get_scenarios", Success = true };

        GateOfferDetector.Detect([Held("accept_scenario"), read]).ShouldBeNull();
    }

    [Test]
    public void RejectedAndSkippedCalls_AreNotTheLastExecutedCall()
    {
        var rejected = new LLMFunctionCall { FunctionName = "accept_scenario", IsRejectedRepeat = true, Success = false };
        var skipped = new LLMFunctionCall { FunctionName = "get_scenarios", SkippedByStop = true, Success = false };

        GateOfferDetector.Detect([Held("bulk_delete_works"), rejected, skipped])!.Topic.Key
            .ShouldBe(ExplainOfferTopics.DeleteFinalKey);
        GateOfferDetector.Detect([rejected]).ShouldBeNull();
    }

    [Test]
    public void NoCalls_AreNoGate()
    {
        GateOfferDetector.Detect(null).ShouldBeNull();
        GateOfferDetector.Detect([]).ShouldBeNull();
    }
}
