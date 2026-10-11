// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The bridge carries the name of the skill a wrapper (confirm_pending_action) replayed from the result metadata to
/// the chat loop, so the previous-action record can name the skill that actually ran - and only from that wrapper.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.Domain.Services.Assistant.Skills.Adapters;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant.Skills;

[TestFixture]
public class LLMSkillBridgeReplayedSkillNameTests
{
    private const string ReplayedSkill = "update_contract";

    private ISkillExecutor _skillExecutor = null!;
    private LLMSkillBridge _bridge = null!;

    [SetUp]
    public void SetUp()
    {
        _skillExecutor = Substitute.For<ISkillExecutor>();
        _bridge = new LLMSkillBridge(
            Substitute.For<ISkillRegistry>(),
            _skillExecutor,
            Substitute.For<ISkillAdapterFactory>(),
            Substitute.For<ILogger<LLMSkillBridge>>());
    }

    [Test]
    public async Task AReplayingWrappersResult_CarriesTheReplayedSkillName()
    {
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "Done.") with
            {
                Metadata = new Dictionary<string, object> { [SkillResultMetadataKeys.ReplayedSkillName] = ReplayedSkill }
            });

        var result = await _bridge.ExecuteSkillFromLLMCallAsync(
            new LLMFunctionCall { FunctionName = AutonomyDefaults.ConfirmPendingActionSkillName }, Context());

        result.ReplayedSkillName.ShouldBe(ReplayedSkill);
    }

    [Test]
    public async Task AnOrdinaryResult_CarriesNoReplayedSkillName()
    {
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "Done."));

        var result = await _bridge.ExecuteSkillFromLLMCallAsync(
            new LLMFunctionCall { FunctionName = ReplayedSkill }, Context());

        result.ReplayedSkillName.ShouldBeNull();
    }

    [Test]
    public async Task AReplayedSkillNameFromAnyOtherSkill_IsIgnored()
    {
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "Done.") with
            {
                Metadata = new Dictionary<string, object> { [SkillResultMetadataKeys.ReplayedSkillName] = ReplayedSkill }
            });

        var result = await _bridge.ExecuteSkillFromLLMCallAsync(
            new LLMFunctionCall { FunctionName = "run_analysis" }, Context());

        result.ReplayedSkillName.ShouldBeNull();
    }

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = nameof(LLMSkillBridgeReplayedSkillNameTests),
        UserPermissions = Array.Empty<string>()
    };
}
