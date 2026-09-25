// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Verifies the write side of the proposal/apply pairing in LLMFunctionExecutor: a successful call to a
/// skill that declares a paired apply skill in the catalogue leaves a proposal hint naming that skill,
/// a failed call and a skill without a declaration leave nothing, and executing the paired apply skill
/// drops the hint again. The pairing is read from the skill catalogue only — no user text is inspected.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using LLMFunctionCall = Klacks.Api.Domain.Services.Assistant.Providers.LLMFunctionCall;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class LLMFunctionExecutorProposalPairingTests
{
    private const string ProposeSkillName = "propose_customer_grouping";
    private const string ApplySkillName = "apply_customer_grouping";
    private const string UnpairedSkillName = "list_groups";
    private const string SelfPairedSkillName = "partition_clients_like_skill";
    private const string ApplyParameterName = "apply";

    private static readonly TimeSpan ForceWindow =
        TimeSpan.FromSeconds(AutonomyDefaults.ConfirmationForceWindowSeconds);

    private ILLMSkillBridge _skillBridge = null!;
    private IAgentSkillRepository _agentSkillRepository = null!;
    private IAgentRepository _agentRepository = null!;
    private IPendingConfirmationStore _confirmationStore = null!;
    private LLMFunctionExecutor _executor = null!;
    private Guid _userId;

    [SetUp]
    public void SetUp()
    {
        _userId = Guid.NewGuid();
        _skillBridge = Substitute.For<ILLMSkillBridge>();
        _agentSkillRepository = Substitute.For<IAgentSkillRepository>();
        _agentRepository = Substitute.For<IAgentRepository>();
        _confirmationStore = PendingStoreTestFactory.CreateConfirmationStore();

        var agent = new Agent { Id = Guid.NewGuid() };
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(agent);
        _agentSkillRepository.GetEnabledAsync(agent.Id, Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>
            {
                new() { Name = ProposeSkillName, PairedApplySkill = ApplySkillName },
                new() { Name = ApplySkillName },
                new() { Name = UnpairedSkillName },
                new() { Name = SelfPairedSkillName, PairedApplySkill = SelfPairedSkillName }
            });

        _executor = new LLMFunctionExecutor(
            Substitute.For<ILogger<LLMFunctionExecutor>>(),
            _agentSkillRepository,
            _agentRepository,
            _confirmationStore,
            Substitute.For<ITurnConfirmationScope>(),
            Substitute.For<ICancellableSkillPolicy>(),
            _skillBridge);
    }

    [Test]
    public async Task SuccessfulProposeCall_RecordsAHintForThePairedApplySkill()
    {
        SetupBridgeResult(success: true);

        await _executor.ProcessFunctionCallsAsync(Context(), [Call(ProposeSkillName)]);

        var hint = _confirmationStore.PeekLatestForUser(
            _userId, ForceWindow, PendingConfirmationPurposes.ProposalHint);

        hint.ShouldNotBeNull();
        hint!.SkillName.ShouldBe(ApplySkillName);
    }

    [Test]
    public async Task FailedProposeCall_RecordsNoHint()
    {
        SetupBridgeResult(success: false);

        await _executor.ProcessFunctionCallsAsync(Context(), [Call(ProposeSkillName)]);

        _confirmationStore.PeekLatestForUser(
            _userId, ForceWindow, PendingConfirmationPurposes.ProposalHint).ShouldBeNull();
    }

    [Test]
    public async Task SkillWithoutAPairingDeclaration_RecordsNoHint()
    {
        SetupBridgeResult(success: true);

        await _executor.ProcessFunctionCallsAsync(Context(), [Call(UnpairedSkillName)]);

        _confirmationStore.PeekLatestForUser(
            _userId, ForceWindow, PendingConfirmationPurposes.ProposalHint).ShouldBeNull();
    }

    [Test]
    public async Task ExecutingThePairedApplySkill_DropsTheHint()
    {
        SetupBridgeResult(success: true);
        _confirmationStore.CreateProposalHint(_userId, ApplySkillName);

        await _executor.ProcessFunctionCallsAsync(Context(), [Call(ApplySkillName)]);

        _confirmationStore.PeekLatestForUser(
            _userId, ForceWindow, PendingConfirmationPurposes.ProposalHint).ShouldBeNull();
    }

    [Test]
    public async Task ExecutingAnUnrelatedSkill_KeepsTheHint()
    {
        SetupBridgeResult(success: true);
        _confirmationStore.CreateProposalHint(_userId, ApplySkillName);

        await _executor.ProcessFunctionCallsAsync(Context(), [Call(UnpairedSkillName)]);

        _confirmationStore.PeekLatestForUser(
            _userId, ForceWindow, PendingConfirmationPurposes.ProposalHint).ShouldNotBeNull();
    }

    [Test]
    public async Task ProposeCall_LeavesTheGateReplayPathUntouched()
    {
        SetupBridgeResult(success: true);

        await _executor.ProcessFunctionCallsAsync(Context(), [Call(ProposeSkillName)]);

        _confirmationStore.PeekLatestForUser(_userId, ForceWindow).ShouldBeNull();
    }

    [Test]
    public async Task SelfPairedSkill_PreviewCallWithApplyFalse_CreatesHintForItself()
    {
        SetupBridgeResult(success: true);

        await _executor.ProcessFunctionCallsAsync(
            Context(), [Call(SelfPairedSkillName, new Dictionary<string, object> { [ApplyParameterName] = false })]);

        var hint = _confirmationStore.PeekLatestForUser(
            _userId, ForceWindow, PendingConfirmationPurposes.ProposalHint);

        hint.ShouldNotBeNull();
        hint!.SkillName.ShouldBe(SelfPairedSkillName);
    }

    [Test]
    public async Task SelfPairedSkill_PreviewCallWithoutApplyParameter_CreatesHintForItself()
    {
        SetupBridgeResult(success: true);

        await _executor.ProcessFunctionCallsAsync(Context(), [Call(SelfPairedSkillName)]);

        var hint = _confirmationStore.PeekLatestForUser(
            _userId, ForceWindow, PendingConfirmationPurposes.ProposalHint);

        hint.ShouldNotBeNull();
        hint!.SkillName.ShouldBe(SelfPairedSkillName);
    }

    [Test]
    public async Task SelfPairedSkill_ApplyCall_DiscardsHint()
    {
        SetupBridgeResult(success: true);
        _confirmationStore.CreateProposalHint(_userId, SelfPairedSkillName);

        await _executor.ProcessFunctionCallsAsync(
            Context(), [Call(SelfPairedSkillName, new Dictionary<string, object> { [ApplyParameterName] = true })]);

        _confirmationStore.PeekLatestForUser(
            _userId, ForceWindow, PendingConfirmationPurposes.ProposalHint).ShouldBeNull();
    }

    [Test]
    public async Task SelfPairedSkill_ApplyCallWithJsonElementTrue_DiscardsHint()
    {
        SetupBridgeResult(success: true);
        _confirmationStore.CreateProposalHint(_userId, SelfPairedSkillName);
        using var applyDocument = JsonDocument.Parse("true");
        var applyElement = applyDocument.RootElement.Clone();

        await _executor.ProcessFunctionCallsAsync(
            Context(), [Call(SelfPairedSkillName, new Dictionary<string, object> { [ApplyParameterName] = applyElement })]);

        _confirmationStore.PeekLatestForUser(
            _userId, ForceWindow, PendingConfirmationPurposes.ProposalHint).ShouldBeNull();
    }

    private void SetupBridgeResult(bool success)
    {
        _skillBridge.ExecuteSkillFromLLMCallAsync(Arg.Any<LLMFunctionCall>(), Arg.Any<SkillExecutionContext>())
            .Returns(new SkillBridgeResult
            {
                Success = success,
                Message = success ? "Preview ready." : "Failed."
            });
    }

    private LLMContext Context() => new()
    {
        Message = "Gruppiere die Kunden nach Ortschaft",
        UserId = _userId.ToString(),
        UserRights = new List<string>()
    };

    private static LLMFunctionCall Call(string functionName) => new() { FunctionName = functionName };

    private static LLMFunctionCall Call(string functionName, Dictionary<string, object> parameters) =>
        new() { FunctionName = functionName, Parameters = parameters };
}
