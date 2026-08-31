// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// W1.7: llm_usage.functions_called is a JSON array of the function names the turn actually invoked,
/// distinct and capped to the varchar(200) column so the row always stays writable.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceSerializeFunctionsCalledTests
{
    [Test]
    public void EmptyCallList_SerializesToEmptyArray()
    {
        LLMService.SerializeFunctionsCalled([]).ShouldBe("[]");
    }

    [Test]
    public void Calls_SerializesDistinctNamesCaseInsensitive()
    {
        var json = LLMService.SerializeFunctionsCalled(
        [
            new LLMFunctionCall { FunctionName = "list_open_shifts" },
            new LLMFunctionCall { FunctionName = "LIST_OPEN_SHIFTS" },
            new LLMFunctionCall { FunctionName = "cut_shift" }
        ]);

        var names = JsonSerializer.Deserialize<List<string>>(json);
        names.ShouldBe(["list_open_shifts", "cut_shift"]);
    }

    [Test]
    public void MoreNamesThanTheColumnWidth_AreTrimmedUntilItFits()
    {
        var calls = Enumerable.Range(0, 40)
            .Select(i => new LLMFunctionCall { FunctionName = $"skill_with_a_long_name_number_{i:00}" })
            .ToList();

        var json = LLMService.SerializeFunctionsCalled(calls);

        json.Length.ShouldBeLessThanOrEqualTo(200);
        var names = JsonSerializer.Deserialize<List<string>>(json);
        names.ShouldNotBeNull();
        names!.Count.ShouldBeLessThan(40);
    }
}
