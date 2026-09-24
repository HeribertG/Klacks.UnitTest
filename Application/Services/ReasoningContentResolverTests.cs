// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the reasoning-content rule: a reasoning model's reasoning_content is NEVER the answer.
/// Content wins when present, a tool call discards both channels, and reasoning without content yields
/// an empty answer that is only flagged (ReasoningWithoutContent) for diagnostics - users once saw the
/// model's deliberation as Klacksy's reply because the channel was used as a fallback answer.
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
        answer.ReasoningWithoutContent.ShouldBeFalse();
    }

    [Test]
    public void ReasoningThenContent_PrefersContentAndIsNotFlagged()
    {
        var answer = ReasoningContentResolver.Resolve("answer", "thinking", false);

        answer.Content.ShouldBe("answer");
        answer.ReasoningWithoutContent.ShouldBeFalse();
    }

    [Test]
    public void ReasoningOnly_ReturnsEmptyAnswerAndIsFlagged()
    {
        var answer = ReasoningContentResolver.Resolve("", "We need to call manage_pending_notes? No tools.", false);

        answer.Content.ShouldBe(string.Empty);
        answer.ReasoningWithoutContent.ShouldBeTrue();
    }

    [Test]
    public void ReasoningOnly_NullContent_ReturnsEmptyAnswerAndIsFlagged()
    {
        var answer = ReasoningContentResolver.Resolve(null, "the answer", false);

        answer.Content.ShouldBe(string.Empty);
        answer.ReasoningWithoutContent.ShouldBeTrue();
    }

    [Test]
    public void ToolCallWithReasoning_DiscardsReasoningAndIsNotFlagged()
    {
        var answer = ReasoningContentResolver.Resolve("", "thinking before tool use", true);

        answer.Content.ShouldBe(string.Empty);
        answer.ReasoningWithoutContent.ShouldBeFalse();
    }

    [Test]
    public void ToolCallWithContent_DiscardsContentTooAndIsNotFlagged()
    {
        var answer = ReasoningContentResolver.Resolve("stray", "thinking", true);

        answer.Content.ShouldBe(string.Empty);
        answer.ReasoningWithoutContent.ShouldBeFalse();
    }

    [Test]
    public void BothEmpty_ReturnsEmptyAndIsNotFlagged()
    {
        var answer = ReasoningContentResolver.Resolve(null, null, false);

        answer.Content.ShouldBe(string.Empty);
        answer.ReasoningWithoutContent.ShouldBeFalse();
    }
}
