// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for PlanningAgent — focused on the 'reversible' parsing rule: the LLM-supplied
/// 'reversible' field may only downgrade a whitelisted skill to non-reversible, never upgrade a
/// non-whitelisted skill to reversible. IAgentRepository, IAgentSkillRepository,
/// ICheapestModelResolver and ILLMProvider are mocked; the LLM response content is injected
/// verbatim to drive the private JSON parsing path via CreatePlanAsync. A skill that shows a server preview before it
/// runs (IConfirmationPreviewCatalog) can only run in the chat after the user confirmed it, so it is not offered to the
/// planner at all.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Planning;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Services.Assistant.Planning;

[TestFixture]
public class PlanningAgentTests
{
    private const string WhitelistedSkill = "create_shift";
    private const string NonWhitelistedSkill = "delete_all_clients";

    private IAgentRepository _agentRepository = null!;
    private IAgentSkillRepository _skillRepository = null!;
    private ICheapestModelResolver _cheapestModelResolver = null!;
    private ILLMProvider _provider = null!;
    private IConfirmationPreviewCatalog _previewCatalog = null!;
    private PlanningAgent _sut = null!;

    [SetUp]
    public void Setup()
    {
        _agentRepository = Substitute.For<IAgentRepository>();
        _skillRepository = Substitute.For<IAgentSkillRepository>();
        _cheapestModelResolver = Substitute.For<ICheapestModelResolver>();
        _provider = Substitute.For<ILLMProvider>();

        var agent = new Agent { Id = Guid.NewGuid(), Name = "klacks-default", IsDefault = true };
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(agent);

        var skills = new List<AgentSkill>
        {
            new() { Id = Guid.NewGuid(), AgentId = agent.Id, Name = WhitelistedSkill, IsEnabled = true },
            new() { Id = Guid.NewGuid(), AgentId = agent.Id, Name = NonWhitelistedSkill, IsEnabled = true }
        };
        _skillRepository.GetEnabledAsync(agent.Id, Arg.Any<CancellationToken>()).Returns(skills);

        var model = new LLMModel
        {
            Id = Guid.NewGuid(),
            ModelId = "cheap-model",
            ModelName = "Cheap Model",
            ApiModelId = "cheap-model-api-id",
            ProviderId = "test-provider",
            IsEnabled = true
        };
        _cheapestModelResolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns((model, _provider));

        _previewCatalog = Substitute.For<IConfirmationPreviewCatalog>();
        _previewCatalog.HasPreview(Arg.Any<string>()).Returns(false);
        _sut = new PlanningAgent(
            _agentRepository, _skillRepository, _cheapestModelResolver, _previewCatalog, NullLogger<PlanningAgent>.Instance);
    }

    private void SetLlmResponse(string skillName, bool? reversibleField)
    {
        var reversibleFragment = reversibleField is null
            ? string.Empty
            : ",\"reversible\":" + (reversibleField.Value ? "true" : "false");
        var content = "{\"steps\":[{\"order\":1,\"skill\":\"" + skillName + "\",\"params\":{}," +
                      "\"verifySkill\":null" + reversibleFragment + "}]}";
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = content });
    }

    private static List<PlanStep> DeserializeSteps(AgentPlan plan) =>
        JsonSerializer.Deserialize<List<PlanStep>>(plan.StepsJson) ?? new List<PlanStep>();

    [Test]
    public async Task CreatePlanAsync_LlmUpgradesNonWhitelistedSkillToReversible_StaysNonReversible()
    {
        SetLlmResponse(NonWhitelistedSkill, reversibleField: true);

        var plan = await _sut.CreatePlanAsync("goal", "user-1", null);

        Assert.That(plan.LastErrorMessage, Is.Null, "CreatePlanAsync must not have swallowed an exception");
        var steps = DeserializeSteps(plan);
        Assert.That(steps, Has.Count.EqualTo(1));
        Assert.That(steps[0].Skill, Is.EqualTo(NonWhitelistedSkill));
        Assert.That(steps[0].Reversible, Is.False,
            "LLM must not be able to upgrade a non-whitelisted skill to reversible");
    }

    [Test]
    public async Task CreatePlanAsync_LlmDowngradesWhitelistedSkillToNonReversible_BecomesNonReversible()
    {
        SetLlmResponse(WhitelistedSkill, reversibleField: false);

        var plan = await _sut.CreatePlanAsync("goal", "user-1", null);

        Assert.That(plan.LastErrorMessage, Is.Null, "CreatePlanAsync must not have swallowed an exception");
        var steps = DeserializeSteps(plan);
        Assert.That(steps, Has.Count.EqualTo(1));
        Assert.That(steps[0].Skill, Is.EqualTo(WhitelistedSkill));
        Assert.That(steps[0].Reversible, Is.False,
            "LLM must be able to downgrade a whitelisted skill to non-reversible");
    }

    [Test]
    public async Task CreatePlanAsync_ReversibleFieldMissingForWhitelistedSkill_IsReversible()
    {
        SetLlmResponse(WhitelistedSkill, reversibleField: null);

        var plan = await _sut.CreatePlanAsync("goal", "user-1", null);

        Assert.That(plan.LastErrorMessage, Is.Null, "CreatePlanAsync must not have swallowed an exception");
        var steps = DeserializeSteps(plan);
        Assert.That(steps, Has.Count.EqualTo(1));
        Assert.That(steps[0].Reversible, Is.True,
            "the allowlist alone decides reversibility when the LLM omits the field");
    }

    [Test]
    public async Task CreatePlanAsync_ReversibleFieldMissingForNonWhitelistedSkill_IsNonReversible()
    {
        SetLlmResponse(NonWhitelistedSkill, reversibleField: null);

        var plan = await _sut.CreatePlanAsync("goal", "user-1", null);

        Assert.That(plan.LastErrorMessage, Is.Null, "CreatePlanAsync must not have swallowed an exception");
        var steps = DeserializeSteps(plan);
        Assert.That(steps, Has.Count.EqualTo(1));
        Assert.That(steps[0].Reversible, Is.False,
            "a non-whitelisted skill stays non-reversible when the LLM omits the field");
    }

    [Test]
    public async Task CreatePlanAsync_DoesNotOfferASkillWithAServerPreviewToThePlanner()
    {
        const string PreviewedSkill = "bulk_delete_works";
        var agentId = (await _agentRepository.GetDefaultAgentAsync(CancellationToken.None))!.Id;
        var skills = new List<AgentSkill>
        {
            new() { Id = Guid.NewGuid(), AgentId = agentId, Name = WhitelistedSkill, IsEnabled = true },
            new() { Id = Guid.NewGuid(), AgentId = agentId, Name = NonWhitelistedSkill, IsEnabled = true },
            new() { Id = Guid.NewGuid(), AgentId = agentId, Name = PreviewedSkill, IsEnabled = true }
        };
        _skillRepository.GetEnabledAsync(agentId, Arg.Any<CancellationToken>()).Returns(skills);
        _previewCatalog.HasPreview(PreviewedSkill).Returns(true);
        LLMProviderRequest? sent = null;
        _provider.ProcessAsync(Arg.Do<LLMProviderRequest>(request => sent = request), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = "{\"steps\":[]}" });

        await _sut.CreatePlanAsync("delete all works of Max in November", "user-1", null);

        sent.ShouldNotBeNull();
        sent.Message.ShouldNotContain(PreviewedSkill);
        sent.Message.ShouldContain(WhitelistedSkill);
        sent.Message.ShouldContain(NonWhitelistedSkill);
        sent.Message.ShouldContain("AVAILABLE SKILLS (2 total)");
    }
}
