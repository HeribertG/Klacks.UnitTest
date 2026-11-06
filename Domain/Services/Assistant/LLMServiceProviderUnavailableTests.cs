// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A streamed turn whose model has no usable provider (fresh installation, provider not set up) ends with one
/// error event that carries the LLM_PROVIDER_UNAVAILABLE code, so the client can show a translated message that
/// points to the provider settings instead of the raw English text.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Providers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceProviderUnavailableTests
{
    private const string UserMessage = "Show me the employee.";

    [Test]
    public async Task StreamedTurn_WithoutProvider_EmitsOneErrorEventWithTheProviderUnavailableCode()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        var context = LLMServiceTurnHarness.Context(UserMessage);
        harness.ProviderFactory.GetProviderForModelAsync(context.ModelId!).Returns((ILLMProvider?)null);

        var chunks = await harness.StreamAsync(context);

        var error = chunks.Single(c => c.Type == SseChunkType.Error);
        error.ErrorCode.ShouldBe(AssistantChatErrorCodes.ProviderUnavailable);
        error.ErrorMessage.ShouldBe(AssistantStreamErrorMessages.ProviderUnavailable);
        chunks.ShouldNotContain(c => c.Type == SseChunkType.StreamStart);
    }

    [Test]
    public async Task StreamedTurn_WithProvider_EmitsNoErrorEvent()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.Script(LLMServiceTurnHarness.Text("Here she is."));

        var chunks = await harness.StreamAsync(LLMServiceTurnHarness.Context(UserMessage));

        chunks.ShouldNotContain(c => c.Type == SseChunkType.Error);
    }
}
