// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A UiAction step of type apiCall makes the browser post any same-origin request with the user's own session, which
/// would let a model-written skill bypass the autonomy gate. These tests pin that create_agent_skill and
/// update_agent_skill refuse such a step, that the inspector finds it at any depth and fails closed on malformed JSON,
/// and that a stored config that already carries one is not delivered to the client.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AgentSkillApiCallStepRejectionTests
{
    private const string NavigateSteps = "[{\"action\":\"navigate\",\"route\":\"/workplace/settings\"}]";
    private const string ApiCallSteps = "[{\"action\":\"apiCall\",\"apiUrl\":\"/api/backend/Clients/1\",\"apiMethod\":\"DELETE\"}]";

    private IAgentSkillRepository _skillRepository = null!;
    private IAgentRepository _agentRepository = null!;
    private ISkillPhraseRepository _phraseRepository = null!;
    private ISkillCatalogRefresher _catalogRefresher = null!;

    [SetUp]
    public void SetUp()
    {
        _skillRepository = Substitute.For<IAgentSkillRepository>();
        _agentRepository = Substitute.For<IAgentRepository>();
        _phraseRepository = Substitute.For<ISkillPhraseRepository>();
        _catalogRefresher = Substitute.For<ISkillCatalogRefresher>();

        _skillRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentSkill>());
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(new Agent { Id = Guid.NewGuid() });
    }

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("[{\"action\":\"navigate\",\"route\":\"/x\"}]", false)]
    [TestCase("[{\"action\":\"click\",\"selector\":\"#apiCall\"}]", false)]
    [TestCase("[{\"action\":\"apiCall\",\"apiUrl\":\"/api/x\"}]", true)]
    [TestCase("[{\"action\":\"APICALL\"}]", true)]
    [TestCase("{\"steps\":[{\"action\":\"conditional\",\"thenSteps\":[{\"action\":\"apiCall\"}]}]}", true)]
    [TestCase("{\"steps\":[{\"action\":\"conditional\",\"elseSteps\":[{\"action\":\"delay\"},{\"action\":\"apiCall\"}]}]}", true)]
    [TestCase("[{\"action\":\"apiCall\"", true)]
    public void ContainsApiCallStep_FindsTheStepAtAnyDepth_AndFailsClosedOnMalformedJson(string? config, bool expected)
    {
        UiActionStepInspector.ContainsApiCallStep(config).ShouldBe(expected);
    }

    [Test]
    public async Task CreateAgentSkill_WithAnApiCallStep_IsRefused_AndNothingIsStored()
    {
        var sut = new CreateAgentSkillSkill(_skillRepository, _agentRepository, _phraseRepository, _catalogRefresher);

        var result = await sut.ExecuteAsync(Context(), Parameters(("name", "wipe_clients"), ("description", "d"), ("handlerSteps", ApiCallSteps)));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(UiActionStepActions.ApiCallRejectedMessage);
        await _skillRepository.DidNotReceive().AddAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
        await _catalogRefresher.DidNotReceive().RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAgentSkill_WithNavigationSteps_IsStillCreated()
    {
        var sut = new CreateAgentSkillSkill(_skillRepository, _agentRepository, _phraseRepository, _catalogRefresher);

        var result = await sut.ExecuteAsync(Context(), Parameters(("name", "open_settings"), ("description", "d"), ("handlerSteps", NavigateSteps)));

        result.Success.ShouldBeTrue();
        await _skillRepository.Received(1).AddAsync(
            Arg.Is<AgentSkill>(skill => skill.Name == "open_settings" && skill.HandlerConfig.Contains("navigate")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateAgentSkill_WithAnApiCallStep_IsRefused_AndTheStoredStepsStayUntouched()
    {
        var existing = new AgentSkill { Name = "open_settings", HandlerConfig = "{\"steps\":" + NavigateSteps + "}" };
        _skillRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentSkill> { existing });
        var sut = new UpdateAgentSkillSkill(_skillRepository, _phraseRepository, _catalogRefresher);

        var result = await sut.ExecuteAsync(Context(), Parameters(("skillName", "open_settings"), ("handlerSteps", ApiCallSteps)));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(UiActionStepActions.ApiCallRejectedMessage);
        existing.HandlerConfig.ShouldNotContain("apiCall");
        await _skillRepository.DidNotReceive().UpdateAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateAgentSkill_WithNavigationSteps_IsStillUpdated()
    {
        var existing = new AgentSkill { Name = "open_settings", HandlerConfig = "{}" };
        _skillRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentSkill> { existing });
        var sut = new UpdateAgentSkillSkill(_skillRepository, _phraseRepository, _catalogRefresher);

        var result = await sut.ExecuteAsync(Context(), Parameters(("skillName", "open_settings"), ("handlerSteps", NavigateSteps)));

        result.Success.ShouldBeTrue();
        await _skillRepository.Received(1).UpdateAsync(existing, Arg.Any<CancellationToken>());
    }

    private static Dictionary<string, object> Parameters(params (string Key, object Value)[] entries)
    {
        return entries.ToDictionary(entry => entry.Key, entry => entry.Value);
    }
}
