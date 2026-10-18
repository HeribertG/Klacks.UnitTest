// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The post-turn seam of the explain offer, fed by the REAL LLMResponseBuilder: a held accept_scenario call goes through
/// BuildSuccessResponse (non-streaming) and SseChunk.Metadata (streaming) and must still be recognised as the gate, by the
/// detector and by the real GateExplainOfferService. Regression guard for the wire-projection trap: FunctionCalls carries
/// anonymous objects without RequiresConfirmation, so a seam that reads it never sees a gate. The typed list must stay off
/// the wire in both serializations.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.ExplainOffers;
using Klacks.Api.Application.Skills.Generic;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Security;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class GateOfferWireSeamTests
{
    private const string ConversationId = "conv-seam";
    private const string HeldToken = "seam-held-token";
    private const string ModelAnswer = "Soll ich das Szenario übernehmen? [REPLIES:single \"Ja\" | \"Nein\"]";
    private const string GermanChip = "Erst kurz erklären";
    private const string ExecutedCallsJsonName = "executedCalls";

    private IProactiveTriggerDispatchRepository _dispatches = null!;
    private GateExplainOfferService _service = null!;
    private string _userId = null!;

    [SetUp]
    public void SetUp()
    {
        AssistantTextCatalogues.ResetAll();
        _userId = Guid.NewGuid().ToString();
        _dispatches = Substitute.For<IProactiveTriggerDispatchRepository>();
        var preferences = Substitute.For<IAgentTriggerPreferenceService>();
        preferences.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        _service = new GateExplainOfferService(
            _dispatches, preferences,
            new ExplainOffersSwitch(Substitute.For<ISettingsReader>(), Substitute.For<IProactiveGovernanceResolver>()),
            new KnowledgeHappenExecutor(Substitute.For<IAgentMemoryRepository>(), Substitute.For<ILogger<KnowledgeHappenExecutor>>()),
            Substitute.For<IPendingConfirmationRenewal>(), TimeProvider.System, Substitute.For<ILogger<GateExplainOfferService>>());
    }

    [TearDown]
    public void TearDown() => AssistantTextCatalogues.ResetAll();

    private static List<LLMFunctionCall> HeldAcceptScenario() =>
    [
        new LLMFunctionCall
        {
            FunctionName = ScenarioAcceptDefaults.SkillName,
            Success = false,
            RequiresConfirmation = true,
            IssuedConfirmationToken = HeldToken
        }
    ];

    private static LLMResponse Built() =>
        new LLMResponseBuilder().BuildSuccessResponse(
            new LLMProviderResponse { Content = ModelAnswer, Usage = new Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage(), Success = true },
            ConversationId, ModelAnswer, HeldAcceptScenario());

    private LLMContext Context() => new()
    {
        UserId = _userId,
        ConversationId = ConversationId,
        Language = "de",
        Message = "Übernimm das Szenario",
        TurnId = Guid.NewGuid(),
        ExplainDepth = new ExplainDepthProfile(ExplainExperienceLevel.Beginner, false)
    };

    private static void ShouldBeTheHeldScenarioGate(GateOffer? gate)
    {
        gate.ShouldNotBeNull();
        gate!.Topic.Key.ShouldBe(ExplainOfferTopics.ScenarioAcceptKey);
        gate.GateSkillName.ShouldBe(ScenarioAcceptDefaults.SkillName);
        gate.ConfirmationToken.ShouldBe(HeldToken);
    }

    [Test]
    public void TheWireProjection_CarriesNoTypedCall_WhichIsWhyTheSeamReadsExecutedCalls()
    {
        var response = Built();

        response.FunctionCalls!.ShouldNotBeEmpty();
        response.FunctionCalls!.OfType<LLMFunctionCall>().ShouldBeEmpty();
    }

    [Test]
    public void NonStreaming_TheBuiltResponse_StillShowsTheHeldGate()
    {
        ShouldBeTheHeldScenarioGate(GateOfferDetector.Detect(Built().ExecutedCalls));
    }

    [Test]
    public void Streaming_TheMetadataChunk_StillShowsTheHeldGate()
    {
        var metadata = SseChunk.Metadata(Built());

        metadata.Type.ShouldBe(SseChunkType.Metadata);
        ShouldBeTheHeldScenarioGate(GateOfferDetector.Detect(metadata.ExecutedCalls));
    }

    [Test]
    public async Task NonStreaming_TheRealService_AddsTheChip_AndRecordsTheGate()
    {
        var response = Built();

        var replies = await _service.AugmentRepliesAsync(
            Context(), response.ConversationId, response.ExecutedCalls, response.SuggestedReplies);

        replies!.Options.Select(option => option.Label).ShouldBe(["Ja", "Nein", GermanChip]);
        response.SuggestedReplies!.Options.Count.ShouldBe(2);
        await _dispatches.Received(1).RecordAsync(
            Arg.Is<ProactiveTriggerDispatchRow>(row =>
                row.TriggerKind == ExplainOfferTopics.TriggerKindPrefix + ExplainOfferTopics.ScenarioAcceptKey
                && ExplainOfferMarker.Parse(row.ContentParamsJson).TokenFingerprint == ConfirmationTokenHasher.Hash(HeldToken)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Streaming_TheRealService_AddsTheChipToTheMetadataChunk()
    {
        var metadata = SseChunk.Metadata(Built());

        var replies = await _service.AugmentRepliesAsync(
            Context(), ConversationId, metadata.ExecutedCalls, metadata.SuggestedReplies);

        replies!.Options.Last().Label.ShouldBe(GermanChip);
        await _dispatches.Received(1).RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void TheTypedCalls_NeverReachTheWire()
    {
        var response = Built();

        var responseJson = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var chunkJson = JsonSerializer.Serialize(SseChunk.Metadata(response), SseChunkJson.Options);

        foreach (var json in new[] { responseJson, chunkJson })
        {
            json.ShouldNotContain(ExecutedCallsJsonName);
            json.ShouldNotContain(HeldToken);
        }
    }
}
