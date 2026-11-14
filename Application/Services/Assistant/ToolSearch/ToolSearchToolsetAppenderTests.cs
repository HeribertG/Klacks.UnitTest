// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// find_tools joins an assembled toolset only when the tool search is switched on and the skill is enabled for the
/// agent, carries the ToolSearch source, and is never added twice.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Services.Assistant.ToolSearch;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Application.Services.Assistant.ToolSearch;

[TestFixture]
public class ToolSearchToolsetAppenderTests
{
    private const string AssembledSkill = "list_clients";

    private ISkillCacheService _skillCache = null!;
    private Agent _agent = null!;

    [SetUp]
    public void SetUp()
    {
        _agent = new Agent { Id = Guid.NewGuid(), Name = "Klacksy" };
        _skillCache = Substitute.For<ISkillCacheService>();
        GivenEnabledSkills(AssembledSkill, ToolSearchConstants.SkillName);
    }

    private void GivenEnabledSkills(params string[] names) =>
        _skillCache.GetEnabledSkillsAsync(_agent.Id, Arg.Any<CancellationToken>())
            .Returns(names
                .Select(n => new AgentSkill
                {
                    Name = n,
                    Description = $"{n} description",
                    ParametersJson = "[{\"name\":\"needs\",\"type\":\"string\",\"description\":\"What is missing\",\"required\":true}]"
                })
                .ToList());

    private static SkillToolsetResult Assembled(params string[] names) =>
        new() { Functions = names.Select(n => new LLMFunction { Name = n }).ToList() };

    private Task Append(bool enabled, SkillToolsetResult toolset) =>
        new ToolSearchToolsetAppender(_skillCache, new ToolSearchOptions(enabled)).AppendAsync(_agent, toolset);

    private static List<string> Names(SkillToolsetResult toolset) => toolset.Functions.Select(f => f.Name).ToList();

    [Test]
    public async Task SwitchedOff_LeavesTheToolsetUnchanged()
    {
        var toolset = Assembled(AssembledSkill);

        await Append(enabled: false, toolset);

        Names(toolset).ShouldBe(new[] { AssembledSkill });
        await _skillCache.DidNotReceiveWithAnyArgs().GetEnabledSkillsAsync(default, default);
    }

    [Test]
    public async Task SwitchedOn_AddsFindToolsOnceWithTheToolSearchSource()
    {
        var toolset = Assembled(AssembledSkill);

        await Append(enabled: true, toolset);

        Names(toolset).ShouldBe(new[] { AssembledSkill, ToolSearchConstants.SkillName });
        var findTools = toolset.Functions.Single(f => f.Name == ToolSearchConstants.SkillName);
        findTools.ToolsetSource.ShouldBe(ToolsetSkillSource.ToolSearch);
        findTools.Description.ShouldBe($"{ToolSearchConstants.SkillName} description");
        findTools.RequiredParameters.ShouldBe(new[] { ToolSearchConstants.NeedsParameter });
    }

    [Test]
    public async Task SwitchedOn_ButTheSkillIsNotEnabledForTheAgent_LeavesTheToolsetUnchanged()
    {
        GivenEnabledSkills(AssembledSkill);
        var toolset = Assembled(AssembledSkill);

        await Append(enabled: true, toolset);

        Names(toolset).ShouldBe(new[] { AssembledSkill });
    }

    [Test]
    public async Task SwitchedOn_AndFindToolsAlreadyInTheToolset_IsNotAddedAgain()
    {
        var toolset = Assembled(AssembledSkill, ToolSearchConstants.SkillName.ToUpperInvariant());

        await Append(enabled: true, toolset);

        Names(toolset).ShouldBe(new[] { AssembledSkill, ToolSearchConstants.SkillName.ToUpperInvariant() });
    }

    [Test]
    public async Task AppendingTwice_AddsFindToolsOnlyOnce()
    {
        var toolset = Assembled(AssembledSkill);

        await Append(enabled: true, toolset);
        await Append(enabled: true, toolset);

        Names(toolset).Count(n => n == ToolSearchConstants.SkillName).ShouldBe(1);
    }
}
