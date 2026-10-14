// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// LLMFunctionExecutor carries the capped channel of the turn (LLMContext.CappedChannel) onto the SkillExecutionContext of
/// every skill call, so the skill executor can refuse the skills the channel withholds; a turn of a signed-in person
/// carries none.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMFunctionExecutorCappedChannelTests
{
    private ILLMSkillBridge _skillBridge = null!;
    private LLMFunctionExecutor _executor = null!;

    [SetUp]
    public void SetUp()
    {
        _skillBridge = Substitute.For<ILLMSkillBridge>();
        _skillBridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new SkillBridgeResult { Success = true, Message = "done" });
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

    private static LLMContext Ctx(string? cappedChannel) => new()
    {
        Message = "Zeig mir die Nachrichten",
        UserId = Guid.NewGuid().ToString(),
        UserRights = new List<string>(),
        CappedChannel = cappedChannel
    };

    [Test]
    public async Task ACappedChannelTurn_PassesTheChannelOnToTheSkillContext()
    {
        var call = new LLMFunctionCall { FunctionName = "read_messages" };

        await _executor.ProcessFunctionCallsAsync(Ctx(CappedChannels.SlackOwnerBridge), new List<LLMFunctionCall> { call });

        await _skillBridge.Received(1).ExecuteSkillFromLLMCallAsync(
            Arg.Any<LLMFunctionCall>(),
            Arg.Is<SkillExecutionContext>(c => c.CappedChannel == CappedChannels.SlackOwnerBridge),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnOrdinaryTurn_CarriesNoChannel()
    {
        var call = new LLMFunctionCall { FunctionName = "read_messages" };

        await _executor.ProcessFunctionCallsAsync(Ctx(null), new List<LLMFunctionCall> { call });

        await _skillBridge.Received(1).ExecuteSkillFromLLMCallAsync(
            Arg.Any<LLMFunctionCall>(),
            Arg.Is<SkillExecutionContext>(c => c.CappedChannel == null),
            Arg.Any<CancellationToken>());
    }
}
