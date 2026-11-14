// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// After each tool round LLMFunctionExecutor takes the skills find_tools offered for the turn and appends them to the
/// turn's toolset: a name already in the toolset is not added twice, and a skill the capped channel withholds is skipped.
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
public class LLMFunctionExecutorToolSearchTests
{
    private const string AlreadyOfferedSkill = "list_clients";
    private const string FoundSkill = "assign_contract";
    private const string OtherFoundSkill = "add_clients_to_group";
    private const string ChannelBlockedSkill = "send_message";

    private ILLMSkillBridge _skillBridge = null!;
    private ITurnToolsetExtension _toolsetExtension = null!;
    private IAgentRepository _agentRepository = null!;

    [SetUp]
    public void SetUp()
    {
        _skillBridge = Substitute.For<ILLMSkillBridge>();
        _skillBridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<LLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new SkillBridgeResult { Success = true, Message = "done" });
        _agentRepository = Substitute.For<IAgentRepository>();
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns((Agent?)null);
        _toolsetExtension = Substitute.For<ITurnToolsetExtension>();
        _toolsetExtension.TakeOffered(Arg.Any<Guid?>()).Returns(Array.Empty<LLMFunction>());
    }

    private LLMFunctionExecutor CreateExecutor(ITurnToolsetExtension toolsetExtension) => new(
        Substitute.For<ILogger<LLMFunctionExecutor>>(),
        Substitute.For<IAgentSkillRepository>(),
        _agentRepository,
        PendingStoreTestFactory.CreateConfirmationStore(),
        Substitute.For<ITurnConfirmationScope>(),
        Substitute.For<ICancellableSkillPolicy>(),
        toolsetExtension,
        _skillBridge);

    private static LLMContext Ctx(Guid turnId, string? cappedChannel = null) => new()
    {
        Message = "Weise Anna den Vertrag zu",
        UserId = Guid.NewGuid().ToString(),
        UserRights = new List<string>(),
        TurnId = turnId,
        CappedChannel = cappedChannel,
        AvailableFunctions = [new LLMFunction { Name = AlreadyOfferedSkill }, new LLMFunction { Name = ToolSearchConstants.SkillName }]
    };

    private static List<LLMFunctionCall> FindToolsCall() =>
        [new LLMFunctionCall { FunctionName = ToolSearchConstants.SkillName, Parameters = new() { [ToolSearchConstants.NeedsParameter] = "assign a contract" } }];

    private void GivenOffered(Guid turnId, params string[] names) =>
        _toolsetExtension.TakeOffered(turnId)
            .Returns(names.Select(n => new LLMFunction { Name = n, ToolsetSource = ToolsetSkillSource.ToolSearch }).ToList());

    private static List<string> Names(LLMContext context) => context.AvailableFunctions.Select(f => f.Name).ToList();

    [Test]
    public async Task AfterABatch_TheOfferedFunctionsOfTheTurnAreAppended()
    {
        var turnId = Guid.NewGuid();
        GivenOffered(turnId, FoundSkill, OtherFoundSkill);
        var context = Ctx(turnId);

        await CreateExecutor(_toolsetExtension).ProcessFunctionCallsAsync(context, FindToolsCall());

        Names(context).ShouldBe(new[] { AlreadyOfferedSkill, ToolSearchConstants.SkillName, FoundSkill, OtherFoundSkill });
        context.AvailableFunctions.Single(f => f.Name == FoundSkill).ToolsetSource.ShouldBe(ToolsetSkillSource.ToolSearch);
        _toolsetExtension.Received(1).TakeOffered(turnId);
    }

    [Test]
    public async Task AFunctionAlreadyInTheToolset_IsNotAppendedTwice()
    {
        var turnId = Guid.NewGuid();
        GivenOffered(turnId, AlreadyOfferedSkill.ToUpperInvariant(), FoundSkill, FoundSkill);
        var context = Ctx(turnId);

        await CreateExecutor(_toolsetExtension).ProcessFunctionCallsAsync(context, FindToolsCall());

        Names(context).ShouldBe(new[] { AlreadyOfferedSkill, ToolSearchConstants.SkillName, FoundSkill });
    }

    [Test]
    public async Task OnACappedChannel_AFunctionTheChannelWithholds_IsSkipped()
    {
        CappedChannelSkillExclusions.Contains(ChannelBlockedSkill).ShouldBeTrue();
        var turnId = Guid.NewGuid();
        GivenOffered(turnId, ChannelBlockedSkill, FoundSkill);
        var context = Ctx(turnId, CappedChannels.SlackOwnerBridge);

        await CreateExecutor(_toolsetExtension).ProcessFunctionCallsAsync(context, FindToolsCall());

        Names(context).ShouldBe(new[] { AlreadyOfferedSkill, ToolSearchConstants.SkillName, FoundSkill });
    }

    [Test]
    public async Task WithoutACappedChannel_TheSameFunctionIsAppended()
    {
        var turnId = Guid.NewGuid();
        GivenOffered(turnId, ChannelBlockedSkill);
        var context = Ctx(turnId);

        await CreateExecutor(_toolsetExtension).ProcessFunctionCallsAsync(context, FindToolsCall());

        Names(context).ShouldContain(ChannelBlockedSkill);
    }
}
