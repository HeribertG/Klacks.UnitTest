// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for LLMService's tool-definition reserve and history-budget arithmetic:
/// EstimateToolDefinitionReserveTokens (measured toolset size plus safety margin, replacing the old
/// flat ToolDefinitionReserveTokens constant) and ComputeHistoryBudget (the final per-turn history
/// token ceiling derived from it).
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceHistoryBudgetTests
{
    private const int EffectiveInputLimit = 200_000;
    private const int MaxOutputTokens = 8_000;
    private const int SystemPromptTokens = 1_000;
    private const int OldFlatReserveTokens = 15_000;

    private static LLMFunction MakeFunction(string name, string description, int parameterCount)
    {
        var parameters = new Dictionary<string, object>();
        var required = new List<string>();
        for (var i = 0; i < parameterCount; i++)
        {
            var paramName = $"param{i}";
            parameters[paramName] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = $"Description for {paramName}"
            };
            required.Add(paramName);
        }

        return new LLMFunction
        {
            Name = name,
            Description = description,
            Parameters = parameters,
            RequiredParameters = required
        };
    }

    private static int ExpectedRawTokens(LLMFunction function)
    {
        var json = JsonSerializer.Serialize(new
        {
            name = function.Name,
            description = function.Description,
            parameters = new
            {
                type = "object",
                properties = function.Parameters,
                required = function.RequiredParameters
            }
        });
        return json.Length / LLMService.CharsPerToken;
    }

    [Test]
    public void EstimateToolDefinitionReserveTokens_NullFunctions_ReturnsZero()
    {
        var result = LLMService.EstimateToolDefinitionReserveTokens(null);

        result.ShouldBe(0);
    }

    [Test]
    public void EstimateToolDefinitionReserveTokens_EmptyList_ReturnsZero()
    {
        var result = LLMService.EstimateToolDefinitionReserveTokens(new List<LLMFunction>());

        result.ShouldBe(0);
    }

    [Test]
    public void EstimateToolDefinitionReserveTokens_SingleFunction_AppliesConfiguredSafetyMargin()
    {
        var function = MakeFunction(
            "get_current_time", "Returns the current date and time in the user's timezone.", parameterCount: 1);
        var rawTokens = ExpectedRawTokens(function);
        var expected = rawTokens + (rawTokens * LLMService.ToolDefinitionSafetyMarginPercent / 100);

        var result = LLMService.EstimateToolDefinitionReserveTokens(new List<LLMFunction> { function });

        result.ShouldBe(expected);
        result.ShouldBeGreaterThan(rawTokens);
    }

    [Test]
    public void EstimateToolDefinitionReserveTokens_RealisticTier12Toolset_IsFarBelowOldFlatReserve()
    {
        var functions = Enumerable.Range(0, 12)
            .Select(i => MakeFunction($"skill_{i}", $"Handles operation number {i} for the assistant.", parameterCount: 2))
            .ToList();

        var result = LLMService.EstimateToolDefinitionReserveTokens(functions);

        result.ShouldBeLessThan(OldFlatReserveTokens);
    }

    [Test]
    public void EstimateToolDefinitionReserveTokens_WorstCase30MaximalSkills_ExceedsOldFlatReserve()
    {
        var maximalDescription = new string('x', 1_200);
        var functions = Enumerable.Range(0, 30)
            .Select(i => MakeFunction($"large_skill_{i}", maximalDescription, parameterCount: 12))
            .ToList();

        var result = LLMService.EstimateToolDefinitionReserveTokens(functions);

        result.ShouldBeGreaterThan(OldFlatReserveTokens);
    }

    [Test]
    public void EstimateToolDefinitionReserveTokens_GrowsMonotonicallyWithMoreFunctions()
    {
        var oneFunction = new List<LLMFunction>
        {
            MakeFunction("skill_a", "First skill description.", parameterCount: 1)
        };
        var twoFunctions = new List<LLMFunction>
        {
            MakeFunction("skill_a", "First skill description.", parameterCount: 1),
            MakeFunction("skill_b", "Second skill description.", parameterCount: 1)
        };

        var oneResult = LLMService.EstimateToolDefinitionReserveTokens(oneFunction);
        var twoResult = LLMService.EstimateToolDefinitionReserveTokens(twoFunctions);

        twoResult.ShouldBeGreaterThan(oneResult);
    }

    [Test]
    public void ComputeHistoryBudget_ZeroToolDefinitionTokens_OnlyReservesOutputSystemAndFixedOverhead()
    {
        var expected = EffectiveInputLimit - MaxOutputTokens - SystemPromptTokens
            - LLMService.InteractionHeadroomTokens - LLMService.SafetyMarginTokens;

        var result = LLMService.ComputeHistoryBudget(EffectiveInputLimit, MaxOutputTokens, SystemPromptTokens, toolDefinitionTokens: 0);

        result.ShouldBe(expected);
    }

    [Test]
    public void ComputeHistoryBudget_NonZeroToolDefinitionTokens_ReducesBudgetByExactAmount()
    {
        const int toolDefinitionTokens = 6_886;

        var withoutTools = LLMService.ComputeHistoryBudget(EffectiveInputLimit, MaxOutputTokens, SystemPromptTokens, toolDefinitionTokens: 0);
        var withTools = LLMService.ComputeHistoryBudget(EffectiveInputLimit, MaxOutputTokens, SystemPromptTokens, toolDefinitionTokens);

        (withoutTools - withTools).ShouldBe(toolDefinitionTokens);
    }

    [Test]
    public void ComputeHistoryBudget_ReservationsExceedLimit_ClampsToMinHistoryBudgetTokens()
    {
        var result = LLMService.ComputeHistoryBudget(
            effectiveInputLimit: 10_000,
            maxOutputTokens: MaxOutputTokens,
            systemPromptTokens: SystemPromptTokens,
            toolDefinitionTokens: 50_000);

        result.ShouldBe(LLMService.MinHistoryBudgetTokens);
    }

    [Test]
    public void ComputeHistoryBudget_HugeToolDefinitionTokens_NeverGoesBelowFloorRegardlessOfMagnitude()
    {
        var resultAtOneMillion = LLMService.ComputeHistoryBudget(EffectiveInputLimit, MaxOutputTokens, SystemPromptTokens, toolDefinitionTokens: 1_000_000);
        var resultAtTenMillion = LLMService.ComputeHistoryBudget(EffectiveInputLimit, MaxOutputTokens, SystemPromptTokens, toolDefinitionTokens: 10_000_000);

        resultAtOneMillion.ShouldBe(LLMService.MinHistoryBudgetTokens);
        resultAtTenMillion.ShouldBe(LLMService.MinHistoryBudgetTokens);
    }

    [Test]
    public void EndToEnd_RealisticTier30Toolset_HistoryBudgetGainsOverOldFlatReserve()
    {
        var functions = Enumerable.Range(0, 30)
            .Select(i => MakeFunction(
                $"skill_{i}", $"Performs scheduling operation {i} for shifts, clients and absences.", parameterCount: 3))
            .ToList();

        var newReserve = LLMService.EstimateToolDefinitionReserveTokens(functions);
        var budgetWithNewReserve = LLMService.ComputeHistoryBudget(EffectiveInputLimit, MaxOutputTokens, SystemPromptTokens, newReserve);
        var budgetWithOldFlatReserve = LLMService.ComputeHistoryBudget(EffectiveInputLimit, MaxOutputTokens, SystemPromptTokens, OldFlatReserveTokens);

        newReserve.ShouldBeLessThan(OldFlatReserveTokens);
        budgetWithNewReserve.ShouldBeGreaterThan(budgetWithOldFlatReserve);
    }
}
