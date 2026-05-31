// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the reasoning-content fallback rule (the whole correctness surface of R2): a
/// reasoning model's reasoning_content is the answer ONLY when there is no content and no tool call;
/// with a tool call present, reasoning is discarded (thinking-before-tool-use) so chain-of-thought
/// never leaks into the chat on a tool-calling turn.
/// </summary>

using Klacks.Api.Infrastructure.Services.Assistant.Providers.Shared;

namespace Klacks.UnitTest.Application.Services;

[TestFixture]
public class ReasoningContentResolverTests
{
    [Test]
    public void ContentOnly_ReturnsContent()
        => ReasoningContentResolver.EffectiveContent("hello", null, false).ShouldBe("hello");

    [Test]
    public void ReasoningThenContent_PrefersContent()
        => ReasoningContentResolver.EffectiveContent("answer", "thinking", false).ShouldBe("answer");

    [Test]
    public void ReasoningOnly_ReturnsReasoning()
        => ReasoningContentResolver.EffectiveContent("", "the answer", false).ShouldBe("the answer");

    [Test]
    public void ReasoningOnly_NullContent_ReturnsReasoning()
        => ReasoningContentResolver.EffectiveContent(null, "the answer", false).ShouldBe("the answer");

    [Test]
    public void ToolCallWithReasoning_DiscardsReasoning()
        => ReasoningContentResolver.EffectiveContent("", "thinking before tool use", true).ShouldBe(string.Empty);

    [Test]
    public void ToolCallWithContent_DiscardsContentToo()
        => ReasoningContentResolver.EffectiveContent("stray", "thinking", true).ShouldBe(string.Empty);

    [Test]
    public void BothEmpty_ReturnsEmpty()
        => ReasoningContentResolver.EffectiveContent(null, null, false).ShouldBe(string.Empty);
}
