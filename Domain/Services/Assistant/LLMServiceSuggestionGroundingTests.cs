// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for LLMService.ApplySuggestionGroundingAsync — the post-processing step that drops
/// ungrounded [SUGGESTIONS: ...] chips (parsed by LLMResponseBuilder with no DB check) once the
/// active recipe ask-step slot identifies which entity they should refer to. Verifies the no-op
/// paths (no slot, no suggestions, unknown slot) alongside the actual filtering.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceSuggestionGroundingTests
{
    private ISuggestionEntityNameReader _nameReader = null!;
    private LLMService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _nameReader = Substitute.For<ISuggestionEntityNameReader>();

        // Every dependency besides ISuggestionEntityNameReader is unused by
        // ApplySuggestionGroundingAsync (verified by reading the method body) — null! keeps this
        // test focused on the grounding seam instead of standing up the whole LLMService graph.
        _service = new LLMService(
            logger: Substitute.For<ILogger<LLMService>>(),
            providerOrchestrator: null!,
            conversationManager: null!,
            functionExecutor: null!,
            responseBuilder: null!,
            promptBuilder: null!,
            agentRepository: null!,
            contextAssemblyPipeline: null!,
            backgroundTaskService: null!,
            pendingConfirmationStore: null!,
            recipeEngine: null!,
            slotExtractor: null!,
            suggestionEntityNameReader: _nameReader);
    }

    [Test]
    public async Task DropsSuggestionsNotMatchingAnyRealName()
    {
        _nameReader.GetRealNamesForSlotAsync("contractName", Arg.Any<CancellationToken>())
            .Returns(new List<string> { "Vollzeit 160 BE" });
        var response = new LLMResponse { Suggestions = new List<string> { "Standardvertrag", "Vollzeit 160 BE" } };

        await _service.ApplySuggestionGroundingAsync(response, "contractName");

        response.Suggestions.ShouldBe(["Vollzeit 160 BE"]);
    }

    [Test]
    public async Task NoSlot_LeavesSuggestionsUntouched()
    {
        var response = new LLMResponse { Suggestions = new List<string> { "Anything" } };

        await _service.ApplySuggestionGroundingAsync(response, null);

        response.Suggestions.ShouldBe(["Anything"]);
        await _nameReader.DidNotReceive().GetRealNamesForSlotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoSuggestions_LeavesResponseUntouched()
    {
        var response = new LLMResponse { Suggestions = null };

        await _service.ApplySuggestionGroundingAsync(response, "contractName");

        response.Suggestions.ShouldBeNull();
        await _nameReader.DidNotReceive().GetRealNamesForSlotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownSlot_ReaderReturnsNull_LeavesSuggestionsUntouched()
    {
        _nameReader.GetRealNamesForSlotAsync("employeeName", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>?)null);
        var response = new LLMResponse { Suggestions = new List<string> { "Anything" } };

        await _service.ApplySuggestionGroundingAsync(response, "employeeName");

        response.Suggestions.ShouldBe(["Anything"]);
    }
}
