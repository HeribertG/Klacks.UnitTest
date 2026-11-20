// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant.Pseudonymization;

[TestFixture]
public class PseudonymReferenceGuardTests
{
    [Test]
    public void CallWithMarker_IsRejectedWithoutRunning_OthersPass()
    {
        var marked = new LLMFunctionCall
        {
            FunctionName = "add_note",
            Parameters = new Dictionary<string, object> { ["text"] = PseudonymizationConstants.UnknownReferenceMarker + "MA-0999" }
        };
        var nested = new LLMFunctionCall
        {
            FunctionName = "update_client",
            Parameters = new Dictionary<string, object>
            {
                ["filter"] = JsonSerializer.SerializeToElement(new { names = new[] { PseudonymizationConstants.UnknownReferenceMarker + "KL-0001" } })
            }
        };
        var clean = new LLMFunctionCall
        {
            FunctionName = "search_clients",
            Parameters = new Dictionary<string, object> { ["name"] = "MA-0017 Meier" }
        };

        var executable = PseudonymReferenceGuard.Apply([marked, nested, clean], NullLogger.Instance);

        executable.ShouldBe([clean]);
        foreach (var call in new[] { marked, nested })
        {
            call.IsRejectedUnknownPseudonym.ShouldBeTrue();
            call.IsRejectedWithoutRunning.ShouldBeTrue();
            call.Success.ShouldBeFalse();
            call.Result.ShouldBe(PseudonymizationConstants.UnknownReferenceRejectedResult);
        }
    }
}
