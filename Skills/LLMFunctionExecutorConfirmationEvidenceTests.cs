// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The chat loop is the only place that can prove the user answered a held-action question: it sets
/// SkillExecutionContext.UserConfirmation from the user's OWN message of the turn (AffirmationDetector.IsExplicitConfirmation),
/// never from anything the model produced. Telegram, Slack, voice and the in-app chat all reach the skill bridge through
/// this executor, so the mapping here is what makes the server-side yes work on every one of them.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class LLMFunctionExecutorConfirmationEvidenceTests
{
    private const string SkillName = "confirm_pending_action";

    private ILLMSkillBridge _skillBridge = null!;
    private LLMFunctionExecutor _executor = null!;
    private SkillExecutionContext? _seenContext;

    [SetUp]
    public void SetUp()
    {
        _seenContext = null;
        _skillBridge = Substitute.For<ILLMSkillBridge>();
        _skillBridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _seenContext = call.Arg<SkillExecutionContext>();
                return new SkillBridgeResult { Success = true, Message = "done" };
            });

        var agentRepository = Substitute.For<IAgentRepository>();
        agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns((Agent?)null);

        _executor = new LLMFunctionExecutor(
            Substitute.For<ILogger<LLMFunctionExecutor>>(),
            Substitute.For<IAgentSkillRepository>(),
            agentRepository,
            PendingStoreTestFactory.CreateConfirmationStore(),
            Substitute.For<ITurnConfirmationScope>(),
            Substitute.For<ICancellableSkillPolicy>(),
            _skillBridge);
    }

    private static LLMContext Context(string message) => new()
    {
        Message = message,
        UserId = Guid.NewGuid().ToString(),
        UserRights = new List<string>()
    };

    private async Task<UserConfirmationEvidence> EvidenceForAsync(string message)
    {
        var call = new LLMFunctionCall { FunctionName = SkillName, Parameters = new() };

        await _executor.ProcessFunctionCallsAsync(Context(message), new List<LLMFunctionCall> { call });

        _seenContext.ShouldNotBeNull();
        return _seenContext.UserConfirmation;
    }

    [TestCase("ja")]
    [TestCase("Ja bitte")]
    [TestCase("Bitte ausführen")]
    [TestCase("yes")]
    [TestCase("Please proceed")]
    [TestCase("oui")]
    [TestCase("sì")]
    public async Task AnExplicitYesOfTheUser_ProvesTheConfirmation(string message)
    {
        (await EvidenceForAsync(message)).ShouldBe(UserConfirmationEvidence.ExplicitConfirmationReply);
    }

    [TestCase("Lösche die Adresse von Müller")]
    [TestCase("Zeig mir die Dienste bitte")]
    [TestCase("nein")]
    [TestCase("ja, aber nicht heute")]
    [TestCase("ja? was kostet das")]
    [TestCase("")]
    public async Task EverythingElse_ProvesNothing(string message)
    {
        (await EvidenceForAsync(message)).ShouldBe(UserConfirmationEvidence.None);
    }
}
