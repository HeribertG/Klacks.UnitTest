// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization of how LLMFunctionExecutor turns one skill-bridge result into the text the model reads and the
/// flags on the call: the exact wording of the missing-bridge, error, confirmation, message-only and data answers, the
/// voice-mode override on a detail-coverage answer, and the bridge fields copied onto the call (external content,
/// replayed skill, issued token, UI action tracking id).
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMFunctionExecutorCharacterizationTests
{
    private const string SkillName = "characterized_skill";
    private const string DetailMessage = "Here is the page. Please cover EVERY element of it.";

    private ILLMSkillBridge _skillBridge = null!;
    private IAgentSkillRepository _agentSkillRepository = null!;
    private IAgentRepository _agentRepository = null!;
    private ITurnConfirmationScope _turnScope = null!;
    private LLMFunctionExecutor _executor = null!;

    [SetUp]
    public void SetUp()
    {
        _skillBridge = Substitute.For<ILLMSkillBridge>();
        _agentSkillRepository = Substitute.For<IAgentSkillRepository>();
        _agentRepository = Substitute.For<IAgentRepository>();
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns((Agent?)null);
        _turnScope = Substitute.For<ITurnConfirmationScope>();

        _executor = CreateExecutor(_skillBridge);
    }

    private LLMFunctionExecutor CreateExecutor(ILLMSkillBridge? skillBridge) => new(
        Substitute.For<ILogger<LLMFunctionExecutor>>(),
        _agentSkillRepository,
        _agentRepository,
        Substitute.For<IPendingConfirmationStore>(),
        _turnScope,
        Substitute.For<ICancellableSkillPolicy>(),
        skillBridge);

    private static LLMContext Ctx(bool isVoiceMode = false) => new()
    {
        Message = "Bitte ausfuehren.",
        UserId = Guid.NewGuid().ToString(),
        UserRights = new List<string>(),
        IsVoiceMode = isVoiceMode
    };

    private void GivenBridgeReturns(SkillBridgeResult result)
    {
        _skillBridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(result);
    }

    private async Task<(string Text, LLMFunctionCall Call)> RunAsync(LLMContext? context = null)
    {
        var call = new LLMFunctionCall { FunctionName = SkillName };
        var text = await _executor.ProcessFunctionCallsAsync(context ?? Ctx(), new List<LLMFunctionCall> { call });
        return (text, call);
    }

    [Test]
    public async Task WithoutASkillBridge_TheCallIsNotAvailable()
    {
        _executor = CreateExecutor(null);

        var (text, call) = await RunAsync();

        text.ShouldBe($"Function '{SkillName}' is not available.");
        call.Result.ShouldBe(text);
        call.ResultKind.ShouldBe(LLMFunctionResultKind.Error);
    }

    [Test]
    public async Task AFailedResult_IsReportedAsAnErrorWithTheBridgeMessage()
    {
        GivenBridgeReturns(new SkillBridgeResult { Success = false, Message = "boom" });

        var (text, call) = await RunAsync();

        text.ShouldBe("Error: boom");
        call.Success.ShouldBeFalse();
        call.ResultKind.ShouldBe(LLMFunctionResultKind.Error);
        call.RequiresConfirmation.ShouldBeFalse();
        call.IsNavigation.ShouldBeFalse();
        call.DataJson.ShouldBeEmpty();
    }

    [Test]
    public async Task AConfirmationResult_AsksForConfirmation_AndCarriesItsToken()
    {
        GivenBridgeReturns(new SkillBridgeResult
        {
            Success = false,
            Message = "really delete?",
            ResultType = nameof(SkillResultType.Confirmation),
            ConfirmationToken = "token-1"
        });

        var (text, call) = await RunAsync();

        text.ShouldBe("Confirmation required: really delete?");
        call.ResultKind.ShouldBe(LLMFunctionResultKind.Confirmation);
        call.RequiresConfirmation.ShouldBeTrue();
        call.IssuedConfirmationToken.ShouldBe("token-1");
        _turnScope.Received(1).MarkIssued("token-1");
    }

    [Test]
    public async Task ATokenOnANonConfirmationResult_IsMarkedOnTheScope_ButNotIssuedOnTheCall()
    {
        GivenBridgeReturns(new SkillBridgeResult { Success = true, Message = "replayed", ConfirmationToken = "token-2" });

        var (_, call) = await RunAsync();

        call.IssuedConfirmationToken.ShouldBeNull();
        call.RequiresConfirmation.ShouldBeFalse();
        _turnScope.Received(1).MarkIssued("token-2");
    }

    [Test]
    public async Task AMessageOnlyResult_ReturnsTheMessageVerbatim()
    {
        GivenBridgeReturns(new SkillBridgeResult { Success = true, Message = "done" });

        var (text, call) = await RunAsync();

        text.ShouldBe("done");
        call.Success.ShouldBeTrue();
        call.ResultKind.ShouldBe(LLMFunctionResultKind.MessageOnly);
        call.DataJson.ShouldBeEmpty();
        _turnScope.DidNotReceiveWithAnyArgs().MarkIssued(default!);
    }

    [Test]
    public async Task ADataResult_AppendsTheCompactDataJsonOnItsOwnLine()
    {
        GivenBridgeReturns(new SkillBridgeResult { Success = true, Message = "found", Data = new { Name = "Anna", Count = 2 } });

        var (text, call) = await RunAsync();

        text.ShouldBe("found\nData: {\"Name\":\"Anna\",\"Count\":2}");
        call.ResultKind.ShouldBe(LLMFunctionResultKind.Data);
        call.DataJson.Count.ShouldBe(1);
        call.DataJson[0].ShouldBe("{\"Name\":\"Anna\",\"Count\":2}");
        call.IsNavigation.ShouldBeFalse();
        _executor.NavigationRoute.ShouldBeNull();
        _executor.NavigationTarget.ShouldBeNull();
    }

    [Test]
    public async Task OnAVoiceTurn_ADetailCoverageMessage_GetsTheVoiceOverrideAppended()
    {
        GivenBridgeReturns(new SkillBridgeResult { Success = true, Message = DetailMessage });

        var (text, _) = await RunAsync(Ctx(isVoiceMode: true));

        text.ShouldBe(DetailMessage + VoiceModeInstructionConstants.VoiceAnswerOverride);
    }

    [Test]
    public async Task OnAVoiceTurn_ADetailCoverageDataResult_GetsTheVoiceOverrideAfterTheDataLine()
    {
        GivenBridgeReturns(new SkillBridgeResult { Success = true, Message = DetailMessage, Data = new { Level = "elements" } });

        var (text, _) = await RunAsync(Ctx(isVoiceMode: true));

        text.ShouldBe(DetailMessage + "\nData: {\"Level\":\"elements\"}" + VoiceModeInstructionConstants.VoiceAnswerOverride);
    }

    [Test]
    public async Task OutsideAVoiceTurn_ADetailCoverageMessage_IsLeftAlone()
    {
        GivenBridgeReturns(new SkillBridgeResult { Success = true, Message = DetailMessage });

        var (text, _) = await RunAsync();

        text.ShouldBe(DetailMessage);
    }

    [Test]
    public async Task TheBridgeFlags_AreCopiedOntoTheCall()
    {
        var trackingId = Guid.NewGuid();
        GivenBridgeReturns(new SkillBridgeResult
        {
            Success = true,
            Message = "done",
            ContainsExternalContent = true,
            ReplayedSkillName = "delete_client",
            UiActionSteps = "{\"steps\":[]}",
            UiActionTrackingId = trackingId
        });

        var (_, call) = await RunAsync();

        call.ContainsExternalContent.ShouldBeTrue();
        call.ReplayedSkillName.ShouldBe("delete_client");
        call.UiActionSteps.ShouldBe("{\"steps\":[]}");
        call.UiActionTrackingId.ShouldBe(trackingId);
        call.Parameters.ShouldBeEmpty();
    }
}
