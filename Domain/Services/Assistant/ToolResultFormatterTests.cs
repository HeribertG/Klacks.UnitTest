// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the shared tool-result formatter beyond the layout pinned by
/// LLMServiceFormatFunctionResultsTests: a result tainted as external content is framed untrusted even
/// under a skill name that UntrustedSkillOutputs does not list (the relay case of confirm_pending_action
/// and run_analysis), the name list still applies without a taint, and the taint travels from the skill
/// executor through the skill bridge and the function executor onto the call the chat loop formats.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.Domain.Services.Assistant.Skills.Adapters;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class ToolResultFormatterTests
{
    private const string WrapperSkill = "run_analysis";
    private const string UntrustedSkill = "web_search";
    private const string TrustedSkill = "list_groups";
    private const int Cap = 8_000;

    [Test]
    public void TaintedEntry_UnderAnUnlistedName_CarriesTheFlagAndTheNotice()
    {
        var formatted = ToolResultFormatter.Format(
            [new ToolResultEntry(WrapperSkill, "summary quoting an e-mail", ContainsExternalContent: true)], Cap);

        formatted.ShouldContain(ToolResultMarkers.ResultUntrustedFlag);
        formatted.ShouldContain(ToolResultMarkers.UntrustedContentNotice);
    }

    [Test]
    public void TaintedFunctionCall_ThroughTheChatLoopFormatter_IsFlagged()
    {
        var formatted = LLMService.FormatFunctionResults(
        [
            new LLMFunctionCall { FunctionName = WrapperSkill, Result = "relayed", ContainsExternalContent = true }
        ]);

        formatted.ShouldContain(ToolResultMarkers.ResultUntrustedFlag);
        formatted.ShouldContain(ToolResultMarkers.UntrustedContentNotice);
    }

    [Test]
    public void UntaintedEntry_UnderAListedName_IsStillFlagged()
    {
        var formatted = ToolResultFormatter.Format([new ToolResultEntry(UntrustedSkill, "snippet")], Cap);

        formatted.ShouldContain(ToolResultMarkers.ResultUntrustedFlag);
    }

    [Test]
    public void UntaintedEntry_UnderAnUnlistedName_IsNotFlagged()
    {
        var formatted = ToolResultFormatter.Format([new ToolResultEntry(TrustedSkill, "internal")], Cap);

        formatted.ShouldNotContain(ToolResultMarkers.ResultUntrustedFlag);
        formatted.ShouldNotContain(ToolResultMarkers.UntrustedContentNotice);
    }

    [Test]
    public void EntryAndFunctionCallOverloads_RenderIdentically()
    {
        var viaEntry = ToolResultFormatter.Format(
            [new ToolResultEntry(WrapperSkill, "x", ContainsExternalContent: true)], Cap);
        var viaCall = ToolResultFormatter.Format(
            [new LLMFunctionCall { FunctionName = WrapperSkill, Result = "x", ContainsExternalContent = true }], Cap);

        viaCall.ShouldBe(viaEntry);
    }

    [Test]
    public async Task TaintFromTheSkillExecutor_ReachesTheFormattedChatLoopMessage()
    {
        var skillExecutor = Substitute.For<ISkillExecutor>();
        skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "relayed mail text") with { ContainsExternalContent = true });
        var bridge = new LLMSkillBridge(
            Substitute.For<ISkillRegistry>(),
            skillExecutor,
            Substitute.For<ISkillAdapterFactory>(),
            Substitute.For<ILogger<LLMSkillBridge>>());
        var agentRepository = Substitute.For<IAgentRepository>();
        agentRepository.GetDefaultAgentAsync().Returns((Agent?)null);
        var functionExecutor = new LLMFunctionExecutor(
            Substitute.For<ILogger<LLMFunctionExecutor>>(),
            Substitute.For<IAgentSkillRepository>(),
            agentRepository,
            Substitute.For<IPendingConfirmationStore>(),
            bridge);
        var call = new LLMFunctionCall { FunctionName = WrapperSkill };

        await functionExecutor.ProcessFunctionCallsAsync(
            new LLMContext { Message = "analyze", UserId = Guid.NewGuid().ToString() }, [call]);

        call.ContainsExternalContent.ShouldBeTrue();
        LLMService.FormatFunctionResults([call]).ShouldContain(ToolResultMarkers.ResultUntrustedFlag);
    }
}
