// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The streaming chat loop feeds the complete calls of a non-streaming provider into the accumulator. The
/// former inline code keyed every call by the finalized-call count, which is zero until finalization, so
/// two calls of one response were merged into a single call with a concatenated name. Each call must keep
/// its own index, name and arguments.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class StreamAccumulatorCompleteCallsTests
{
    [Test]
    public void TwoCompleteCalls_StayTwoCallsWithTheirOwnArguments()
    {
        var accumulator = new StreamAccumulator();

        var any = accumulator.AppendCompleteFunctionCalls(new[]
        {
            new LLMFunctionCall
            {
                FunctionName = "get_employee",
                Parameters = new Dictionary<string, object> { ["name"] = "Anna" }
            },
            new LLMFunctionCall { FunctionName = "list_groups", Parameters = new Dictionary<string, object>() }
        });
        accumulator.FinalizeFunctionCalls();

        any.ShouldBeTrue();
        accumulator.FunctionCalls.Select(call => call.FunctionName).ShouldBe(new[] { "get_employee", "list_groups" });
        ((JsonElement)accumulator.FunctionCalls[0].Parameters["name"]).GetString().ShouldBe("Anna");
        accumulator.FunctionCalls[1].Parameters.ShouldBeEmpty();
    }

    [Test]
    public void NoCalls_ReportsNone()
    {
        var accumulator = new StreamAccumulator();

        accumulator.AppendCompleteFunctionCalls(Array.Empty<LLMFunctionCall>()).ShouldBeFalse();
        accumulator.FinalizeFunctionCalls();

        accumulator.HasFunctionCalls.ShouldBeFalse();
    }
}
