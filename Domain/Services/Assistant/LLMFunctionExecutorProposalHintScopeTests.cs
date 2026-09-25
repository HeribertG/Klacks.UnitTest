// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The executor tells the turn's confirmation scope what the turn left behind, so a stopped turn can drop it:
/// every confirmation token a skill issued, wherever the skill mints it (the autonomy gate, create_plan, the
/// membership plausibility check), and every proposal hint. A hint is left when a propose-style skill
/// succeeds: for its paired apply skill, or for itself when it previews and applies through one name.
/// Nothing is recorded when nothing was left.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMFunctionExecutorProposalHintScopeTests
{
    private const string ProposeSkill = "propose_shift";
    private const string ApplySkill = "apply_shift";
    private const string SelfPairedSkill = "bulk_update";
    private const string PlainSkill = "search_employees";

    private static readonly Guid AgentId = Guid.NewGuid();

    private ITurnConfirmationScope _scope = null!;
    private IPendingConfirmationStore _pending = null!;
    private LLMFunctionExecutor _executor = null!;
    private LLMContext _context = null!;
    private bool _bridgeSucceeds;
    private string? _bridgeToken;

    [SetUp]
    public void SetUp()
    {
        _scope = Substitute.For<ITurnConfirmationScope>();
        _pending = Substitute.For<IPendingConfirmationStore>();
        _bridgeSucceeds = true;
        _bridgeToken = null;

        var agents = Substitute.For<IAgentRepository>();
        agents.GetDefaultAgentAsync().Returns(new Agent { Id = AgentId });
        var skills = Substitute.For<IAgentSkillRepository>();
        skills.GetEnabledAsync(AgentId).Returns(new List<AgentSkill>
        {
            new() { Name = ProposeSkill, PairedApplySkill = ApplySkill },
            new() { Name = SelfPairedSkill, PairedApplySkill = SelfPairedSkill },
            new() { Name = PlainSkill }
        });
        var bridge = Substitute.For<ILLMSkillBridge>();
        bridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => new SkillBridgeResult
            {
                Success = _bridgeSucceeds,
                ResultType = nameof(SkillResultType.Data),
                Message = "Done.",
                ConfirmationToken = _bridgeToken
            });

        _executor = new LLMFunctionExecutor(
            Substitute.For<ILogger<LLMFunctionExecutor>>(), skills, agents, _pending, _scope, bridge);
        _context = new LLMContext { UserId = Guid.NewGuid().ToString(), Message = "Do it." };
    }

    [Test]
    public async Task ASuccessfulProposeSkill_RecordsTheHintForItsApplySkill()
    {
        await _executor.ProcessFunctionCallsAsync(_context, [new LLMFunctionCall { FunctionName = ProposeSkill }]);

        _pending.Received(1).CreateProposalHint(Arg.Any<Guid>(), ApplySkill);
        _scope.Received(1).MarkProposalHint(ApplySkill);
    }

    [Test]
    public async Task ASelfPairedSkillsPreview_RecordsTheHintForItself()
    {
        await _executor.ProcessFunctionCallsAsync(_context, [new LLMFunctionCall { FunctionName = SelfPairedSkill }]);

        _scope.Received(1).MarkProposalHint(SelfPairedSkill);
    }

    [Test]
    public async Task AConfirmationTokenAnySkillIssued_IsRecordedOnTheScope()
    {
        _bridgeToken = "membership-token";

        await _executor.ProcessFunctionCallsAsync(_context, [new LLMFunctionCall { FunctionName = PlainSkill }]);

        _scope.Received(1).MarkIssued("membership-token");
    }

    [Test]
    public async Task AResultWithoutAConfirmationToken_RecordsNoToken()
    {
        await _executor.ProcessFunctionCallsAsync(_context, [new LLMFunctionCall { FunctionName = PlainSkill }]);

        _scope.DidNotReceiveWithAnyArgs().MarkIssued(default!);
    }

    [Test]
    public async Task ASkillWithoutAPairedApplySkill_RecordsNoHint()
    {
        await _executor.ProcessFunctionCallsAsync(_context, [new LLMFunctionCall { FunctionName = PlainSkill }]);

        _scope.DidNotReceiveWithAnyArgs().MarkProposalHint(default!);
    }

    [Test]
    public async Task AFailedProposeSkill_RecordsNoHint()
    {
        _bridgeSucceeds = false;

        await _executor.ProcessFunctionCallsAsync(_context, [new LLMFunctionCall { FunctionName = ProposeSkill }]);

        _scope.DidNotReceiveWithAnyArgs().MarkProposalHint(default!);
    }
}
