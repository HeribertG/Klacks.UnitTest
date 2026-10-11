// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// LLMFunctionExecutor lifts the replayed skill name of a confirm_pending_action result onto the function call,
/// while the call keeps the wrapper's own name.
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
public class LLMFunctionExecutorReplayedSkillNameTests
{
    private const string ReplayedSkill = "update_contract";

    private ILLMSkillBridge _skillBridge = null!;
    private LLMFunctionExecutor _executor = null!;

    [SetUp]
    public void SetUp()
    {
        _skillBridge = Substitute.For<ILLMSkillBridge>();
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

    private static LLMContext Ctx() => new()
    {
        Message = "Ja, bitte.",
        UserId = Guid.NewGuid().ToString(),
        UserRights = new List<string>()
    };

    [Test]
    public async Task AConfirmedHeldAction_KeepsTheWrapperName_AndCarriesTheReplayedSkill()
    {
        _skillBridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new SkillBridgeResult { Success = true, Message = "done", ReplayedSkillName = ReplayedSkill });
        var call = new LLMFunctionCall { FunctionName = AutonomyDefaults.ConfirmPendingActionSkillName };

        await _executor.ProcessFunctionCallsAsync(Ctx(), new List<LLMFunctionCall> { call });

        call.FunctionName.ShouldBe(AutonomyDefaults.ConfirmPendingActionSkillName);
        call.ReplayedSkillName.ShouldBe(ReplayedSkill);
        call.Success.ShouldBeTrue();
    }

    [Test]
    public async Task AnOrdinaryCall_CarriesNoReplayedSkill()
    {
        _skillBridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new SkillBridgeResult { Success = true, Message = "done" });
        var call = new LLMFunctionCall { FunctionName = ReplayedSkill };

        await _executor.ProcessFunctionCallsAsync(Ctx(), new List<LLMFunctionCall> { call });

        call.ReplayedSkillName.ShouldBeNull();
    }
}
