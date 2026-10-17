// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The skill executor is the last line of the capped channels (Slack owner bridge, MCP): a skill of
/// CappedChannelSkillExclusions is refused with an honest "not available in this channel" before any gate or handler runs,
/// whatever rights the caller carries, while the same call in the chat and every other skill on the capped channel run.
/// Exercised with the real executor; only registry, dispatcher and gates are stubbed.
/// </summary>

using Klacks.Api.Application.Services.Groups;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SkillExecutorCappedChannelTests
{
    private const string GenericHandlerType = "test_handler";
    private const string GenericHandlerConfig = "{\"endpoint\":\"test\"}";
    private const string OtherSkill = "list_emails";

    private IGenericSkillDispatcher _genericDispatcher = null!;
    private SkillExecutorService _sut = null!;

    private static IEnumerable<string> ExcludedSkills => CappedChannelSkillExclusions.All;

    [SetUp]
    public void SetUp()
    {
        var registry = Substitute.For<ISkillRegistry>();
        _genericDispatcher = Substitute.For<IGenericSkillDispatcher>();
        var autonomyGate = Substitute.For<IAutonomyGate>();
        autonomyGate.CheckAsync(
                Arg.Any<SkillDescriptor>(), Arg.Any<SkillExecutionContext>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns((SkillResult?)null);
        _genericDispatcher.CanHandle(GenericHandlerType).Returns(true);
        _genericDispatcher.ExecuteAsync(
                GenericHandlerType, GenericHandlerConfig,
                Arg.Any<Dictionary<string, object>>(), Arg.Any<ExplainDepthProfile?>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "ran"));
        registry.GetSkillByName(Arg.Any<string>()).Returns(call =>
            new SkillDescriptor(call.Arg<string>(), "test", SkillCategory.Query, [], [], [], null)
            {
                HandlerType = GenericHandlerType,
                HandlerConfig = GenericHandlerConfig
            });

        var scopeGuard = Substitute.For<ISkillScenarioScopeGuard>();
        scopeGuard.CheckAsync(Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns((SkillResult?)null);

        _sut = new SkillExecutorService(
            registry,
            Substitute.For<ISkillUsageTracker>(),
            Substitute.For<IServiceProvider>(),
            _genericDispatcher,
            autonomyGate,
            Substitute.For<IEntityChangeNotifier>(),
            Substitute.For<IRecentEntityRegistrar>(),
            scopeGuard,
            Substitute.For<ILogger<SkillExecutorService>>());
    }

    private static SkillExecutionContext Ctx(string? cappedChannel = null, PersonalAccessTokenAccessMode? mode = null) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "caller",
        UserPermissions = new List<string> { Roles.Authorised, Permissions.CanEditClients },
        BypassAutonomyGate = true,
        CappedChannel = cappedChannel,
        ExternalAgentAccessMode = mode
    };

    private Task<SkillResult> ExecuteAsync(string skill, SkillExecutionContext context) =>
        _sut.ExecuteAsync(new SkillInvocation { SkillName = skill, Parameters = new Dictionary<string, object>() }, context);

    [TestCaseSource(nameof(ExcludedSkills))]
    public async Task OnTheSlackBridge_AnExcludedSkill_IsRefusedHonestly_AndNeverRuns(string skill)
    {
        var result = await ExecuteAsync(skill, Ctx(CappedChannels.SlackOwnerBridge));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(CappedChannelSkillExclusions.DescribeNotAvailable(skill));
        await _genericDispatcher.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!, default!, default, default);
    }

    [TestCaseSource(nameof(ExcludedSkills))]
    public async Task OverMcp_AnExcludedSkill_IsRefused_EvenForARunThatBypassedTheMcpCatalog(string skill)
    {
        var result = await ExecuteAsync(skill, Ctx(mode: PersonalAccessTokenAccessMode.Write));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(CappedChannelSkillExclusions.DescribeNotAvailable(skill));
        await _genericDispatcher.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!, default!, default, default);
    }

    [Test]
    public async Task AnAdminOnACappedChannel_IsRefusedToo()
    {
        var context = Ctx(CappedChannels.SlackOwnerBridge) with { UserPermissions = new List<string> { Roles.Admin } };

        var result = await ExecuteAsync("read_messages", context);

        result.Success.ShouldBeFalse();
    }

    [TestCaseSource(nameof(ExcludedSkills))]
    public async Task InTheChat_TheSameSkill_Runs(string skill)
    {
        var result = await ExecuteAsync(skill, Ctx());

        result.Success.ShouldBeTrue();
    }

    [Test]
    public async Task OnTheSlackBridge_ASkillOutsideTheList_Runs()
    {
        var result = await ExecuteAsync(OtherSkill, Ctx(CappedChannels.SlackOwnerBridge));

        result.Success.ShouldBeTrue();
    }
}
