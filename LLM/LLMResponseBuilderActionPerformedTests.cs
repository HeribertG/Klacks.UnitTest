// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ActionPerformed of the chat response means that at least one call of the turn succeeded. A failed call, a
/// held confirmation and a call rejected without running performed nothing, so a turn made only of those no
/// longer reports an action. A successful navigation call stays an action, because the client opens the page
/// only when it receives navigateTo together with actionPerformed.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;

namespace Klacks.UnitTest.LLM;

[TestFixture]
public class LLMResponseBuilderActionPerformedTests
{
    private const string Route = "/workplace/clients";

    private static LLMProviderResponse ProviderResponse() => new()
    {
        Content = string.Empty,
        Success = true,
        FunctionCalls = new List<LLMFunctionCall>(),
        Usage = new Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage()
    };

    private static bool ActionPerformedFor(params LLMFunctionCall[] calls) =>
        new LLMResponseBuilder()
            .BuildSuccessResponse(ProviderResponse(), "conv-1", "answer", calls.ToList())
            .ActionPerformed;

    [Test]
    public void NoCalls_NoAction()
    {
        ActionPerformedFor().ShouldBeFalse();
    }

    [Test]
    public void SuccessfulCall_IsAnAction()
    {
        ActionPerformedFor(new LLMFunctionCall { FunctionName = "create_contract", Success = true }).ShouldBeTrue();
    }

    [Test]
    public void OnlyAFailedCall_IsNoAction()
    {
        ActionPerformedFor(new LLMFunctionCall { FunctionName = "create_contract", Success = false }).ShouldBeFalse();
    }

    [Test]
    public void OnlyAHeldConfirmation_IsNoAction()
    {
        ActionPerformedFor(
            new LLMFunctionCall { FunctionName = "create_contract", Success = false, RequiresConfirmation = true })
            .ShouldBeFalse();
    }

    [Test]
    public void OnlyRejectedCalls_AreNoAction()
    {
        ActionPerformedFor(
                new LLMFunctionCall { FunctionName = "create_contract", Success = false, IsRejectedRepeat = true },
                new LLMFunctionCall { FunctionName = "delete_client", Success = false, IsRejectedOutsideToolset = true })
            .ShouldBeFalse();
    }

    [Test]
    public void PartialSuccess_IsAnAction()
    {
        ActionPerformedFor(
                new LLMFunctionCall { FunctionName = "create_contract", Success = false },
                new LLMFunctionCall { FunctionName = "update_contract", Success = true })
            .ShouldBeTrue();
    }

    [Test]
    public void SuccessfulNavigation_StaysAnAction_SoTheClientOpensThePage()
    {
        var navigation = new LLMFunctionCall { FunctionName = "navigate_to", Success = true, IsNavigation = true };

        var response = new LLMResponseBuilder().BuildSuccessResponse(
            ProviderResponse(), "conv-1", "answer", new List<LLMFunctionCall> { navigation }, Route, null);

        response.ActionPerformed.ShouldBeTrue();
        response.NavigateTo.ShouldBe(Route);
    }

    [Test]
    public void CallsFallBackToTheProviderResponse_WhenNoTurnListIsGiven()
    {
        var provider = ProviderResponse();
        provider.FunctionCalls.Add(new LLMFunctionCall { FunctionName = "create_contract", Success = false });

        new LLMResponseBuilder().BuildSuccessResponse(provider, "conv-1", "answer").ActionPerformed.ShouldBeFalse();
    }
}
