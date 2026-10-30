// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests the central Playground skill block: with the mode on, a skill of PlaygroundBlockedSkills (the memory, notes,
/// agent-skill, model, user and plugin skills ...) is refused with the feature-blocked error before the inner executor is
/// touched, a chain containing one is refused as a whole, and business demo skills still reach the inner executor. With
/// the mode off every call is forwarded. Also pins that every blocked name is a seeded skill (a typo or rename would
/// otherwise fail open) and that the real registrations use the decorators.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Services.Playground;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundBlockedSkillExecutorTests
{
    private const string BusinessSkill = "create_employee";
    private const string MemoryWriterSkill = "add_personal_memory";

    private ISkillExecutor _inner = null!;
    private IPlaygroundMode _mode = null!;
    private PlaygroundBlockedSkillExecutor _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _inner = Substitute.For<ISkillExecutor>();
        _inner.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { }, "ran"));
        _inner.ExecuteChainAsync(Arg.Any<IReadOnlyList<SkillInvocation>>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new List<SkillResult> { SkillResult.SuccessResult(new { }, "ran") });
        _mode = Substitute.For<IPlaygroundMode>();
        _sut = new PlaygroundBlockedSkillExecutor(_inner, _mode);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Roles.Admin },
    };

    private static SkillInvocation Invocation(string name) => new()
    {
        SkillName = name,
        Parameters = new Dictionary<string, object> { ["content"] = "KIWI-4711" },
    };

    [Test]
    public async Task ModeOn_BlockedSkill_IsRefusedWithoutReachingTheInnerExecutor()
    {
        _mode.IsActive.Returns(true);

        var result = await _sut.ExecuteAsync(Invocation(MemoryWriterSkill), Ctx());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(PlaygroundHardeningConstants.FeatureBlockedMessage);
        _inner.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task ModeOn_BlockedSkillInOtherCase_IsRefused()
    {
        _mode.IsActive.Returns(true);

        var result = await _sut.ExecuteAsync(Invocation("Add_Personal_Memory"), Ctx());

        result.Message.ShouldBe(PlaygroundHardeningConstants.FeatureBlockedMessage);
        _inner.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task ModeOn_BusinessSkill_IsForwarded()
    {
        _mode.IsActive.Returns(true);

        var result = await _sut.ExecuteAsync(Invocation(BusinessSkill), Ctx());

        result.Success.ShouldBeTrue();
        await _inner.Received(1).ExecuteAsync(
            Arg.Is<SkillInvocation>(i => i.SkillName == BusinessSkill), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ModeOff_BlockedSkill_IsForwarded()
    {
        _mode.IsActive.Returns(false);

        var result = await _sut.ExecuteAsync(Invocation(MemoryWriterSkill), Ctx());

        result.Success.ShouldBeTrue();
        await _inner.Received(1).ExecuteAsync(
            Arg.Is<SkillInvocation>(i => i.SkillName == MemoryWriterSkill), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ModeOn_ChainWithBlockedStep_IsRefusedAsAWholeWithoutReachingTheInnerExecutor()
    {
        _mode.IsActive.Returns(true);
        var chain = new List<SkillInvocation> { Invocation(BusinessSkill), Invocation(MemoryWriterSkill), Invocation(BusinessSkill) };

        var results = await _sut.ExecuteChainAsync(chain, Ctx());

        results.Count.ShouldBe(chain.Count);
        results.ShouldAllBe(r => !r.Success && r.Message == PlaygroundHardeningConstants.FeatureBlockedMessage);
        _inner.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task ModeOn_ChainWithoutBlockedStep_IsForwarded()
    {
        _mode.IsActive.Returns(true);
        var chain = new List<SkillInvocation> { Invocation(BusinessSkill) };

        var results = await _sut.ExecuteChainAsync(chain, Ctx());

        results.Count.ShouldBe(1);
        await _inner.Received(1).ExecuteChainAsync(chain, Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ModeOff_ChainWithBlockedStep_IsForwarded()
    {
        _mode.IsActive.Returns(false);
        var chain = new List<SkillInvocation> { Invocation(MemoryWriterSkill) };

        await _sut.ExecuteChainAsync(chain, Ctx());

        await _inner.Received(1).ExecuteChainAsync(chain, Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void EveryBlockedSkillName_IsASeededSkill()
    {
        var seeded = SkillGuardSources.LoadSkillEffects().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var pluginSeeds = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Klacks.UnitTest.TestHelpers.RepositoryRootLocator.RequireFile("Klacks.Api", "Plugins", "Features", "messaging", "skill-seeds.json")));
        seeded.UnionWith(pluginSeeds.RootElement.EnumerateArray().Select(skill => skill.GetProperty("name").GetString()!));

        var unknown = PlaygroundBlockedSkills.Names.Where(name => !seeded.Contains(name)).OrderBy(n => n).ToList();

        unknown.ShouldBeEmpty("blocked names that are not in skill-seeds.json (typo or renamed skill): " + string.Join(", ", unknown));
    }

    [TestCase("add_ai_memory")]
    [TestCase("add_personal_memory")]
    [TestCase("update_ai_memory")]
    [TestCase("delete_ai_memory")]
    [TestCase("get_ai_memories")]
    [TestCase("update_ai_soul")]
    [TestCase("update_ai_guidelines")]
    [TestCase("create_agent_skill")]
    [TestCase("create_llm_provider")]
    [TestCase("create_llm_model")]
    [TestCase("stash_pending_note")]
    [TestCase("schedule_recurring_task")]
    [TestCase("set_autonomy_level")]
    [TestCase("set_proactive_governance")]
    [TestCase("add_transcription_dictionary_entry")]
    [TestCase("update_my_account")]
    [TestCase("create_personal_access_token")]
    [TestCase("create_plan")]
    [TestCase("update_speech_settings")]
    public void NamedSkill_IsOnTheBlockList(string skillName) => PlaygroundBlockedSkills.IsBlocked(skillName).ShouldBeTrue();

    [TestCase("create_employee")]
    [TestCase("create_shift")]
    [TestCase("get_current_time")]
    [TestCase("navigate_to")]
    public void BusinessDemoSkill_IsNotOnTheBlockList(string skillName) => PlaygroundBlockedSkills.IsBlocked(skillName).ShouldBeFalse();

    [Test]
    public void RealRegistrations_UseTheDecorators()
    {
        var extensions = File.ReadAllText(RepositoryRootLocator.RequireFile(
            [RepositoryRootLocator.ApiProjectDirectoryName, "Infrastructure", "Extensions", "ServiceCollectionExtensions.cs"]));
        var llmCore = File.ReadAllText(RepositoryRootLocator.RequireFile(
            [RepositoryRootLocator.ApiProjectDirectoryName, "Infrastructure", "Extensions", "LLMCoreServiceCollectionExtensions.cs"]));

        extensions.ShouldContain("new Application.Services.Playground.PlaygroundBlockedSkillExecutor(");
        extensions.ShouldNotContain("AddScoped<ISkillExecutor, Domain.Services.Assistant.Skills.SkillExecutorService>");
        llmCore.ShouldContain("new Klacks.Api.Application.Services.Playground.PlaygroundSilentAssistantLastActionStore(");
        llmCore.ShouldNotContain("IAssistantLastActionStore, Klacks.Api.Infrastructure.Services.Assistant.PersistentAssistantLastActionStore>");
    }
}
