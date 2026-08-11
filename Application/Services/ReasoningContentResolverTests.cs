// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the reasoning-content fallback rule: a reasoning model's reasoning_content is the
/// answer ONLY when there is no content and no tool call; with a tool call present, reasoning is
/// discarded (thinking-before-tool-use) so chain-of-thought never leaks into the chat on a
/// tool-calling turn. Every case also asserts FromReasoning, because callers that must never show
/// raw chain-of-thought (the opening greeting) reject an answer on that flag alone.
/// </summary>

using Klacks.Api.Infrastructure.Services.Assistant.Providers.Shared;

namespace Klacks.UnitTest.Application.Services;

[TestFixture]
public class ReasoningContentResolverTests
{
    [Test]
    public void ContentOnly_ReturnsContentAndIsNotFlagged()
    {
        var answer = ReasoningContentResolver.Resolve("hello", null, false);

        answer.Content.ShouldBe("hello");
        answer.FromReasoning.ShouldBeFalse();
    }

    [Test]
    public void ReasoningThenContent_PrefersContentAndIsNotFlagged()
    {
        var answer = ReasoningContentResolver.Resolve("answer", "thinking", false);

        answer.Content.ShouldBe("answer");
        answer.FromReasoning.ShouldBeFalse();
    }

    [Test]
    public void ReasoningOnly_ReturnsReasoningAndIsFlagged()
    {
        var answer = ReasoningContentResolver.Resolve("", "the answer", false);

        answer.Content.ShouldBe("the answer");
        answer.FromReasoning.ShouldBeTrue();
    }

    [Test]
    public void ReasoningOnly_NullContent_ReturnsReasoningAndIsFlagged()
    {
        var answer = ReasoningContentResolver.Resolve(null, "the answer", false);

        answer.Content.ShouldBe("the answer");
        answer.FromReasoning.ShouldBeTrue();
    }

    [Test]
    public void ToolCallWithReasoning_DiscardsReasoningAndIsNotFlagged()
    {
        var answer = ReasoningContentResolver.Resolve("", "thinking before tool use", true);

        answer.Content.ShouldBe(string.Empty);
        answer.FromReasoning.ShouldBeFalse();
    }

    [Test]
    public void ToolCallWithContent_DiscardsContentTooAndIsNotFlagged()
    {
        var answer = ReasoningContentResolver.Resolve("stray", "thinking", true);

        answer.Content.ShouldBe(string.Empty);
        answer.FromReasoning.ShouldBeFalse();
    }

    [Test]
    public void BothEmpty_ReturnsEmptyAndIsNotFlagged()
    {
        var answer = ReasoningContentResolver.Resolve(null, null, false);

        answer.Content.ShouldBe(string.Empty);
        answer.FromReasoning.ShouldBeFalse();
    }
}
